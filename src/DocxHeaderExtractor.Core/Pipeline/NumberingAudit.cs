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
    /// <summary>
    /// <c>1.</c>, <c>3.1.</c>, <c>2.3.4)</c>, kể cả <c>1.MUC</c> thiếu dấu cách.
    /// Giữ giống hệt <c>HeadingHeuristics.DecimalPrefixRx</c>: hai bên lệch nhau thì hậu kiểm sẽ
    /// nói về những mục mà tầng chấm điểm không hề thấy, hoặc ngược lại.
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
    public static IReadOnlyList<AuditWarning> Run(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return [];

        var ordered = headings.OrderBy(h => h.Index).ToList();
        var tokens = ordered
            .Select(h => (Heading: h, Token: Parse(h.Text)))
            .Where(x => x.Token is not null)
            .Select(x => (x.Heading, Token: x.Token!.Value))
            .ToList();

        if (tokens.Count == 0) return [];

        var warnings = new List<AuditWarning>();
        warnings.AddRange(CheckLevelConsistency(tokens));
        warnings.AddRange(CheckSequenceGaps(tokens));
        return warnings;
    }

    /// <summary>Cùng chữ ký mà khác cấp ⇒ dòng lệch khỏi cấp phổ biến nhất là dòng đáng ngờ.</summary>
    private static IEnumerable<AuditWarning> CheckLevelConsistency(
        List<(HeadingRecord Heading, NumberToken Token)> tokens)
    {
        foreach (var group in tokens.GroupBy(x => x.Token.Signature))
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
        List<(HeadingRecord Heading, NumberToken Token)> tokens)
    {
        foreach (var group in tokens.GroupBy(x => x.Token.Signature))
        {
            var run = new List<(HeadingRecord Heading, NumberToken Token)>();

            foreach (var item in group)
            {
                if (run.Count > 0 && item.Token.Value <= run[^1].Token.Value)
                {
                    foreach (var w in InspectRun(run, item.Token)) yield return w;
                    run = [];
                }
                run.Add(item);
            }

            foreach (var w in InspectRun(run, group.First().Token)) yield return w;
        }
    }

    private static IEnumerable<AuditWarning> InspectRun(
        List<(HeadingRecord Heading, NumberToken Token)> run,
        NumberToken sample)
    {
        if (run.Count == 0) yield break;

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
