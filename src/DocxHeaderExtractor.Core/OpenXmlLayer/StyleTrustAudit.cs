using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

public static class StyleTrustAudit
{
    /// <summary>
    /// Chấm điểm tin cậy của style. Phải chạy SAU lượt <see cref="HeadingHeuristics.Classify"/> đầu
    /// tiên vì vế "trông không phải đề mục" dùng lại đúng những luật hình dạng đã có ở đó — chú
    /// thích đối tượng, dòng mục lục, gạch đầu dòng — thay vì dựng một bộ luật thứ hai đi lệch dần.
    /// </summary>
    public static StyleTrust Measure(IReadOnlyList<SlimParagraph> paragraphs)
    {
        var nonEmpty = paragraphs.Count(p => p.Role != ParagraphRole.Empty);
        var styled = paragraphs.Where(p => HeadingHeuristics.BuiltInLevel(p) is not null).ToList();
        if (styled.Count == 0) return new StyleTrust(0, 0, 0, false, 0);

        var suspect = styled.Count(LooksNothingLikeHeading);

        var levels = styled
            .Select(p => HeadingHeuristics.BuiltInLevel(p)!.Value)
            .ToHashSet();
        var skips = levels.Count > 1 &&
                    Enumerable.Range(levels.Min(), levels.Max() - levels.Min() + 1).Any(l => !levels.Contains(l));

        return new StyleTrust(
            styled.Count,
            suspect,
            levels.Count,
            skips,
            nonEmpty == 0 ? 0 : (double)styled.Count / nonEmpty);
    }

    /// <summary>
    /// Đoạn mang style Heading nhưng mọi dấu hiệu khác nói nó không phải đề mục. Cố ý CHỈ dùng tín
    /// hiệu cấu trúc/hình dạng, không dùng một từ tiếng Việt nào — cùng kỷ luật §9.
    /// </summary>
    private static bool LooksNothingLikeHeading(SlimParagraph p) =>
        p.InTableOfContents
        || p.TableDepth > 0
        || HeadingHeuristics.IsObjectCaption(p)
        || HeadingHeuristics.LooksLikeListItem(p.Text)
        || HeadingHeuristics.EndsLikeSentence(p.Text);
}
