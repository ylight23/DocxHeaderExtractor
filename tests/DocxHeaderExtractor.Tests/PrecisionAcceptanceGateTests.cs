using DocxHeaderExtractor.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PrecisionAcceptanceGateTests
{
    [Fact]
    public void Phan_bien_mot_minh_khong_con_du_de_tu_nhan()
    {
        // ĐẢO NGƯỢC khẳng định cũ của test này ("phản biện một mình đạt bậc 0,93 và được tự nhận").
        // §37.2 đo trên khoá luận với đáp án có NHÃN NGƯỜI: nhóm 0,93 — nhóm được tự nhận — đúng
        // 47,6%, còn nhóm 0,85 bị bắt duyệt tay đúng 100%. Ở mức quyết định: tự nhận 16 mục đúng
        // 62,5%, bắt duyệt 111 mục đúng 82,0%.
        //
        // Lý do: lượt phản biện CHỈ chạy trên khối mà chính pipeline đã đánh dấu là không đáng tin
        // (bịa chỉ số, hoặc mọi mục cùng một cấp). "Đã qua phản biện" vì thế là dấu hiệu ĐẾN TỪ
        // VÙNG ĐÁNG NGỜ, không phải dấu hiệu đúng.
        var heading = H("Phạm vi nghiên cứu", critic: true);

        PrecisionAcceptanceGate.Apply([heading], profile: null, targetPrecision: 0.93, minimumSamples: 30);

        Assert.True(heading.Confidence < 0.93, $"phản biện một mình vẫn được {heading.Confidence}");
        Assert.Equal(HeadingDecisionStatus.RequiresReview, heading.DecisionStatus);
        Assert.Equal("evidence_not_calibrated", heading.ConfidenceBasis);
    }

    [Fact]
    public void Critic_plus_numbering_reaches_95_evidence_tier()
    {
        var heading = H("2. Phạm vi nghiên cứu", critic: true);
        heading.Evidence = new HeadingEvidence(true, true, true, true, true, "verified_by_multiple_checks");

        PrecisionAcceptanceGate.Apply([heading], profile: null, targetPrecision: 0.93, minimumSamples: 30);

        Assert.Equal(0.95, heading.Confidence);
        Assert.Equal(HeadingDecisionStatus.AutoAcceptedEvidence, heading.DecisionStatus);
    }

    [Fact]
    public void Holdout_bucket_must_pass_sample_count_and_wilson_lower_bound()
    {
        var heading = H("Phạm vi nghiên cứu", critic: true);
        var signature = HeadingAcceptanceSignature.For(heading);
        var profile = new PrecisionCalibrationProfile
        {
            Documents = 40,
            Buckets =
            [
                new(signature, 100, 100, 1.0,
                    PrecisionCalibrationProfile.WilsonLowerBound(100, 100)),
            ],
        };

        PrecisionAcceptanceGate.Apply([heading], profile, targetPrecision: 0.93, minimumSamples: 30);

        Assert.Equal(HeadingDecisionStatus.AutoAcceptedCalibrated, heading.DecisionStatus);
        Assert.Equal("holdout_wilson_lower95", heading.ConfidenceBasis);
        Assert.InRange(heading.Confidence, 0.96, 0.97);
        Assert.Equal(100, heading.CalibrationSamples);
    }

    [Fact]
    public void Profile_target_precision_and_minimum_samples_override_call_defaults()
    {
        var heading = H("Phạm vi nghiên cứu", critic: true);
        var signature = HeadingAcceptanceSignature.For(heading);
        var profile = new PrecisionCalibrationProfile
        {
            TargetPrecision = 0.90,
            MinimumSamples = 5,
            Buckets = [new(signature, 10, 10, 1.0, 0.91)],
        };

        PrecisionAcceptanceGate.Apply([heading], profile, targetPrecision: 0.93, minimumSamples: 30);

        Assert.Equal(HeadingDecisionStatus.AutoAcceptedCalibrated, heading.DecisionStatus);
        Assert.Equal("holdout_wilson_lower95", heading.ConfidenceBasis);
    }

    [Fact]
    public void Disputed_heading_never_auto_accepts_even_with_perfect_bucket()
    {
        var heading = H("Phạm vi nghiên cứu", critic: true);
        heading.Disputed = true;
        var signature = HeadingAcceptanceSignature.For(heading);
        var profile = new PrecisionCalibrationProfile
        {
            Buckets = [new(signature, 100, 100, 1, 0.963)],
        };

        PrecisionAcceptanceGate.Apply([heading], profile, targetPrecision: 0.93, minimumSamples: 30);

        Assert.Equal(HeadingDecisionStatus.RequiresReview, heading.DecisionStatus);
    }

    [Fact]
    public void Loaded_profile_never_auto_accepts_an_unmeasured_signature()
    {
        var heading = H("Phạm vi nghiên cứu", critic: true);
        var profile = new PrecisionCalibrationProfile
        {
            Buckets = [new("some_other_bucket", 100, 100, 1, 0.963)],
        };

        PrecisionAcceptanceGate.Apply([heading], profile, targetPrecision: 0.93, minimumSamples: 30);

        Assert.Equal(HeadingDecisionStatus.RequiresReview, heading.DecisionStatus);
        Assert.Equal("holdout_bucket_missing", heading.ConfidenceBasis);
    }

    [Fact]
    public void Profile_from_another_model_or_configuration_is_rejected()
    {
        var heading = H("Phạm vi nghiên cứu", critic: true);
        var signature = HeadingAcceptanceSignature.For(heading);
        var profile = new PrecisionCalibrationProfile
        {
            Model = "model-a",
            ConfigurationSignature = "config-a",
            Buckets = [new(signature, 100, 100, 1, 0.963)],
        };

        PrecisionAcceptanceGate.Apply([heading], profile, 0.93, 30,
            currentModel: "model-b", configurationSignature: "config-b");

        Assert.Equal(HeadingDecisionStatus.RequiresReview, heading.DecisionStatus);
        Assert.Equal("calibration_profile_mismatch", heading.ConfidenceBasis);
    }

    [Fact]
    public void Style_or_heuristic_alone_cannot_claim_93_percent_semantic_precision()
    {
        var style = new HeadingRecord
        {
            Index = 1, Level = 1, Text = "Tiêu đề", Source = HeadingSource.Style, Confidence = 1,
        };
        var heuristic = new HeadingRecord
        {
            Index = 2, Level = 1, Text = "Tiêu đề", Source = HeadingSource.Heuristic, Confidence = 1,
        };

        PrecisionAcceptanceGate.Apply([style, heuristic], null, 0.93, 30);

        // Ý ĐỊNH của test giữ nguyên: không nguồn nào một mình đòi được 93%. Bậc điểm đổi vì
        // thang mới do bằng chứng dẫn dắt, mà hai mục này KHÔNG có evidence nào — nên rơi về trần
        // "không bằng chứng" (0,60) thay vì 0,85/0,75. Chặt hơn bản cũ, cùng chiều với ý định.
        Assert.True(style.Confidence <= 0.60, $"style một mình được {style.Confidence}");
        Assert.True(heuristic.Confidence <= 0.60, $"heuristic một mình được {heuristic.Confidence}");
        Assert.All([style, heuristic], h => Assert.Equal(HeadingDecisionStatus.RequiresReview, h.DecisionStatus));
    }

    [Fact]
    public void Wilson_bound_does_not_treat_small_perfect_sample_as_95_percent_proof()
    {
        Assert.True(PrecisionCalibrationProfile.WilsonLowerBound(5, 5) < 0.60);
        Assert.True(PrecisionCalibrationProfile.WilsonLowerBound(30, 30) < 0.90);
        Assert.True(PrecisionCalibrationProfile.WilsonLowerBound(52, 52) >= 0.93);
        Assert.True(PrecisionCalibrationProfile.WilsonLowerBound(73, 73) >= 0.95);
        Assert.True(PrecisionCalibrationProfile.WilsonLowerBound(100, 100) > 0.95);
    }

    [Fact]
    public void Calibration_builder_counts_false_positive_and_wrong_level_as_errors()
    {
        var correct = H("Phạm vi nghiên cứu", critic: true, index: 1);
        var falsePositive = H("Thông tin liên hệ", critic: true, index: 2);
        var outline = new DocumentOutline
        {
            File = "holdout.docx",
            ParagraphCount = 2,
            CandidateCount = 2,
            Headings = [correct, falsePositive],
        };
        var builder = new PrecisionCalibrationBuilder();

        builder.Add(outline, AnswerKey.Parse("1 1"));
        var bucket = Assert.Single(builder.Build().Buckets);

        Assert.Equal(2, bucket.Samples);
        Assert.Equal(1, bucket.Correct);
        Assert.Equal(0.5, bucket.Precision);
    }

    private static HeadingRecord H(string text, bool critic, int index = 1) => new()
    {
        Index = index,
        Level = 1,
        Text = text,
        Source = HeadingSource.Model,
        ModelConfirmed = true,
        CriticConfirmed = critic,
        Confidence = 0.85,
    };
}
