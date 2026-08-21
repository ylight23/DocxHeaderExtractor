using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Chọn đúng nhóm heading do model tự đề xuất nhưng thiếu bằng chứng độc lập để phản biện lại.
/// Đây là cổng tổng quát theo evidence, không chứa từ khóa hay mẫu câu của một tài liệu cụ thể.
/// </summary>
public static class ModelHeadingCriticGate
{
    public const double WeakEvidenceThreshold = 0.70;

    public static bool NeedsCritique(
        HeadingRecord heading,
        SlimParagraph paragraph,
        double weakEvidenceThreshold = WeakEvidenceThreshold) =>
        heading.Source == HeadingSource.Model &&
        paragraph.Score < Math.Clamp(weakEvidenceThreshold, 0, 1) &&
        !paragraph.HasBuiltInHeadingStyle &&
        paragraph.OutlineLevel is null &&
        paragraph.NumberingId is null &&
        NumberingAudit.Parse(heading.Text) is null;
}
