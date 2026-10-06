using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Thống kê tổng hợp hiển thị trên bảng điều khiển. Không có số "độ tin cậy": mô hình không trả
/// về độ tin cậy đã hiệu chuẩn, muốn có độ chính xác thật thì đối chiếu với đáp án.
/// </summary>
public sealed record Stats(
    int Headings,
    int Sources,
    int Rejected,
    int ByModel,
    int ByHumanCorrection,
    int HumanVerified,
    int RequiresReview,
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
            MaxLevel: h.Count == 0 ? 0 : h.Max(x => x.Level) ?? 0);
    }
}
