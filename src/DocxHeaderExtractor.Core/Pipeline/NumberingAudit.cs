using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Loại ký hiệu đánh số đứng đầu một tiêu đề.</summary>
public enum NumberKind
{
    None,
    Arabic,
    Roman,
    Letter,
}

/// <summary>
/// Ký hiệu đánh số đã tách: <c>3.1.</c> → Arabic, Depth 2, Value 1. <c>IV.</c> → Roman, Depth 1, Value 4.
/// </summary>
public readonly record struct NumberToken(NumberKind Kind, int Depth, int Value)
{
    /// <summary>Hai tiêu đề cùng chữ ký thì phải cùng cấp — đó là bất biến mà kiểm tra này dựa vào.</summary>
    public string Signature => $"{Kind}:{Depth}";
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
    // KHÔNG có mẫu nào tương ứng LabelledNumberPrefixRx ở đây: "Chương 1. Tổng quan" không phân
    // tích được thành token. Hệ quả đã đo được nằm ở StructuralHierarchyResolver.SignatureTiers.

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
        @"^\s*([A-Za-z])\s*[\.\)]\s*(\S.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Một prefix giống numbering chỉ mang ý nghĩa cấu trúc khi sau nó có nhãn ngôn ngữ.
    // Điều này loại các dòng số liệu kiểu "A: 04, B: 04" hoặc "1: 03/04" mà không cần
    // hardcode tên trường. Hai chữ cái liên tiếp vẫn chấp nhận viết tắt tổng quát.
    private static readonly Regex TitleWordRx = new(@"\p{L}{2,}", RegexOptions.Compiled);

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
        SlimDocument? document = null)
    {
        if (headings.Count == 0) return [];

        var ordered = headings.OrderBy(h => h.Index).ToList();
        var tokens = ordered
            .Select(h => (Heading: h, Token: ParseParagraph(document?.ByIndex(h.Index), h.Text)))
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

        // Dãy bắt đầu từ 2 nghĩa là mục số 1 đã bị đánh rơi ở tầng lọc — mô hình không cứu được
        // vì nó chưa từng nhìn thấy đoạn đó.
        if (first.Token.Value > 1)
        {
            var missing = string.Join(", ", Enumerable.Range(1, first.Token.Value - 1));
            yield return new AuditWarning(
                $"{kind}: dãy bắt đầu từ {first.Token.Value} tại đoạn {first.Heading.Index} " +
                $"(\"{Excerpt(first.Heading.Text)}\") — thiếu mục {missing}",
                [first.Heading.Index]);
        }

        for (var i = 1; i < run.Count; i++)
        {
            var gap = run[i].Token.Value - run[i - 1].Token.Value;
            if (gap <= 1) continue;

            var missing = string.Join(", ", Enumerable.Range(run[i - 1].Token.Value + 1, gap - 1));
            run[i].Heading.Disputed = true;
            yield return new AuditWarning(
                $"{kind}: nhảy từ {run[i - 1].Token.Value} sang {run[i].Token.Value} " +
                $"tại đoạn {run[i].Heading.Index} — thiếu mục {missing}",
                [run[i - 1].Heading.Index, run[i].Heading.Index]);
        }
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
        if (ParseParagraph(document?.ByIndex(current.Index), current.Text)?.Kind == NumberKind.Roman)
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
            var c = char.ToUpperInvariant(letter.Groups[1].Value[0]);
            if (c is >= 'A' and <= 'Z') return new NumberToken(NumberKind.Letter, 1, c - 'A' + 1);
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

    private static bool HasTitleRemainder(Match match) =>
        match.Groups.Count > 2 && TitleWordRx.IsMatch(match.Groups[2].Value);

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
