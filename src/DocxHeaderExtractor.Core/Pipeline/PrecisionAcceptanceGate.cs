using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Chữ ký ổn định để benchmark đúng loại evidence thay vì gộp mọi heading.</summary>
public static class HeadingAcceptanceSignature
{
    public static string For(HeadingRecord heading)
    {
        if (heading.Source == HeadingSource.HumanCorrection) return "human_exact";
        if (heading.Source == HeadingSource.Structure)
            return $"structure_checks_{EvidenceCount(heading.Evidence)}";
        if (heading.Source == HeadingSource.Style)
            return heading.CriticConfirmed ? "style_critic" : "style_only";
        if (heading.Source == HeadingSource.Model)
        {
            var critic = heading.CriticConfirmed ? "critic" : "single";
            // Lấy lại kết quả EvidenceConfidenceCalibrator đã tính (chạy ngay trước cổng này) thay
            // vì đọc số lần thứ hai từ text: bản đọc trần không thấy heading do Word tự đánh số qua
            // w:numPr, nên chúng rơi vào bucket "unnumbered" trong khi chúng có số. Chỉ tự đọc khi
            // chưa có Evidence — đường mà unit test của chính cổng này đi.
            var hasNumbering = heading.Evidence?.NumberingValid
                ?? NumberingAudit.Parse(heading.Text) is not null;
            return $"model_{critic}_{(hasNumbering ? "numbered" : "unnumbered")}";
        }
        return heading.Source.ToString().ToLowerInvariant();
    }

    private static int EvidenceCount(HeadingEvidence? evidence) => evidence is null ? 0 :
        new[] { evidence.NumberingValid, evidence.SiblingSequenceValid, evidence.FormattingConsistent,
            evidence.ModelConfirmed, evidence.TreeValid }.Count(x => x);
}

/// <summary>
/// Cổng selective prediction: chỉ tự nhận nhóm đạt target; phần còn lại để review. Khi chưa có
/// holdout profile, trạng thái ghi rõ chỉ dựa trên evidence, không giả làm calibrated accuracy.
/// </summary>
public static class PrecisionAcceptanceGate
{
    public static void Apply(
        IList<HeadingRecord> headings,
        PrecisionCalibrationProfile? profile,
        double targetPrecision,
        int minimumSamples,
        string? currentModel = null,
        string? configurationSignature = null)
    {
        targetPrecision = Math.Clamp(targetPrecision, 0.50, 0.999);
        minimumSamples = Math.Max(1, minimumSamples);
        var profileCompatible = profile is null ||
            ((currentModel is null && profile.Model is null) ||
             string.Equals(currentModel, profile.Model, StringComparison.OrdinalIgnoreCase)) &&
            (configurationSignature is null ||
             string.Equals(configurationSignature, profile.ConfigurationSignature, StringComparison.Ordinal));

        foreach (var heading in headings)
        {
            var signature = HeadingAcceptanceSignature.For(heading);
            heading.AcceptanceSignature = signature;

            if (heading.Source == HeadingSource.HumanCorrection)
            {
                heading.DecisionStatus = HeadingDecisionStatus.HumanVerified;
                heading.Confidence = 1;
                heading.ConfidenceBasis = "human_exact_match";
                continue;
            }

            // Heading do luật R1 gán thẳng đi qua cổng mà không bị hạ: spec nói rõ nhánh
            // auto_assign "không có cơ chế review nào phía sau bắt lại". Giữ đúng như vậy để phép
            // đo chấm chính cái spec đề xuất, không phải một bản đã bị làm mềm.
            if (heading.ConfidenceBasis == OoxmlStyleAutoAssign.Basis)
            {
                heading.DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence;
                heading.Confidence = 1;
                continue;
            }

            if (profile is not null && !profileCompatible)
            {
                heading.Confidence = EvidenceScore(heading);
                heading.ConfidenceBasis = "calibration_profile_mismatch";
                heading.DecisionStatus = HeadingDecisionStatus.RequiresReview;
                continue;
            }

            var bucket = profile?.Find(signature);
            if (bucket is { } measured)
            {
                heading.CalibrationSamples = measured.Samples;
                // Hiển thị cận dưới thay vì precision quan sát để 100/100 không biến thành
                // lời khẳng định chắc chắn 100%. Precision thô vẫn được lưu trong profile.
                heading.Confidence = measured.WilsonLower95;
                heading.ConfidenceBasis = "holdout_wilson_lower95";
                heading.DecisionStatus = measured.Samples >= minimumSamples &&
                    measured.WilsonLower95 >= targetPrecision && !heading.Disputed
                    ? HeadingDecisionStatus.AutoAcceptedCalibrated
                    : HeadingDecisionStatus.RequiresReview;
                continue;
            }

            var evidenceScore = EvidenceScore(heading);
            if (profile is not null)
            {
                heading.Confidence = evidenceScore;
                heading.ConfidenceBasis = "holdout_bucket_missing";
                heading.DecisionStatus = HeadingDecisionStatus.RequiresReview;
                continue;
            }

            heading.Confidence = evidenceScore;
            heading.ConfidenceBasis = "evidence_not_calibrated";
            heading.DecisionStatus = evidenceScore >= targetPrecision && !heading.Disputed
                ? HeadingDecisionStatus.AutoAcceptedEvidence
                : HeadingDecisionStatus.RequiresReview;
        }
    }

    private static double EvidenceScore(HeadingRecord heading)
    {
        if (heading.Source == HeadingSource.Model && heading.CriticConfirmed)
        {
            var e = heading.Evidence;
            var independentStructure = e is
                { NumberingValid: true, SiblingSequenceValid: true, FormattingConsistent: true, TreeValid: true };
            return independentStructure ? 0.95 : 0.93;
        }
        if (heading.Source == HeadingSource.Style && heading.CriticConfirmed) return 0.93;
        if (heading.Source is HeadingSource.Model or HeadingSource.Style)
            return Math.Min(heading.Confidence, 0.85);
        if (heading.Source == HeadingSource.Heuristic)
            return Math.Min(heading.Confidence, 0.75);
        return heading.Confidence; // Structure đã được bộ 5 evidence checks chấm riêng.
    }
}
