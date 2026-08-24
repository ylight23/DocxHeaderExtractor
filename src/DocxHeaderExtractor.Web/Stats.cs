using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Thống kê tổng hợp hiển thị trên bảng điều khiển.
/// <paramref name="AvgConfidence"/> là ĐỘ TIN CẬY do pipeline tự đánh giá, không phải độ chính
/// xác đo được — muốn có độ chính xác thật thì phải đối chiếu với đáp án (xem ô "Đối chiếu đáp án").
/// </summary>
public sealed record Stats(
    int Headings,
    int Candidates,
    int Rejected,
    int ByStyle,
    int ByModel,
    int ByHeuristic,
    int ByHumanCorrection,
    int AutoAccepted,
    int AutoAcceptedCalibrated,
    int AutoAcceptedDeterministic,
    int AutoAcceptedUncalibratedEvidence,
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
            Candidates: o.CandidateCount,
            Rejected: Math.Max(0, o.CandidateCount - h.Count),
            ByStyle: h.Count(x => x.Source == HeadingSource.Style),
            ByModel: h.Count(x => x.Source == HeadingSource.Model),
            ByHeuristic: h.Count(x => x.Source == HeadingSource.Heuristic),
            ByHumanCorrection: h.Count(x => x.Source == HeadingSource.HumanCorrection),
            AutoAccepted: h.Count(x => x.DecisionStatus is not HeadingDecisionStatus.RequiresReview),
            AutoAcceptedCalibrated: o.DecisionAudit?.AutoAcceptedCalibrated ??
                h.Count(x => x.DecisionStatus == HeadingDecisionStatus.AutoAcceptedCalibrated),
            AutoAcceptedDeterministic: o.DecisionAudit?.AutoAcceptedDeterministic ?? 0,
            AutoAcceptedUncalibratedEvidence: o.DecisionAudit?.AutoAcceptedUncalibratedEvidence ?? 0,
            HumanVerified: o.DecisionAudit?.HumanVerified ??
                h.Count(x => x.DecisionStatus == HeadingDecisionStatus.HumanVerified),
            RequiresReview: h.Count(x => x.DecisionStatus == HeadingDecisionStatus.RequiresReview),
            AvgConfidence: h.Count == 0 ? 0 : h.Average(x => x.Confidence),
            MaxLevel: h.Count == 0 ? 0 : h.Max(x => x.Level) ?? 0);
    }
}
