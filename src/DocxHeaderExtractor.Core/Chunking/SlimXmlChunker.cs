using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Chunking;

/// <summary>
/// Một khối XML gửi cho mô hình. <see cref="CandidateIndexes"/> là những đoạn mô hình PHẢI
/// trả lời; các ứng viên khác vẫn nằm trong <see cref="Lines"/> làm ngữ cảnh nhưng không bị hỏi.
/// </summary>
public sealed record XmlChunk(int Number, IReadOnlyList<XmlLine> Lines, IReadOnlyList<int> CandidateIndexes);

/// <summary>
/// Cắt danh sách dòng XML thành các khối vừa cửa sổ ngữ cảnh của mô hình,
/// có chồng lấn vài ứng viên để không mất tiêu đề ở mép khối.
/// </summary>
public static class SlimXmlChunker
{
    /// <summary>Ước lượng thô: 1 token ≈ 3 ký tự cho hỗn hợp tiếng Việt + markup.</summary>
    public const double CharsPerToken = 3.0;

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
    public static IReadOnlyList<XmlChunk> Split(
        IReadOnlyList<XmlLine> lines,
        int maxTokensPerChunk,
        int overlapCandidates = 2,
        int maxCandidatesPerChunk = DefaultMaxCandidatesPerChunk,
        Func<int, bool>? shouldAsk = null)
    {
        bool Asked(XmlLine l) =>
            l.IsCandidate && l.ParagraphIndex is { } i && (shouldAsk is null || shouldAsk(i));

        var maxChars = (int)(maxTokensPerChunk * CharsPerToken);
        var chunks = new List<XmlChunk>();
        var current = new List<XmlLine>();
        int currentChars = 0, currentAsked = 0;

        void Close()
        {
            var asked = current.Where(Asked).Select(l => l.ParagraphIndex!.Value).ToArray();
            chunks.Add(new XmlChunk(chunks.Count + 1, current, asked));
        }

        foreach (var line in lines)
        {
            var cost = line.Text.Length + 1;
            var wouldExceedChars = currentChars + cost > maxChars;
            var wouldExceedCandidates = Asked(line) && currentAsked >= maxCandidatesPerChunk;

            if ((wouldExceedChars || wouldExceedCandidates) && current.Count > 0)
            {
                Close();
                var carry = TakeTailOverlap(current, overlapCandidates, Asked);
                current = [.. carry];
                currentChars = carry.Sum(l => l.Text.Length + 1);
                currentAsked = carry.Count(Asked);
            }

            current.Add(line);
            currentChars += cost;
            if (Asked(line)) currentAsked++;
        }

        // Khối cuối chỉ đáng gửi đi nếu còn câu hỏi nào trong đó.
        if (current.Count > 0 && (current.Any(Asked) || chunks.Count == 0))
            Close();

        return [.. chunks.Where(c => c.CandidateIndexes.Count > 0)];
    }

    private static List<XmlLine> TakeTailOverlap(List<XmlLine> lines, int candidates, Func<XmlLine, bool> asked)
    {
        if (candidates <= 0) return [];

        int seen = 0, start = lines.Count;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (asked(lines[i]))
            {
                seen++;
                start = i;
                if (seen >= candidates) break;
            }
        }

        return start >= lines.Count ? [] : lines.GetRange(start, lines.Count - start);
    }
}
