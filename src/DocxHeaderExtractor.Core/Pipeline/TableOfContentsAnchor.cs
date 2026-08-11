using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Mục lục của chính tài liệu là TUYÊN BỐ CỦA TÁC GIẢ về bố cục đáng lẽ phải có — kèm cả tên mục
/// lẫn độ sâu đánh số. Pipeline vốn coi dòng mục lục là rác (đúng: chúng không phải đề mục), rồi
/// vứt luôn thông tin chúng mang.
/// <para>
/// ĐO ĐƯỢC trên khoá luận thật: 21 dòng mục lục khớp <b>21/21</b> với đề mục thật trong đáp án đồng
/// thuận — không sai một dòng. Chúng phủ 23/110 đề mục (12 cấp 1, 11 cấp 2).
/// </para>
/// <para>
/// Lượt này CHỈ pin cấp, không thêm và không xoá đề mục nào: dòng mục lục nói "mục này tồn tại và
/// sâu chừng này", nó không nói gì về những mục nó không nhắc tới. Ghép theo TEXT đã chuẩn hoá (bỏ
/// số trang, bỏ tiền tố đánh số, bỏ dấu câu, hạ chữ thường) nên không phụ thuộc ngôn ngữ.
/// </para>
/// </summary>
public static class TableOfContentsAnchor
{
    private static readonly Regex TrailingPageRx = new(@"\s+\d{1,4}$", RegexOptions.Compiled);
    private static readonly Regex NumberPrefixRx = new(@"^[0-9IVXLCDM]+(?:[\.\-][0-9]+)*[\.\):]*\s*", RegexOptions.Compiled);
    private static readonly Regex PunctRx = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
    private static readonly Regex SpaceRx = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Trả về số cấp đã pin.</summary>
    public static int Apply(IList<HeadingRecord> headings, SlimDocument document)
    {
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in document.Paragraphs)
        {
            if (!p.InTableOfContents) continue;
            // Ưu tiên NumberLabel đã resolve từ numbering.xml ("1.1.1" -> cấp 3) trước khi đọc TEXT:
            // heading numPr-driven (numbering do Word vẽ, không gõ tay) không để số nào trong TEXT
            // của dòng mục lục. ĐO ĐƯỢC trên báo cáo thực tập MBBank thật (numbering-driven, đáp án
            // người kiểm): thiếu vế này khiến DepthOf mặc định 14/23 mục về cấp 1 — Apply "nói lời
            // cuối" nên ghi đè cấp ĐÚNG đã có từ numPr thành cấp SAI. Xác nhận bằng test cô lập
            // TableOfContentsAnchorNumberLabelTests trước khi sửa ở đây.
            var depth = DepthFromNumberLabel(p.NumberLabel) ?? DepthOf(p.Text);
            if (depth is not { } d) continue;
            var key = Normalize(p.Text);
            if (key.Length < 4) continue;
            // Trùng tên trong mục lục thì bỏ cả hai: không phân định được mục nào là mục nào.
            entries[key] = entries.TryGetValue(key, out var old) && old != d ? -1 : d;
        }
        if (entries.Count == 0) return 0;

        var changed = 0;
        foreach (var heading in headings)
        {
            if (!entries.TryGetValue(Normalize(heading.Text), out var depth) || depth < 1) continue;
            if (heading.Level == depth) continue;
            heading.Level = Math.Clamp(depth, 1, 9);
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Độ sâu suy từ chính ký hiệu đánh số trong dòng mục lục: <c>1.1.</c> ⇒ 2, <c>CHƯƠNG 1:</c> ⇒ 1.
    /// Dòng không có ký hiệu nào (<c>MỞ ĐẦU</c>, <c>KẾT LUẬN</c>) ⇒ cấp 1, vì mục lục chỉ liệt kê
    /// mục cấp ngoài cùng khi chúng không đánh số.
    /// </summary>
    /// <remarks>internal (không private): dùng chung với <see cref="Eval.TocAnswerKeyGenerator"/> —
    /// một nguồn chuẩn hoá duy nhất, không nhân đôi logic đã kiểm chứng ở đây.</remarks>
    internal static int? DepthOf(string text)
    {
        var body = TrailingPageRx.Replace(text.Trim(), string.Empty);
        var token = NumberingAudit.Parse(body);
        if (token is { Kind: NumberKind.Arabic }) return token.Value.Depth;
        if (token is not null) return 1;
        return NumberPrefixRx.IsMatch(body) ? null : 1;
    }

    /// <summary>Đếm số đoạn ngăn cách bởi dấu chấm trong nhãn numbering đã resolve ("1.1.1." -> 3,
    /// "IV." -> 1). Null nếu đoạn không mang numPr (headings kiểu Chương/từ khoá thường không có).</summary>
    /// <remarks>internal: dùng chung với <see cref="Eval.TocAnswerKeyGenerator"/>.</remarks>
    internal static int? DepthFromNumberLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var parts = label.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts.Length : null;
    }

    internal static string Normalize(string text)
    {
        var body = TrailingPageRx.Replace(text.Trim(), string.Empty);
        body = NumberPrefixRx.Replace(body, string.Empty);
        body = PunctRx.Replace(body.Normalize(NormalizationForm.FormC), " ");
        return SpaceRx.Replace(body, " ").Trim().ToLower(CultureInfo.InvariantCulture);
    }
}
