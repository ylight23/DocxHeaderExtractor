using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Thống kê tổng hợp hiển thị trên bảng điều khiển.
/// <paramref name="AvgConfidence"/> là ĐỘ TIN CẬY do pipeline tự đánh giá, không phải độ chính
/// xác đo được — muốn có độ chính xác thật thì phải đối chiếu với đáp án (xem ô "Đối chiếu đáp án").
/// </summary>
public sealed record Stats(
    int Headings,
    int Sources,
    int Rejected,
    int ByModel,
    int ByHumanCorrection,
    int HumanVerified,
    int RequiresReview,
    double AvgConfidence,
    int MaxLevel)
{
    public static Stats From(DocumentOutline o)
    {
        var h = o.Headings;
        return new Stats(
            Headings: h.Count,
            Sources: o.SourceCount,
            Rejected: Math.Max(0, o.SourceCount - h.Count),
            ByModel: h.Count(x => x.Source == HeadingSource.Model),
            ByHumanCorrection: h.Count(x => x.Source == HeadingSource.HumanCorrection),
            HumanVerified: h.Count(x => x.DecisionStatus == HeadingDecisionStatus.HumanVerified),
            RequiresReview: h.Count(x => x.DecisionStatus == HeadingDecisionStatus.RequiresReview),
            AvgConfidence: h.Count == 0 ? 0 : h.Average(x => x.Confidence),
            MaxLevel: h.Count == 0 ? 0 : h.Max(x => x.Level) ?? 0);
    }
}
