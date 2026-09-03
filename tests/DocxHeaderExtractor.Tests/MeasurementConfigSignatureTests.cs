using DocxHeaderExtractor.Eval;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Khoá chữ ký cấu hình đo — nơi một con số được phép so với một con số khác.
/// <para>
/// Lý do tồn tại nằm ở handoff §3.7 và §8.1: cùng model, cùng seed, cùng <c>top_k=1</c>, cùng một
/// tài liệu, <c>-ngl 20</c> cho đúng cấp 100% còn <c>-ngl 99</c> cho 85,7% — TÁI LẬP ở cả hai lượt,
/// nên đó là hai cấu hình đo khác nhau chứ không phải nhiễu. §8.1 chốt điều đó bằng LỜI
/// ("mọi con số phải ghi kèm số lớp offload"), nhưng lời không phải cơ chế: chữ ký từng bỏ qua cả
/// <c>GpuLayerCount</c> lẫn <c>Seed</c>, nên profile dựng ở mức offload này được coi là còn hiệu lực
/// ở mức kia — chính cái bẫy §3.7 cảnh báo, do code thi hành thay vì ngăn.
/// </para>
/// </summary>
public sealed class MeasurementConfigSignatureTests
{
    [Fact]
    public void So_lop_offload_GPU_lam_doi_chu_ky_cau_hinh()
    {
        var a = new PipelineOptions();
        var b = new PipelineOptions();
        var aProvider = new DocxHeaderExtractor.Infrastructure.AI.InferenceProviderSelection();
        var bProvider = new DocxHeaderExtractor.Infrastructure.AI.InferenceProviderSelection();
        aProvider.LocalModel.GpuLayerCount = 20;
        bProvider.LocalModel.GpuLayerCount = 99;

        Assert.NotEqual(
            PrecisionCalibrationProfile.ConfigurationFor(a, aProvider),
            PrecisionCalibrationProfile.ConfigurationFor(b, bProvider));
    }

    [Fact]
    public void Seed_sampler_lam_doi_chu_ky_cau_hinh()
    {
        var a = new PipelineOptions();
        var b = new PipelineOptions();
        var aProvider = new DocxHeaderExtractor.Infrastructure.AI.InferenceProviderSelection();
        var bProvider = new DocxHeaderExtractor.Infrastructure.AI.InferenceProviderSelection();
        bProvider.LocalModel.Seed = aProvider.LocalModel.Seed + 1;

        Assert.NotEqual(
            PrecisionCalibrationProfile.ConfigurationFor(a, aProvider),
            PrecisionCalibrationProfile.ConfigurationFor(b, bProvider));
    }

    /// <summary>
    /// R1 rút đoạn ra khỏi luồng LLM nên nó đổi hẳn phân phối dự đoán — xem §10. Hai lượt khác cờ
    /// này mà chung một profile thì profile học trên hai phân phối trộn lẫn.
    /// </summary>
    [Fact]
    public void Co_style_auto_assign_lam_doi_chu_ky_cau_hinh()
    {
        var a = new PipelineOptions();
        var b = new PipelineOptions { StyleAutoAssign = true };

        Assert.NotEqual(
            PrecisionCalibrationProfile.ConfigurationFor(a),
            PrecisionCalibrationProfile.ConfigurationFor(b));
    }

    [Fact]
    public void Auto_detect_document_mode_lam_doi_chu_ky_cau_hinh()
    {
        var a = new PipelineOptions();
        // a dùng mặc định (BẬT, có chốt đoạn gộp — §101), nên b phải TẮT để hai chữ ký khác nhau.
        var b = new PipelineOptions { AutoDetectDocumentMode = false };

        Assert.NotEqual(
            PrecisionCalibrationProfile.ConfigurationFor(a),
            PrecisionCalibrationProfile.ConfigurationFor(b));
    }

    [Fact]
    public void Critic_threshold_lam_doi_chu_ky_cau_hinh()
    {
        var a = new PipelineOptions();
        var b = new PipelineOptions { ModelCriticWeakEvidenceThreshold = 0.42 };

        Assert.NotEqual(
            PrecisionCalibrationProfile.ConfigurationFor(a),
            PrecisionCalibrationProfile.ConfigurationFor(b));
    }

    [Fact]
    public void Evidence_tiers_lam_doi_chu_ky_cau_hinh()
    {
        var a = new PipelineOptions();
        var b = new PipelineOptions { EvidenceConfidenceTiers = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6] };

        Assert.NotEqual(
            PrecisionCalibrationProfile.ConfigurationFor(a),
            PrecisionCalibrationProfile.ConfigurationFor(b));
    }

    /// <summary>Cùng cấu hình thì phải cùng chữ ký — nếu không, mọi profile đều tự vô hiệu.</summary>
    [Fact]
    public void Cung_cau_hinh_thi_cung_chu_ky()
    {
        Assert.Equal(
            PrecisionCalibrationProfile.ConfigurationFor(new PipelineOptions()),
            PrecisionCalibrationProfile.ConfigurationFor(new PipelineOptions()));
    }
}
