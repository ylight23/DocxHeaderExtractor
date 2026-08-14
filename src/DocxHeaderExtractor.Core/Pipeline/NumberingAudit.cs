using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Loại ký hiệu đánh số đứng đầu một tiêu đề.</summary>
public enum NumberKind
{
    None,
    Arabic,
    Roman,
    Letter,

    /// <summary>
    /// "Nhãn + số": <c>Chương 1.</c>, <c>PHẦN I.</c>, <c>Abschnitt 4.</c>. Nhãn là chữ đọc được từ
    /// chính tài liệu, KHÔNG phải danh sách từ khoá cài sẵn — cùng nguyên tắc với
    /// <c>HeadingHeuristics.LabelledNumberPrefixRx</c> đã thay <c>KeywordPrefixRx</c> ở §3.2.
    /// </summary>
    Labelled,
}

/// <summary>
/// Ký hiệu đánh số đã tách: <c>3.1.</c> → Arabic, Depth 2, Value 1. <c>IV.</c> → Roman, Depth 1, Value 4.
/// </summary>
public readonly record struct NumberToken(NumberKind Kind, int Depth, int Value, string Label = "")
{
    /// <summary>
    /// Hai tiêu đề cùng chữ ký thì phải cùng cấp — đó là bất biến mà kiểm tra này dựa vào.
    /// <para>
    /// Với <see cref="NumberKind.Labelled"/>, NHÃN phải nằm trong chữ ký. Nếu không, <c>Chương 1.</c>
    /// và <c>1.</c> trần cùng ra <c>Arabic:1</c> và <c>SignatureTiers</c> gộp hai tầng khác nhau làm
    /// một. Trước khi có kind này, <c>PHẦN I</c> nằm trên <c>1.</c> đúng chỉ vì TÌNH CỜ hai loại số
    /// khác nhau (La Mã vs Ả Rập); với <c>Chương 1.</c> và <c>1.1.</c> thì sự tình cờ đó không có.
    /// </para>
    /// </summary>
    public string Signature => Kind == NumberKind.Labelled
        ? $"Labelled({Label}):{Depth}"
        : $"{Kind}:{Depth}";
}

/// <summary>Một điểm đáng ngờ do hậu kiểm phát hiện, kèm các đoạn liên quan.</summary>
public sealed record AuditWarning(string Message, IReadOnlyList<int> Indexes);

/// <summary>
/// Hậu kiểm xác định (không gọi mô hình) dựa trên chính ký hiệu đánh số có sẵn trong tài liệu.
/// <para>
/// Grammar liệt kê bắt mô hình sinh một chữ số cấp cho mỗi ứng viên trong CÙNG một chuỗi tự hồi
/// quy, nên nó dễ khoá vào một nếp — đo được trên tài liệu thật: mô hình trả về dãy cấp
/// 1,2,3,4,5,6,7,8,9 tăng đều cho các mục vốn cùng một cấp. Thu nhỏ khối không chặn được
/// (6 ứng viên/khối vẫn trượt), vì đây là bản chất của cách sinh chứ không phải độ dài khối.
/// </para>
/// <para>
/// Ký hiệu đánh số thì do người soạn gõ ra, không phải mô hình suy đoán, nên dùng nó làm đối
/// chứng bắt được lỗi mà bản thân mô hình không thấy. Hai bất biến được kiểm:
/// cùng chữ ký ⇒ cùng cấp, và dãy số của các mục anh em phải bắt đầu từ 1 và liên tục.
/// </para>
/// </summary>
public static class NumberingAudit
{
    private sealed record AuditItem(HeadingRecord Heading, NumberToken Token, string Scope);

    // ── Quan hệ với HeadingHeuristics ────────────────────────────────────────────────────────
    // Hai file cùng đọc tiền tố đánh số nhưng KHÔNG cùng một hợp đồng, và đó là chủ đích:
    //
    //   HeadingHeuristics  — chạy TRƯỚC mô hình, quyết định "có đáng hỏi không". Sai theo hướng
    //                        rộng: bỏ sót một ứng viên là mất hẳn, vì mô hình không bao giờ thấy nó.
    //                        LƯU Ý: vế "sai theo hướng rộng" ĐÚNG với phần chấm điểm ở đây, nhưng
    //                        KHÔNG áp cho nhóm luật hạ cấp theo cấu trúc trong DocxSlimExtractor —
    //                        §21 đo được rằng nới chúng ra làm F1 tụt 90,8% → 78,4%.
    //   NumberingAudit     — chạy SAU mô hình, quyết định "dãy số này có nhất quán không". Sai theo
    //                        hướng hẹp: nhận nhầm "1: 03/04" là mục số 1 thì hậu kiểm sẽ báo thiếu
    //                        mục 2, 3 không hề tồn tại.
    //
    // Từ đó ba chỗ lệch dưới đây là CÓ CHỦ ĐÍCH, không phải quên đồng bộ:
    //   • HasTitleRemainder: chỉ file này đòi phần còn lại có một từ ≥2 chữ cái.
    //   • LetterRx nhận cả chữ thường; LetterPrefixRx bên kia chỉ nhận \p{Lu}.
    //   • RomanRx/LetterRx cho \s* sau dấu ngắt; bên kia đòi \s+.
    // Chỉ riêng mẫu số Ả Rập là giữ giống hệt nhau — lệch ở đó thì hậu kiểm sẽ nói về những mục mà
    // tầng chấm điểm chưa từng thấy.
    //
    // Mẫu "nhãn + số" ĐÃ CÓ (LabelledRx) từ TODO mục 3. Bản ở đây HẸP hơn bên HeadingHeuristics,
    // đúng theo hợp đồng ghi trên: audit sai theo hướng hẹp. Cụ thể là đòi dấu ngắt tường minh và
    // đòi phần còn lại bắt đầu bằng CHỮ — nếu không, "Bảng 1.2 Đối chiếu…" sẽ tách thành nhãn
    // "Bảng" + số 1 và hậu kiểm đi báo thiếu những mục không tồn tại.

    /// <summary>
    /// <c>1.</c>, <c>3.1.</c>, <c>2.3.4)</c>, kể cả <c>1.MUC</c> thiếu dấu cách.
    /// Giữ giống hệt <c>HeadingHeuristics.DecimalPrefixRx</c>.
    /// </summary>
    private static readonly Regex ArabicRx = new(
        @"^\s*(\d{1,2}(?:\.\d{1,2}){0,4})(?!\d)\s*(?:[\.\)\-–:]\s*|\s+)(\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex RomanRx = new(
        @"^\s*([IVXLCDM]{1,7})\s*[\.\)\-–:]\s*(\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex LetterRx = new(
        @"^\s*([A-Za-zĂÂĐÊÔƠƯăâđêôơư])\s*[\.\)]\s*(\S.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Nghị định 30/2020 quy định điểm đánh bằng "chữ cái tiếng Việt theo thứ tự bảng chữ cái
    // tiếng Việt". Bảng đó có đ ngay sau d và KHÔNG có f j w z. Hệ quả: d) → đ) → e) là liên
    // tục, còn d) → e) chỉ liên tục nếu tài liệu dùng thứ tự Latin.
    //
    // Không có một thứ tự cố định nào đúng cho cả hai. Chọn Latin thì mọi văn bản hành chính có
    // đ) bị báo đứt quãng sai; chọn tiếng Việt thì mọi tài liệu Latin có d) e) bị báo "thiếu đ)".
    // Cả hai đều là cảnh báo do ta tự tạo ra chứ không phải lỗi của tài liệu.
    //
    // Nên quyết định phải nhìn CẢ DÃY chứ không nhìn từng mục — cùng dạng với việc phân biệt số
    // La Mã thường với chữ cái. Ta chấm dãy theo từng thứ tự ứng viên rồi lấy thứ tự ít đứt quãng
    // nhất; hoà thì ưu tiên Latin vì nó phổ biến hơn trong corpus.
    private static readonly string[] LetterAlphabets =
    [
        "abcdefghijklmnopqrstuvwxyz",      // Latin
        "abcdđeghiklmnopqrstuvxy",         // tiếng Việt, biến thể quan sát được ở điểm văn bản
        "aăâbcdđeêghiklmnoôơpqrstuưvxy",   // tiếng Việt đầy đủ 29 chữ, đọc sát Nghị định 30
    ];

    /// <summary>
    /// Thứ tự hợp nhất: mọi chữ của cả ba bảng trên, xếp sao cho nếu x đứng trước y ở BẤT KỲ bảng
    /// nào thì x cũng đứng trước y ở đây. Nhờ vậy giá trị token vẫn đơn điệu tăng cho cả tài liệu
    /// Latin lẫn tiếng Việt, nên việc cắt dãy ở <see cref="CheckSequenceGaps"/> — vốn chỉ cần tính
    /// đơn điệu — đúng cho cả hai mà không phải biết trước tài liệu theo quy ước nào.
    /// </summary>
    private const string MergedLetterOrder = "aăâbcdđeêfghijklmnoôơpqrstuưvwxyz";

    private static int? LetterOrdinal(char c, string alphabet)
    {
        var at = alphabet.IndexOf(char.ToLowerInvariant(c));
        return at < 0 ? null : at + 1;
    }

    // Một prefix giống numbering chỉ mang ý nghĩa cấu trúc khi sau nó có nhãn ngôn ngữ.
    // Điều này loại các dòng số liệu kiểu "A: 04, B: 04" hoặc "1: 03/04" mà không cần
    // hardcode tên trường. Hai chữ cái liên tiếp vẫn chấp nhận viết tắt tổng quát.
    private static readonly Regex TitleWordRx = new(@"\p{L}{2,}", RegexOptions.Compiled);

    /// <summary>
    /// <c>Chương 1. Tổng quan</c>, <c>PHẦN I. Cơ sở</c> — và cả dạng KHÔNG có dấu ngắt:
    /// <c>Chương II QUY ĐỊNH CHUNG</c>.
    /// <para>
    /// Dạng không dấu ngắt là hệ quả trực tiếp của Nghị định 30/2020: từ "Chương" cùng số thứ tự
    /// nằm một dòng riêng, tiêu đề nằm dòng ngay dưới. Bản chuyển PDF→DOCX dán hai dòng lại thành
    /// <c>Chương II QUY ĐỊNH CHUNG</c>, không còn dấu chấm ở giữa. Hậu quả đo được trên
    /// <c>082_Bo_luat_Lao_dong_2019_EN</c>: 26 <c>Chapter</c> + 221 <c>Article</c> mà TẤT CẢ đều
    /// cấp 1 — vì <c>Chapter</c> không parse được nên tài liệu chỉ còn MỘT chữ ký, mà
    /// <see cref="StructuralHierarchyResolver"/> đòi từ hai chữ ký trở lên mới suy được quan hệ
    /// lồng nhau. Không tài liệu nào có 26 chương và 221 điều mà chỉ một cấp.
    /// </para>
    /// <para>
    /// <b>Chốt chặn của nhánh không-dấu-ngắt: phần còn lại phải KHÔNG có chữ thường nào.</b>
    /// Nghị định 30/2020 quy định tiêu đề phần và chương trình bày bằng <i>chữ in hoa, đậm</i>, nên
    /// đây là tín hiệu cấu trúc có căn cứ, không phải danh sách từ khoá.
    /// </para>
    /// <para>
    /// Chốt này chặn hai nhóm cùng lúc, cả hai đều đã có test:
    /// <list type="bullet">
    /// <item>Tham chiếu chéo giữa câu — <c>Điều 3 của Bộ luật này</c>, <c>khoản 2 Điều này</c>.</item>
    /// <item>Chú thích hình/bảng — <c>Bảng 3 Thống kê số liệu</c>, <c>Table 5 Summary Of Results</c>.
    /// Nhóm này nguy hiểm hơn: <see cref="StructuralRecovery"/> cứu MỌI đoạn có token
    /// <see cref="NumberKind.Labelled"/>, mà <c>bench --no-llm</c> không chạy tới đó nên phép đo
    /// thường dùng KHÔNG bắt được. Đúng cái bẫy mà chú thích ở đầu file đã cảnh báo.</item>
    /// </list>
    /// Đổi lại: <c>Chương II Quy định chung</c> (không in hoa) sẽ bị bỏ qua. Đó là hướng sai ĐÚNG
    /// với hợp đồng của file này — hậu kiểm sai theo hướng HẸP.
    /// </para>
    /// </summary>
    private static readonly Regex LabelledRx = new(
        @"^\s*(\p{Lu}[\p{L}]{1,11})\s+(\d{1,3}|[IVXLCDM]{1,7})(?:\s*[\.\):\-–]\s*|\s+(?=[^\p{Ll}]*$))(\p{L}.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Chạy hậu kiểm, đánh dấu <see cref="HeadingRecord.Disputed"/> cho các dòng lệch và trả về
    /// danh sách cảnh báo. Không tự sửa cấp: ký hiệu đánh số nói lên quan hệ anh em, không nói
    /// được cấp tuyệt đối (<c>I.</c> và <c>1.</c> cùng Depth 1 nhưng khác tầng), nên sửa mù dễ
    /// thay một lỗi bằng một lỗi khác. Việc của nó là chỉ đúng chỗ cần nhìn lại.
    /// </summary>
    /// <param name="document">
    /// Nguồn <see cref="SlimParagraph.NumberLabel"/> cho heading được Word tự đánh số. Bỏ trống thì
    /// hậu kiểm chỉ đọc được số gõ tay trong text — đủ cho unit test, thiếu cho tài liệu thật.
    /// </param>
    public static IReadOnlyList<AuditWarning> Run(
        IReadOnlyList<HeadingRecord> headings,
        SlimDocument? document = null,
        ExtractionOptions? options = null)
    {
        if (headings.Count == 0) return [];

        options ??= new ExtractionOptions();
        var ordered = headings.OrderBy(h => h.Index).ToList();
        var tokens = ordered
            .Select(h => (Heading: h, Token: ParseHeadingNumber(h, document?.ByIndex(h.Index), options)))
            .Where(x => x.Token is not null)
            .Select(x => new AuditItem(x.Heading, x.Token!.Value,
                ScopeKey(ordered, ordered.IndexOf(x.Heading), document)))
            .ToList();

        if (tokens.Count == 0) return [];

        var warnings = new List<AuditWarning>();
        warnings.AddRange(CheckLevelConsistency(tokens));
        warnings.AddRange(CheckSequenceGaps(tokens));
        return warnings;
    }

    /// <summary>Cùng chữ ký mà khác cấp ⇒ dòng lệch khỏi cấp phổ biến nhất là dòng đáng ngờ.</summary>
    private static IEnumerable<AuditWarning> CheckLevelConsistency(
        List<AuditItem> tokens)
    {
        foreach (var group in tokens.GroupBy(x => (x.Token.Signature, x.Scope)))
        {
            var members = group.ToList();
            if (members.Count < 2) continue;

            var levels = members.Select(m => m.Heading.Level).Distinct().ToList();
            if (levels.Count == 1) continue;

            // Cấp tham chiếu: xuất hiện nhiều nhất; hoà thì lấy cấp nông nhất, vì phần đầu tài
            // liệu (nơi mô hình chưa kịp trượt) thường đúng và cũng thường là cấp nhỏ hơn.
            var reference = members
                .GroupBy(m => m.Heading.Level)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .First().Key;

            var odd = members.Where(m => m.Heading.Level != reference).ToList();
            foreach (var m in odd) m.Heading.Disputed = true;

            var kind = Describe(group.First().Token);
            yield return new AuditWarning(
                $"{kind}: {members.Count} mục cùng dạng đánh số nhưng cấp không thống nhất " +
                $"(phổ biến là H{reference}) — lệch ở đoạn " +
                string.Join(", ", odd.Select(m => $"{m.Heading.Index} (H{m.Heading.Level})")),
                [.. odd.Select(m => m.Heading.Index)]);
        }
    }

    /// <summary>
    /// Dãy anh em phải bắt đầu từ 1 và liên tục. Một dãy mới bắt đầu khi số không còn tăng
    /// (<c>… 2. 3.</c> rồi <c>1.</c> nghĩa là đã sang mục cha khác), nên không cần biết cây cha con.
    /// </summary>
    private static IEnumerable<AuditWarning> CheckSequenceGaps(
        List<AuditItem> tokens)
    {
        foreach (var group in tokens.GroupBy(x => x.Token.Signature))
        {
            // Một dãy chỉ liên tục trong cùng một parent/sibling scope. Các mục 1..9
            // của chương I không được nối với 1..9 của chương II.
            foreach (var scoped in group.GroupBy(x => x.Scope))
            {
                var run = new List<AuditItem>();

                foreach (var item in scoped)
                {
                    if (run.Count > 0 && item.Token.Value <= run[^1].Token.Value)
                    {
                        foreach (var w in InspectRun(run, item.Token)) yield return w;
                        run = [];
                    }
                    run.Add(item);
                }

                foreach (var w in InspectRun(run, scoped.First().Token)) yield return w;
            }
        }
    }

    private static IEnumerable<AuditWarning> InspectRun(
        List<AuditItem> run,
        NumberToken sample)
    {
        if (run.Count == 0) yield break;
        // Một nhánh chỉ có một mục không đủ bằng chứng để kết luận mất mục trước đó;
        // đây thường là mục con đầu tiên hoặc tài liệu bắt đầu giữa chừng.
        if (run.Count == 1) yield break;

        var kind = Describe(sample);
        var first = run[0];
        // Dãy chữ cái được chấm lại theo bảng chữ cái hợp với chính nó, xem chú thích ở
        // LetterAlphabets. Các dạng khác giữ nguyên giá trị token.
        var values = sample.Kind == NumberKind.Letter
            ? RescoreLetters(run)
            : [.. run.Select(x => x.Token.Value)];

        // Dãy bắt đầu từ 2 nghĩa là mục số 1 đã bị đánh rơi ở tầng lọc — mô hình không cứu được
        // vì nó chưa từng nhìn thấy đoạn đó.
        if (values[0] > 1)
        {
            var missing = string.Join(", ", Enumerable.Range(1, values[0] - 1));
            yield return new AuditWarning(
                $"{kind}: dãy bắt đầu từ {values[0]} tại đoạn {first.Heading.Index} " +
                $"(\"{Excerpt(first.Heading.Text)}\") — thiếu mục {missing}",
                [first.Heading.Index]);
        }

        for (var i = 1; i < run.Count; i++)
        {
            var gap = values[i] - values[i - 1];
            if (gap <= 1) continue;

            var missing = string.Join(", ", Enumerable.Range(values[i - 1] + 1, gap - 1));
            run[i].Heading.Disputed = true;
            yield return new AuditWarning(
                $"{kind}: nhảy từ {values[i - 1]} sang {values[i]} " +
                $"tại đoạn {run[i].Heading.Index} — thiếu mục {missing}",
                [run[i - 1].Heading.Index, run[i].Heading.Index]);
        }
    }

    /// <summary>
    /// Chọn bảng chữ cái khớp với dãy này nhất rồi trả về thứ tự theo bảng đó. Tiêu chí là tổng
    /// độ hụt (<c>Σ max(0, bước − 1)</c>) chứ không phải số lần đứt, để một bảng gây một bước
    /// nhảy dài bị phạt nặng hơn bảng gây hai bước nhảy ngắn. Bảng không chứa đủ chữ của dãy bị
    /// loại. Không bảng nào chứa đủ thì giữ nguyên giá trị hợp nhất — thà cảnh báo theo thứ tự
    /// xấp xỉ còn hơn im lặng bỏ qua cả dãy.
    /// </summary>
    private static int[] RescoreLetters(List<AuditItem> run)
    {
        var letters = new char[run.Count];
        for (var i = 0; i < run.Count; i++)
        {
            if (LetterRx.Match(run[i].Heading.Text ?? "") is not { Success: true } m)
                return [.. run.Select(x => x.Token.Value)];
            letters[i] = m.Groups[1].Value[0];
        }

        int[]? best = null;
        var bestCost = int.MaxValue;
        foreach (var alphabet in LetterAlphabets)
        {
            var scored = new int[letters.Length];
            var cost = 0;
            var usable = true;
            for (var i = 0; i < letters.Length && usable; i++)
            {
                if (LetterOrdinal(letters[i], alphabet) is not { } v) usable = false;
                else scored[i] = v;
            }
            if (!usable) continue;

            cost += Math.Max(0, scored[0] - 1);
            for (var i = 1; i < scored.Length; i++) cost += Math.Max(0, scored[i] - scored[i - 1] - 1);
            if (cost >= bestCost) continue;
            (best, bestCost) = (scored, cost);
        }

        return best ?? [.. run.Select(x => x.Token.Value)];
    }

    private static string Describe(NumberToken t) => t.Kind switch
    {
        NumberKind.Roman => "Số La Mã",
        NumberKind.Letter => "Chữ cái",
        _ => t.Depth == 1 ? "Đánh số" : $"Đánh số {t.Depth} cấp",
    };

    private static string Excerpt(string text) =>
        text.Length <= 40 ? text : text[..40] + "…";

    /// <summary>
    /// Xác định phạm vi sibling bằng heading cha gần nhất. Đây là điểm quan trọng:
    /// cùng dạng 3.1 ở hai chương khác nhau không phải cùng một nhóm kiểm tra.
    /// </summary>
    private static string ScopeKey(IReadOnlyList<HeadingRecord> ordered, int at, SlimDocument? document)
    {
        var current = ordered[at];
        // La Mã ở đầu chương là chuỗi section-level; giữ chung một scope để phát hiện
        // I → III, nhưng không suy ra parent từ cấp model có thể đang lệch.
        if (ParseHeadingNumber(current, document?.ByIndex(current.Index))?.Kind == NumberKind.Roman)
            return "roman-root";
        for (var i = at - 1; i >= 0; i--)
        {
            if (ordered[i].Level < current.Level)
                return $"parent:{ordered[i].Index}";
        }
        return "root";
    }

    /// <summary>
    /// Tách ký hiệu đánh số ở đầu chuỗi. Thử La Mã trước số Ả Rập vì <c>I.</c>, <c>V.</c>, <c>X.</c>
    /// cũng khớp mẫu chữ cái đơn.
    /// </summary>
    public static NumberToken? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (RomanRx.Match(text) is { Success: true } roman && HasTitleRemainder(roman)
            && RomanToInt(roman.Groups[1].Value) is { } rv)
            return new NumberToken(NumberKind.Roman, 1, rv);

        if (ArabicRx.Match(text) is { Success: true } arabic && HasTitleRemainder(arabic))
        {
            var parts = arabic.Groups[1].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && int.TryParse(parts[^1], out var last))
                return new NumberToken(NumberKind.Arabic, parts.Length, last);
        }

        if (LetterRx.Match(text) is { Success: true } letter && HasTitleRemainder(letter))
        {
            // Giá trị lưu theo thứ tự hợp nhất; đứt quãng được chấm lại theo dãy ở InspectRun.
            if (LetterOrdinal(letter.Groups[1].Value[0], MergedLetterOrder) is { } ordinal)
                return new NumberToken(NumberKind.Letter, 1, ordinal);
        }

        // Sau cùng: ba mẫu trên đều bắt đầu bằng chính ký hiệu số nên không thể va vào "nhãn + số".
        if (LabelledRx.Match(text) is { Success: true } labelled && HasTitleRemainder(labelled, 3))
        {
            var label = labelled.Groups[1].Value.ToLowerInvariant();
            var numeral = labelled.Groups[2].Value;
            if (int.TryParse(numeral, out var labelledValue))
                return new NumberToken(NumberKind.Labelled, 1, labelledValue, label);
            if (RomanToInt(numeral) is { } labelledRoman)
                return new NumberToken(NumberKind.Labelled, 1, labelledRoman, label);
        }

        return null;
    }

    /// <summary>
    /// Đọc ký hiệu đánh số của một ĐOẠN chứ không của một chuỗi rời.
    /// <para>
    /// Khi Word đánh số qua <c>w:numPr</c>, con số KHÔNG nằm trong text của run —
    /// <c>NumberingResolver</c> tính nó ra <see cref="SlimParagraph.NumberLabel"/>. Gọi thẳng
    /// <see cref="Parse"/> trên text hiển thị thì cả nhóm tài liệu dùng danh sách nhiều cấp kiểu
    /// Word đều trả null, tức là "không có đánh số" cho đúng những đoạn được đánh số bài bản nhất.
    /// </para>
    /// <para>
    /// Ghép nhãn vào trước text thay vì phân tích riêng: nhãn của cấp con là "1.1." nguyên vẹn nên
    /// cùng một luật đọc được cả hai nguồn, và ràng buộc "sau tiền tố phải có tên mục" vẫn giữ.
    /// </para>
    /// </summary>
    public static NumberToken? ParseParagraph(SlimParagraph? paragraph, string fallbackText) =>
        Parse(TextWithNumberLabel(paragraph, fallbackText));

    private static NumberToken? ParseHeadingNumber(
        HeadingRecord heading,
        SlimParagraph? paragraph,
        ExtractionOptions? options = null)
    {
        if (heading.ConfidenceBasis == "pdf_textbook_layout")
            return Parse(heading.Text);
        return options is null
            ? ParseParagraph(paragraph, heading.Text)
            : ParseParagraph(paragraph, heading.Text, options);
    }

    /// <summary>
    /// Như <see cref="ParseParagraph"/>, nhưng đọc thêm được dạng <c>NHÃN + SỐ + HẾT</c>
    /// (<c>PHỤ LỤC 1</c>) khi <see cref="ExtractionOptions.AllowBareLabelledNumbers"/> bật.
    /// <para>
    /// Chỉ áp cho đoạn NGOÀI <c>w:sdt</c>. Trong content control, cùng hình dạng ấy là dòng mục lục
    /// kèm số TRANG (<c>MỞ ĐẦU 1</c>, <c>KẾT LUẬN 154</c>) — đọc nó thành chuỗi đánh số thì hậu kiểm
    /// sẽ đi báo thiếu những mục không tồn tại. Đo được: trong sdt 0/8 là đề mục, ngoài sdt 5/5 là
    /// đề mục (§36).
    /// </para>
    /// </summary>
    public static NumberToken? ParseParagraph(
        SlimParagraph? paragraph, string fallbackText, ExtractionOptions options)
    {
        if (ParseParagraph(paragraph, fallbackText) is { } token) return token;
        if (!options.AllowBareLabelledNumbers) return null;
        if (paragraph is null or { InContentControl: true }) return null;

        var text = TextWithNumberLabel(paragraph, fallbackText);
        if (BareLabelledRx.Match(text) is not { Success: true } m) return null;
        var value = int.TryParse(m.Groups[2].Value, out var arabic)
            ? arabic
            : RomanToInt(m.Groups[2].Value);
        return value is { } v
            ? new NumberToken(NumberKind.Labelled, 1, v, Label: m.Groups[1].Value.ToUpperInvariant())
            : null;
    }

    /// <summary>
    /// <c>PHỤ LỤC 1</c>, <c>Tiểu kết chương 2</c> — nhãn rồi số rồi HẾT. Khác
    /// <see cref="LabelledRx"/> ở chỗ cấm phần đuôi thay vì đòi nó: chú thích (<c>Bảng 1.2 Đối
    /// chiếu…</c>) luôn có đuôi, nên hai mẫu không giẫm lên nhau.
    /// </summary>
    private static readonly Regex BareLabelledRx = new(
        @"^\s*(\p{L}[\p{L}\s]{1,24}?)\s+(\d{1,3}|[IVXLCDM]{1,7})\s*[\.\):\-–]?\s*$",
        RegexOptions.Compiled);

    /// <summary>Chuỗi dùng để đọc đánh số: nhãn do OOXML sinh (nếu có) ghép trước text hiển thị.</summary>
    public static string TextWithNumberLabel(SlimParagraph? paragraph, string fallbackText) =>
        paragraph?.NumberLabel is { Length: > 0 } label
            ? label + " " + (paragraph.Text ?? fallbackText)
            : paragraph?.Text ?? fallbackText;

    /// <summary>
    /// Trả đường dẫn số Ả Rập đầy đủ, ví dụ "3.1." → [3, 1]. Khác <see cref="Parse"/>
    /// chỉ giữ depth/giá trị cuối, API này dùng để dựng quan hệ cha–con và sibling.
    /// </summary>
    public static int[]? ParseArabicPath(string text)
    {
        if (ArabicRx.Match(text) is not { Success: true } match || !HasTitleRemainder(match)) return null;
        var parts = match.Groups[1].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var path = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out path[i])) return null;
        return path;
    }

    private static bool HasTitleRemainder(Match match) => HasTitleRemainder(match, 2);

    /// <summary>
    /// Phần tên nằm ở nhóm nào là tuỳ mẫu. Ba mẫu số nguyên thuỷ đặt nó ở nhóm 2; mẫu "nhãn + số"
    /// có thêm nhóm nhãn nên phần tên lùi xuống nhóm 3.
    /// <para>
    /// Chốt này suýt lọt: gọi bản mặc định cho <c>LabelledRx</c> thì nhóm 2 là CHỮ SỐ, và
    /// <c>TitleWordRx</c> (≥2 chữ cái) khớp <c>II</c> nhưng không khớp <c>I</c> — nên
    /// <c>PHẦN I.</c> trượt còn <c>PHẦN II.</c> lọt, hai mục cùng dạng ra hai chữ ký khác nhau.
    /// <c>SignatureTierTests</c> bắt được đúng ca đó.
    /// </para>
    /// </summary>
    private static bool HasTitleRemainder(Match match, int titleGroup) =>
        match.Groups.Count > titleGroup && TitleWordRx.IsMatch(match.Groups[titleGroup].Value);

    /// <summary>Trả null khi chuỗi không phải số La Mã hợp lệ (vd "IIII", "VV").</summary>
    private static int? RomanToInt(string s)
    {
        var map = new Dictionary<char, int>
        {
            ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100, ['D'] = 500, ['M'] = 1000,
        };

        var total = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var v = map[s[i]];
            total += i + 1 < s.Length && map[s[i + 1]] > v ? -v : v;
        }

        // Chuẩn hoá ngược lại: chỉ nhận khi viết đúng chính tả La Mã, tránh nuốt nhầm từ viết hoa.
        return total is > 0 and < 40 && ToRoman(total) == s.ToUpperInvariant() ? total : null;
    }

    private static string ToRoman(int n)
    {
        int[] values = [10, 9, 5, 4, 1];
        string[] symbols = ["X", "IX", "V", "IV", "I"];
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < values.Length; i++)
            while (n >= values[i]) { sb.Append(symbols[i]); n -= values[i]; }
        return sb.ToString();
    }
}
