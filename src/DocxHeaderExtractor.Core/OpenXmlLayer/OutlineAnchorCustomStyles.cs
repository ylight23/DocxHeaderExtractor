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

    public static bool IsAnchoredCustomStyle(
        SlimParagraph paragraph,
        HashSet<string> stylesUnderOutlineAnchor) =>
        paragraph.TableDepth == 0 &&
        paragraph.OutlineLevel is null &&
        !paragraph.HasBuiltInHeadingStyle &&
        paragraph.StyleId is { Length: > 0 } styleId &&
        stylesUnderOutlineAnchor.Contains(styleId);

    private static bool CanContribute(SlimParagraph p) =>
        p.TableDepth == 0 &&
        p.OutlineLevel is null &&
        !p.HasBuiltInHeadingStyle &&
        !string.IsNullOrWhiteSpace(p.StyleId) &&
        !string.IsNullOrWhiteSpace(p.Text) &&
        !p.InTableOfContents &&
        !p.Corrupt;
}
