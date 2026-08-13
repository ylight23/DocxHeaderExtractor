using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Style tự đặt lặp lại dưới một anchor <c>w:outlineLvl</c>.
/// <para>
/// Tài liệu form-based thường dùng <c>w:outlineLvl</c> cho phần chính, rồi dùng style riêng như
/// <c>SPDForms1</c>, <c>SectionIXHeader</c> cho biểu mẫu/phụ lục. Đây là tín hiệu phụ thuộc anchor,
/// không phải mode tài liệu độc lập.
/// </para>
/// </summary>
internal static class OutlineAnchorCustomStyles
{
    public const int MinimumUses = 3;
    public const int MaxAverageLength = 90;

    public static HashSet<string> Find(IReadOnlyList<SlimParagraph> paragraphs)
    {
        var anchored = new List<SlimParagraph>();
        var hasAnchor = false;

        foreach (var p in paragraphs.OrderBy(p => p.Index))
        {
            if (p.OutlineLevel is not null && !p.InTableOfContents)
                hasAnchor = true;

            if (!hasAnchor) continue;
            if (!CanContribute(p)) continue;
            anchored.Add(p);
        }

        return
        [
            .. anchored
                .GroupBy(p => p.StyleId!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= MinimumUses &&
                            g.Average(p => p.Text.Length) < MaxAverageLength)
                .Select(g => g.Key)
        ];
    }

    public static HashSet<string> FindTableStyles(IReadOnlyList<SlimParagraph> paragraphs)
    {
        var anchored = new List<SlimParagraph>();
        var hasAnchor = false;

        foreach (var p in paragraphs.OrderBy(p => p.Index))
        {
            if (p.OutlineLevel is not null && !p.InTableOfContents)
                hasAnchor = true;

            if (!hasAnchor) continue;
            if (!CanContributeTableStyle(p)) continue;
            anchored.Add(p);
        }

        return
        [
            .. anchored
                .GroupBy(p => p.StyleId!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= MinimumUses &&
                            g.Average(p => p.Text.Length) < MaxAverageLength &&
                            LooksLikeHeadingStyleGroup(g))
                .Select(g => g.Key)
        ];
    }

    public static bool IsAnchoredCustomStyle(
        SlimParagraph paragraph,
        HashSet<string> stylesUnderOutlineAnchor) =>
        paragraph.TableDepth == 0 &&
        paragraph.OutlineLevel is null &&
        !paragraph.HasBuiltInHeadingStyle &&
        paragraph.StyleId is { Length: > 0 } styleId &&
        (stylesUnderOutlineAnchor.Contains(styleId) || LooksLikeSparseCustomHeading(paragraph));

    public static bool IsAnchoredTableCustomStyle(
        SlimParagraph paragraph,
        HashSet<string> tableStylesUnderOutlineAnchor) =>
        paragraph.TableDepth > 0 &&
        paragraph.OutlineLevel is null &&
        !paragraph.HasBuiltInHeadingStyle &&
        paragraph.StyleId is { Length: > 0 } styleId &&
        (tableStylesUnderOutlineAnchor.Contains(styleId) ||
         LooksLikeSparseCustomHeading(paragraph));

    private static bool CanContribute(SlimParagraph p) =>
        p.TableDepth == 0 &&
        p.OutlineLevel is null &&
        !p.HasBuiltInHeadingStyle &&
        !string.IsNullOrWhiteSpace(p.StyleId) &&
        !string.IsNullOrWhiteSpace(p.Text) &&
        !p.InTableOfContents &&
        !p.Corrupt;

    private static bool CanContributeTableStyle(SlimParagraph p) =>
        p.TableDepth > 0 &&
        p.OutlineLevel is null &&
        !p.HasBuiltInHeadingStyle &&
        !string.IsNullOrWhiteSpace(p.StyleId) &&
        !IsGenericBodyStyle(p.StyleId) &&
        !string.IsNullOrWhiteSpace(p.Text) &&
        !p.InTableOfContents &&
        !p.Corrupt;

    private static bool IsGenericBodyStyle(string styleId) =>
        styleId.Equals("Normal", StringComparison.OrdinalIgnoreCase) ||
        styleId.Equals("ListParagraph", StringComparison.OrdinalIgnoreCase) ||
        styleId.Contains("Normal", StringComparison.OrdinalIgnoreCase) ||
        styleId.Contains("List", StringComparison.OrdinalIgnoreCase) ||
        styleId.Contains("Caption", StringComparison.OrdinalIgnoreCase) ||
        styleId.Contains("Footer", StringComparison.OrdinalIgnoreCase) ||
        styleId.Contains("Note", StringComparison.OrdinalIgnoreCase) ||
        styleId.Contains("Bullet", StringComparison.OrdinalIgnoreCase) ||
        styleId.Contains("Sub", StringComparison.OrdinalIgnoreCase) ||
        styleId.Contains("Text", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeShortHeading(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length is < 2 or > MaxAverageLength) return false;
        if (trimmed.EndsWith('.') || trimmed.EndsWith(';') || trimmed.EndsWith(',') || trimmed.EndsWith(':'))
            return false;
        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 12;
    }

    private static bool LooksLikeSparseCustomHeading(SlimParagraph paragraph)
    {
        if (paragraph.StyleId is not { Length: > 0 } styleId || IsGenericBodyStyle(styleId))
            return false;
        if (!LooksLikeShortHeading(paragraph.Text)) return false;
        return HasHeadingFormat(paragraph);
    }

    private static bool HasHeadingFormat(SlimParagraph paragraph)
    {
        var fontLift = paragraph.FontSizePt is { } size && paragraph.BodyFontSizePt is { } body
            ? size - body
            : 0;
        var centered = string.Equals(paragraph.Alignment, "center", StringComparison.OrdinalIgnoreCase);
        var numbered = paragraph.NumberingId is not null || !string.IsNullOrWhiteSpace(paragraph.NumberLabel);
        return paragraph.Bold || centered || fontLift >= 1.5 || numbered;
    }

    private static bool LooksLikeHeadingStyleGroup(IEnumerable<SlimParagraph> paragraphs)
    {
        var items = paragraphs.ToList();
        if (items.Count == 0) return false;
        var headingLike = items.Count(HasHeadingFormat);
        return headingLike >= Math.Max(2, (int)Math.Ceiling(items.Count * 0.6));
    }
}
