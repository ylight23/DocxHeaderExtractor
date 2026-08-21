using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Repair;

public sealed record RepairDiagnosticGateResult(
    string File,
    double ReviewRate,
    double CorpusMedianReviewRate,
    double RatioToMedian,
    bool SuspectedUpstreamError,
    string Reason);

/// <summary>
/// Cổng chẩn đoán chạy TRƯỚC khi đưa một tài liệu vào hàng chờ người/agent duyệt. Không đánh giá đúng/
/// sai của heading — chỉ so tỷ lệ "cần xem lại" (RequiresReview/Disputed) của MỘT file với trung vị
/// toàn corpus. Tỷ lệ cao bất thường là dấu hiệu lỗi tầng đọc/tách phía dưới (heading thật bị cắt vụn
/// thành nhiều mảnh mơ hồ), không phải một tài liệu "khó" thật sự — đưa loại này vào duyệt sẽ làm
/// nhiễm correction-memory pool bằng những entry vô nghĩa (đã đo ở 092: FN=38/61 khi để LLM tự quyết
/// trên candidate đã vỡ, xem handoff §139). File bị gắn cờ nên quay lại sửa tầng tách/đọc, không đưa
/// review trực tiếp.
/// </summary>
public static class RepairDiagnosticGate
{
    public const string FormatVersion = "dhx-repair-diagnostic-gate/v1";

    /// <summary>Bội số so với trung vị corpus để coi là bất thường — chốt theo yêu cầu "&gt; 3x".</summary>
    public const double OutlierMultiplier = 3.0;

    /// <summary>
    /// Sàn tuyệt đối cho tỷ lệ cần xem lại của chính file đó. Khi trung vị corpus gần 0 (đa số file
    /// sạch), bất kỳ tỷ lệ khác 0 nhỏ nào cũng vượt 3x trung vị — sàn này chặn báo động giả cho các ca
    /// chỉ có 1-2 mục mơ hồ thật sự, không phải lỗi tầng dưới.
    /// </summary>
    public const double MinimumReviewRateFloor = 0.05;

    public static double ReviewRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var uncertain = headings.Count(h =>
            h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);
        return (double)uncertain / headings.Count;
    }

    /// <summary>
    /// Đánh giá toàn bộ corpus: tính trung vị tỷ lệ cần xem lại, rồi gắn cờ từng file có tỷ lệ vượt
    /// <see cref="OutlierMultiplier"/> lần trung vị (và vượt <see cref="MinimumReviewRateFloor"/> theo
    /// giá trị tuyệt đối). Code thuần — không gọi model, không suy luận ngữ nghĩa.
    /// </summary>
    public static IReadOnlyList<RepairDiagnosticGateResult> Evaluate(IReadOnlyList<RepairCorpusAuditRow> rows) =>
        Evaluate(rows.Select(r => (r.File, r.ReviewRate)).ToList());

    /// <summary>
    /// Cùng luật như overload trên, nhưng chỉ cần (tên file, tỷ lệ cần xem lại) — dùng khi caller chưa
    /// có/không cần dựng đủ <see cref="RepairCorpusAuditRow"/> (vd <c>repair-key-package</c> chạy trên
    /// một đợt file, chỉ cần <see cref="ReviewRate"/> từng file để so trung vị đợt đó, không cần chạy
    /// lại toàn bộ candidate/validation gate).
    /// </summary>
    public static IReadOnlyList<RepairDiagnosticGateResult> Evaluate(IReadOnlyList<(string File, double ReviewRate)> rows)
    {
        var median = Median(rows.Select(r => r.ReviewRate).ToList());
        var effectiveMedian = Math.Max(median, MinimumReviewRateFloor);

        return rows.Select(r =>
        {
            var ratio = effectiveMedian <= 0 ? 0 : r.ReviewRate / effectiveMedian;
            var suspected = r.ReviewRate > MinimumReviewRateFloor && ratio > OutlierMultiplier;
            var reason = suspected
                ? $"tỷ lệ cần xem lại {r.ReviewRate:P1} > {OutlierMultiplier:0}x trung vị corpus ({median:P1}) " +
                  "— nghi lỗi tầng đọc/tách, không đưa review trực tiếp"
                : $"tỷ lệ cần xem lại {r.ReviewRate:P1}, trong ngưỡng bình thường (trung vị corpus {median:P1})";
            return new RepairDiagnosticGateResult(
                r.File, Math.Round(r.ReviewRate, 4), Math.Round(median, 4), Math.Round(ratio, 2), suspected, reason);
        }).ToList();
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
