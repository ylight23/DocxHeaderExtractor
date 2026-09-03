using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Application.Policy;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

public static class StyleTrustAudit
{
    /// <summary>
    /// Chấm điểm tin cậy của style. Phải chạy SAU lượt <see cref="HeadingHeuristics.Classify"/> đầu
    /// tiên vì vế "trông không phải đề mục" dùng lại đúng những luật hình dạng đã có ở đó — chú
    /// thích đối tượng, dòng mục lục, gạch đầu dòng — thay vì dựng một bộ luật thứ hai đi lệch dần.
    /// </summary>
    public static StyleTrust Measure(IReadOnlyList<IPolicyParagraph> paragraphs)
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

        // Vế thứ ba: đối chiếu style với một nguồn cấp ĐỘC LẬP — độ sâu của chuỗi đánh số người
        // soạn gõ ("1.1.2." ⇒ sâu 3). Hai vế trên chỉ soi chính style nên chúng mù với kiểu hỏng
        // mà §16 đo được: cùng một style mang hai độ sâu khác nhau ở hai phần của cùng tài liệu,
        // trong khi số cấp riêng biệt và tính liên tục đều khoẻ mạnh.
        var numbered = 0;
        var disagree = 0;
        foreach (var p in styled)
        {
            var path = NumberingAudit.ParseArabicPath(NumberingAudit.TextWithNumberLabel(p, p.Text));
            if (path is not { Length: > 0 }) continue;
            numbered++;
            if (path.Length != HeadingHeuristics.BuiltInLevel(p)!.Value) disagree++;
        }

        return new StyleTrust(
            styled.Count,
            suspect,
            levels.Count,
            skips,
            nonEmpty == 0 ? 0 : (double)styled.Count / nonEmpty,
            numbered,
            disagree);
    }

    /// <summary>
    /// Đoạn mang style Heading nhưng mọi dấu hiệu khác nói nó không phải đề mục. Cố ý CHỈ dùng tín
    /// hiệu cấu trúc/hình dạng, không dùng một từ tiếng Việt nào — cùng kỷ luật §9.
    /// </summary>
    private static bool LooksNothingLikeHeading(IPolicyParagraph p) =>
        p.InTableOfContents
        || p.TableDepth > 0
        || HeadingHeuristics.IsObjectCaption(p)
        || HeadingHeuristics.LooksLikeListItem(p.Text)
        || HeadingHeuristics.EndsLikeSentence(p.Text);
}
