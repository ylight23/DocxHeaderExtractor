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
        string? configurationSignature = null,
        IReadOnlyList<double>? evidenceConfidenceTiers = null)
    {
        targetPrecision = Math.Clamp(profile?.TargetPrecision ?? targetPrecision, 0.50, 0.999);
        minimumSamples = Math.Max(1, profile?.MinimumSamples ?? minimumSamples);
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

            if (IsDeterministicDeclaredBasis(heading.ConfidenceBasis))
            {
                heading.DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence;
                heading.Confidence = Math.Max(heading.Confidence, 0.95);
                continue;
            }

            if (profile is not null && !profileCompatible)
            {
                heading.Confidence = EvidenceScore(heading, evidenceConfidenceTiers);
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

            var evidenceScore = EvidenceScore(heading, evidenceConfidenceTiers);
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

    /// <summary>
    /// Điểm để so với cổng precision, do BẰNG CHỨNG dẫn dắt.
    /// <para>
    /// Bản cũ đặt <c>CriticConfirmed</c> lên trên mọi bằng chứng: mục đã qua phản biện nhận 0,93 kể
    /// cả khi không có evidence nào, còn mục qua đủ 5/5 kiểm tra bị chặn trần 0,85 — dưới cổng 93%.
    /// </para>
    /// <para>
    /// ĐO ĐƯỢC (§37) trên khoá luận, chấm bằng đáp án có nhãn người:
    /// nhóm hiển thị 0,93 (TỰ NHẬN) đúng <b>47,6%</b>; nhóm 0,85 (bị bắt duyệt) đúng <b>100%</b>.
    /// Ở mức quyết định: tự nhận 16 mục đúng 62,5%, bắt duyệt 111 mục đúng 82,0% — cổng tự nhận
    /// đúng nhóm TỆ HƠN cả nhóm nó bắt người đi duyệt.
    /// </para>
    /// <para>
    /// Vì sao phản biện lại phản chỉ báo: lượt phản biện CHỈ chạy trên những khối mà chính pipeline
    /// đã đánh dấu là không đáng tin (bịa chỉ số, hoặc mọi mục cùng một cấp). "Đã qua phản biện"
    /// vì thế là dấu hiệu ĐẾN TỪ VÙNG ĐÁNG NGỜ, không phải dấu hiệu đúng. Nó bị bỏ khỏi thang điểm.
    /// </para>
    /// </summary>
    private static double EvidenceScore(HeadingRecord heading, IReadOnlyList<double>? evidenceConfidenceTiers)
    {
        // Structure đã được bộ 5 evidence checks chấm riêng bằng ConfidenceForChecks.
        if (heading.Source == HeadingSource.Structure) return heading.Confidence;

        // Không có evidence thì không được nhận điểm cao. Nhóm này đo được 0/5 đúng (§37.2) —
        // toàn bộ là mục mang style nhưng chưa qua một kiểm tra cấu trúc nào.
        if (heading.Evidence is not { } e) return Math.Min(heading.Confidence, NoEvidenceCeiling);

        var passed = new[]
        {
            e.NumberingValid, e.SiblingSequenceValid, e.FormattingConsistent, e.ModelConfirmed, e.TreeValid,
        }.Count(x => x);

        var score = EvidenceConfidenceCalibrator.ConfidenceForChecks(passed, evidenceConfidenceTiers);
        return heading.Source == HeadingSource.Heuristic ? Math.Min(score, HeuristicCeiling) : score;
    }

    /// <summary>Trần cho mục không có một kiểm tra cấu trúc nào — dưới mọi cổng thực dụng.</summary>
    private const double NoEvidenceCeiling = 0.60;

    /// <summary>Ứng viên thuần heuristic vẫn giữ trần cũ: hình thức không thay được cấu trúc.</summary>
    private const double HeuristicCeiling = 0.75;

    /// <summary>
    /// Chữ ký của các bộ dựng DETERMINISTIC — mục của chúng có bằng chứng cấu trúc, không phải
    /// phỏng đoán ngữ nghĩa, nên đi thẳng qua cổng.
    /// <para>
    /// <b>Đây từng là danh sách trôi.</b> <see cref="PartSectionOutline"/> và
    /// <see cref="PdfBoldLabelOutline"/> thêm sau và không được đăng ký; cả hai TỰ đặt
    /// <see cref="HeadingDecisionStatus.AutoAcceptedEvidence"/> lúc dựng rồi bị chính cổng này ghi
    /// đè xuống <see cref="HeadingDecisionStatus.RequiresReview"/>. Hệ quả đo được: tài liệu bị chặn
    /// TOÀN BỘ hay không gì — <c>063</c> 25/25, <c>030</c> 12/12, <c>020</c> 48/48 — vì một tài liệu
    /// đi trọn một nhánh. Không có mô hình nào tham gia (<c>--no-llm</c>), nên đây là cổng chống ảo
    /// giác chặn nhầm đường suy luận cấu trúc. Một test phản chiếu mọi hằng
    /// <c>Basis</c> trong assembly để danh sách không trôi lại được.
    /// </para>
    /// </summary>
    internal static bool IsDeterministicDeclaredBasis(string basis) =>
        basis is "legal_marker_declared" or "typed_number_depth" or "numbering_declared" or
            "style_declared" or "outline_level_declared" or "part_section_declared" or
            "pdf_textbook_layout" ||
        basis == BookTocDictionaryOutline.Basis ||
        basis == PdfBookmarkOutline.Basis ||
        basis == PdfTaggedEvidenceOutline.Basis ||
        basis == RfcTocDictionaryOutline.Basis ||
        basis == PdfTocDictionaryOutline.Basis ||
        basis == PartSectionOutline.Basis ||
        basis == FinancialStatementsTocOutline.Basis ||
        basis == PdfFinancialReportOutline.Basis ||
        basis == PdfBoldLabelOutline.Basis ||
        basis == DoclingLayoutOutline.Basis ||
        basis.StartsWith("outline_anchor_", StringComparison.Ordinal);
}
