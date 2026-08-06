using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Chunking;

/// <summary>
/// Một khối các dòng prompt gửi cho mô hình. <see cref="CandidateIndexes"/> là những đoạn mô hình PHẢI
/// trả lời; các ứng viên khác vẫn nằm trong <see cref="Lines"/> làm ngữ cảnh nhưng không bị hỏi.
/// </summary>
public sealed record XmlChunk(int Number, IReadOnlyList<XmlLine> Lines, IReadOnlyList<int> CandidateIndexes);

/// <summary>
/// Cắt danh sách dòng prompt thành các khối vừa cửa sổ ngữ cảnh của mô hình,
/// có chồng lấn vài ứng viên để không mất tiêu đề ở mép khối.
/// </summary>
public static class SlimXmlChunker
{
    /// <summary>
    /// Ước lượng DỰ PHÒNG, chỉ dùng khi không có tokenizer thật.
    /// <para>
    /// ĐO ĐƯỢC bằng tokenizer Qwen2.5-7B: prompt cố định (tiếng Anh + markup + GBNF) đạt
    /// 3.10 ký tự/token, nhưng thân bài tiếng Việt chỉ 1.85 và một tài liệu khác 2.29 — mỗi âm
    /// tiết có dấu tốn nhiều token hơn hẳn. Lấy 3.0 cho toàn bộ nghĩa là khối vượt ngân sách tới
    /// 62% ở phần tiếng Việt dày, mà chi phí attention thì bậc hai theo độ dài: đo được khối đầu
    /// 44 s trong khi khối giữa tài liệu tốn 128–153 s.
    /// </para>
    /// <para>
    /// Vì vậy hằng số này lấy theo phía AN TOÀN (tiếng Việt dày nhất đo được), không lấy trung
    /// bình: ước lượng thấp làm tràn ngữ cảnh, ước lượng cao chỉ làm khối nhỏ hơn cần thiết.
    /// Đường đi đúng vẫn là truyền <c>countTokens</c> để đếm thật.
    /// </para>
    /// </summary>
    public const double CharsPerToken = 1.85;

    /// <summary>
    /// Trần số ứng viên mỗi khối. Ở chế độ grammar liệt kê, độ dài đầu ra tỉ lệ thuận với
    /// số ứng viên, nên trần này giữ cho prompt + đầu ra luôn nằm gọn trong cửa sổ ngữ cảnh.
    /// </summary>
    public const int DefaultMaxCandidatesPerChunk = 40;

    /// <param name="shouldAsk">
    /// Đoạn nào cần mô hình trả lời. Null = hỏi mọi ứng viên. Đoạn không được hỏi vẫn nằm trong
    /// khối làm ngữ cảnh, nhưng không tính vào trần <paramref name="maxCandidatesPerChunk"/> và
    /// không vào grammar — nhờ đó độ dài đầu ra giảm theo đúng số câu hỏi thật sự cần hỏi.
    /// </param>
    /// <param name="countTokens">
    /// Đếm token THẬT bằng tokenizer của chính mô hình sẽ chạy. Null thì rơi về ước lượng
    /// <see cref="CharsPerToken"/> — chấp nhận được cho test, nhưng production nên truyền vào:
    /// ngân sách chỉ có nghĩa khi đơn vị của nó đúng là đơn vị mà cửa sổ ngữ cảnh dùng.
    /// </param>
    public static IReadOnlyList<XmlChunk> Split(
        IReadOnlyList<XmlLine> lines,
        int maxTokensPerChunk,
        int overlapCandidates = 2,
        int maxCandidatesPerChunk = DefaultMaxCandidatesPerChunk,
        Func<int, bool>? shouldAsk = null,
        Func<string, int>? countTokens = null)
    {
        bool Asked(XmlLine l) =>
            l.IsCandidate && l.ParagraphIndex is { } i && (shouldAsk is null || shouldAsk(i));

        // +1 cho ký tự xuống dòng nối các dòng lại thành khối.
        int Cost(XmlLine l) => countTokens is null
            ? (int)Math.Ceiling((l.Text.Length + 1) / CharsPerToken)
            : countTokens(l.Text) + 1;

        var chunks = new List<XmlChunk>();
        var current = new List<XmlLine>();
        int currentTokens = 0, currentAsked = 0;

        void Close()
        {
            var asked = current.Where(Asked).Select(l => l.ParagraphIndex!.Value).ToArray();
            chunks.Add(new XmlChunk(chunks.Count + 1, current, asked));
        }

        foreach (var line in lines)
        {
            var cost = Cost(line);
            var wouldExceedTokens = currentTokens + cost > maxTokensPerChunk;
            // maxCandidatesPerChunk <= 0 nghĩa là KHÔNG chặn theo số ứng viên: để ngân sách token
            // quyết định một mình. Dùng khi không muốn một hằng số cố định áp lên mọi tài liệu.
            var wouldExceedCandidates = maxCandidatesPerChunk > 0
                && Asked(line) && currentAsked >= maxCandidatesPerChunk;

            if ((wouldExceedTokens || wouldExceedCandidates) && current.Count > 0)
            {
                Close();
                var carry = TakeTailOverlap(current, overlapCandidates, Asked);

                // Chồng lấn không được chiếm quá nửa ngân sách, nếu không khối mới sinh ra đã gần
                // đầy và dòng kế tiếp lại cắt tiếp — thành mỗi dòng một khối. ĐO ĐƯỢC: tài liệu
                // 344 đoạn chỉ có 13 ứng viên nên phần đuôi phải lùi hàng trăm dòng mới gom đủ 2
                // ứng viên, sinh ra 138 khối. Trần 1/2 để ca bình thường (ứng viên dày, đuôi ngắn)
                // không bị đụng tới: chồng lấn là thứ đã sửa được lỗi trượt cấp giữa hai khối.
                var carryCap = Math.Max(1, maxTokensPerChunk / 2);
                var carryTokens = carry.Sum(Cost);
                var carryAsked = carry.Count(Asked);

                while (carry.Count > 1 && carryTokens > carryCap)
                {
                    // Bỏ dần từ đầu, nhưng ứng viên CUỐI CÙNG thì giữ: mất nó là mất luôn chồng
                    // lấn, mà chồng lấn chính là thứ cho phép bỏ phiếu cấp giữa hai khối liền kề.
                    // Lúc đó chuyển sang tỉa dòng ngữ cảnh nằm sau nó, thay vì dừng hẳn — dừng hẳn
                    // là bug: carry giữ nguyên kích cỡ và mỗi dòng lại thành một khối.
                    var drop = 0;
                    if (Asked(carry[0]) && carryAsked <= 1)
                    {
                        drop = carry.FindIndex(1, l => !Asked(l));
                        if (drop < 0) break;
                    }

                    if (Asked(carry[drop])) carryAsked--;
                    carryTokens -= Cost(carry[drop]);
                    carry.RemoveAt(drop);
                }

                current = carry;
                currentTokens = carryTokens;
                currentAsked = carry.Count(Asked);
            }

            current.Add(line);
            currentTokens += cost;
            if (Asked(line)) currentAsked++;
        }

        // Khối cuối chỉ đáng gửi đi nếu còn câu hỏi nào trong đó.
        if (current.Count > 0 && (current.Any(Asked) || chunks.Count == 0))
            Close();

        return [.. chunks.Where(c => c.CandidateIndexes.Count > 0)];
    }

    /// <summary>Số dòng ngữ cảnh giữ kèm trước mỗi ứng viên chồng lấn.</summary>
    private const int OverlapContextLines = 2;

    /// <summary>
    /// Phần đuôi mang sang khối sau: các ứng viên chồng lấn kèm vài dòng ngữ cảnh sát trước chúng.
    /// <para>
    /// Chồng lấn tồn tại để hai khối liền kề cùng chấm MỘT ứng viên rồi bỏ phiếu cấp — nó không
    /// cần mang theo toàn bộ đoạn văn nằm giữa hai ứng viên. Bản cũ lấy nguyên khúc đuôi từ ứng
    /// viên thứ <c>candidates</c> tính từ cuối, nên với tài liệu ứng viên thưa (ĐO ĐƯỢC: 13 ứng
    /// viên trên 344 đoạn) khúc đó dài gần bằng cả khối: khối mới sinh ra đã đầy, dòng kế tiếp
    /// lại cắt tiếp, sinh ra 138 khối. Giữ theo ứng viên thay vì theo khoảng liên tục làm kích cỡ
    /// đuôi bị chặn bởi <c>candidates × (1 + OverlapContextLines)</c>, không phụ thuộc mật độ.
    /// </para>
    /// </summary>
    private static List<XmlLine> TakeTailOverlap(List<XmlLine> lines, int candidates, Func<XmlLine, bool> asked)
    {
        if (candidates <= 0) return [];

        var keep = new SortedSet<int>();
        var seen = 0;
        for (var i = lines.Count - 1; i >= 0 && seen < candidates; i--)
        {
            if (!asked(lines[i])) continue;
            seen++;
            keep.Add(i);
            for (var c = 1; c <= OverlapContextLines && i - c >= 0; c++) keep.Add(i - c);
        }

        return [.. keep.Select(i => lines[i])];
    }
}
