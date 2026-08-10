using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>Vai trò của một bảng — spec §5.5.</summary>
public enum TableRole
{
    /// <summary>Không nằm trong bảng.</summary>
    None,

    /// <summary>Bảng dùng để DÀN TRANG (khung bìa, một cột). Nội dung bên trong là luồng chính.</summary>
    Layout,

    /// <summary>Bảng chứa NỘI DUNG có cấu trúc — mỗi ô là một bước/mục có đánh số.</summary>
    Content,

    /// <summary>Bảng DỮ LIỆU thật (số liệu, ô ngắn). Không chứa đề mục.</summary>
    Data,
}

/// <summary>
/// Phân loại từng BẢNG, không phải từng ô — spec §5.5, chỗ spec gọi là <i>"mất dữ liệu lớn nhất nếu
/// làm sai"</i>: tài liệu D có 87% block nằm trong bảng, loại vô điều kiện làm mất <b>40 heading
/// thật</b> (`I.1`, `I.2`, `1. Văn bản đề nghị giao đất;`…).
/// <para>
/// Ba nhóm, xét theo thứ tự: <b>layout</b> (một cột, hoặc ≤2 dòng, hoặc nằm trong 15% đầu tài liệu —
/// khung trang bìa) coi như luồng chính; <b>data</b> (≥30% ô toàn số HOẶC độ dài ô trung bình ≤ 40)
/// loại hẳn; còn lại là <b>content</b> — cho phép chứa đề mục.
/// </para>
/// <para>
/// Ranh giới "một cột" xấp xỉ bằng số đoạn trên mỗi hàng, vì mô hình tinh gọn không giữ lưới ô.
/// Đây là xấp xỉ có thật; ghi ra để người sau biết chỗ nào cần dựng lưới nếu muốn chặt hơn.
/// </para>
/// </summary>
public static class TableRoleClassifier
{
    public const double NumericCellRatio = 0.30;
    public const int ShortCellLength = 40;
    public const double CoverPageFraction = 0.15;

    /// <summary>
    /// Gán <see cref="SlimParagraph.TableRole"/> cho mọi đoạn nằm trong bảng. Các đoạn liên tiếp
    /// cùng <c>TableDepth &gt; 0</c> được coi là một bảng — xấp xỉ, vì hai bảng kề nhau không có đoạn
    /// ngoài bảng xen giữa sẽ bị gộp làm một.
    /// </summary>
    public static void Apply(IReadOnlyList<SlimParagraph> paragraphs)
    {
        var coverLimit = (int)(paragraphs.Count * CoverPageFraction);
        var i = 0;
        while (i < paragraphs.Count)
        {
            if (paragraphs[i].TableDepth == 0) { i++; continue; }

            var start = i;
            while (i < paragraphs.Count && paragraphs[i].TableDepth > 0) i++;
            var cells = paragraphs.Skip(start).Take(i - start).ToList();

            var role = Classify(cells, start, coverLimit);
            foreach (var cell in cells) cell.TableRole = role;
        }
    }

    private static TableRole Classify(IReadOnlyList<SlimParagraph> cells, int start, int coverLimit)
    {
        if (cells.Count <= 2 || start < coverLimit) return TableRole.Layout;

        var texts = cells.Select(c => c.Text ?? "").Where(t => t.Length > 0).ToList();
        if (texts.Count == 0) return TableRole.Layout;

        var numeric = texts.Count(IsNumericCell) / (double)texts.Count;
        var averageLength = texts.Average(t => t.Length);

        return numeric >= NumericCellRatio || averageLength <= ShortCellLength
            ? TableRole.Data
            : TableRole.Content;
    }

    /// <summary>Ô chỉ gồm số và ký hiệu số học — <c>^[\d\s.,%/-]+$</c> của spec.</summary>
    private static bool IsNumericCell(string text) =>
        text.All(c => char.IsDigit(c) || char.IsWhiteSpace(c) || c is '.' or ',' or '%' or '/' or '-');
}
