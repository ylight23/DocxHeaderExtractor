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
            AvgConfidence: h.Count == 0 ? 0 : h.Average(x => x.Confidence),
            MaxLevel: h.Count == 0 ? 0 : h.Max(x => x.Level));
    }
}
