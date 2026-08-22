using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class TocCommandLineOptionsTests
{
    [Fact]
    public void Toc_partial_duoc_bat_bang_co_rieng()
    {
        var o = CommandLineOptions.Parse(["toc-keys", "corpus", "--toc-match-threshold", "0.4", "--toc-partial"]);

        Assert.Equal("toc-keys", o.Command);
        Assert.Equal(0.4, o.TocMatchThreshold);
        Assert.True(o.TocPartial);
    }

    [Fact]
    public void Verify_corrupt_duoc_nhan_dung_lenh_khong_roi_vao_input()
    {
        // Bắt đúng lớp lỗi đã gặp: thêm case vào switch dispatch trong Program.cs nhưng quên thêm tên
        // lệnh vào whitelist ở đây khiến "verify-corrupt" bị coi là input file, Command lặng lẽ giữ
        // "extract" — không có exception, không có cảnh báo (handoff §174).
        var o = CommandLineOptions.Parse([
            "verify-corrupt", "file.docx",
            "--vlm-model", "model.gguf",
            "--vlm-mmproj", "mmproj.gguf",
            "--vlm-gpu-layers", "20",
            "--vlm-context", "8192",
            "--vlm-dpi", "150",
        ]);

        Assert.Equal("verify-corrupt", o.Command);
        Assert.Equal(["file.docx"], o.Inputs);
        Assert.Equal("model.gguf", o.VlmModelPath);
        Assert.Equal("mmproj.gguf", o.VlmMmprojPath);
        Assert.Equal(20, o.VlmGpuLayerCount);
        Assert.Equal(8192, o.VlmContextSize);
        Assert.Equal(150, o.VlmDpi);
    }

    [Fact]
    public void Force_review_package_duoc_nhan_dung_flag()
    {
        var o = CommandLineOptions.Parse(["repair-key-package", "file.docx", "--force-review-package"]);

        Assert.Equal("repair-key-package", o.Command);
        Assert.True(o.ForceReviewPackage);
    }

    [Fact]
    public void Pdf_stage_retrieval_only_khong_goi_analyst_duoc_nhan_dung()
    {
        var o = CommandLineOptions.Parse(["pdf-stage-eval", "file.docx", "--pdf-stage-retrieval-only", "--no-llm"]);

        Assert.Equal("pdf-stage-eval", o.Command);
        Assert.True(o.PdfStageRetrievalOnly);
        Assert.True(o.Pipeline.DisableLlm);
    }

    [Fact]
    public void Pdf_stage_lossless_duoc_nhan_dung()
    {
        var o = CommandLineOptions.Parse(["pdf-stage-eval", "file.docx", "--pdf-stage-lossless"]);

        Assert.True(o.PdfStageLosslessBlocks);
    }

    [Fact]
    public void Pdf_stage_atomic_duoc_nhan_dung()
    {
        var o = CommandLineOptions.Parse(["pdf-stage-eval", "file.docx", "--pdf-stage-atomic"]);

        Assert.True(o.PdfStageAtomicLines);
    }

    [Fact]
    public void Pdf_stage_vlm_duoc_nhan_dung()
    {
        var o = CommandLineOptions.Parse(["pdf-stage-eval", "file.docx", "--pdf-stage-vlm"]);

        Assert.True(o.PdfStageVisualReview);
    }

    [Fact]
    public void Vlm_max_images_duoc_nhan_dung()
    {
        var o = CommandLineOptions.Parse(["pdf-stage-eval", "file.docx", "--vlm-max-images", "4"]);

        Assert.Equal(4, o.VlmMaxImagesPerRequest);
    }

    [Fact]
    public void Vlm_concurrency_duoc_nhan_dung()
    {
        var o = CommandLineOptions.Parse(["pdf-stage-eval", "file.docx", "--vlm-concurrency", "6"]);

        Assert.Equal(6, o.VlmMaxConcurrentRequests);
    }

    [Fact]
    public void Nvidia_uses_vision_capable_openai_compatible_endpoint()
    {
        var o = CommandLineOptions.Parse(["extract", "file.docx", "--nvidia"]);

        Assert.Equal(InferenceBackend.Sglang, o.Pipeline.Backend);
        Assert.Equal("meta/llama-3.2-90b-vision-instruct", o.Pipeline.Sglang.Model);
        Assert.Equal("https://integrate.api.nvidia.com/v1/chat/completions", o.Pipeline.Sglang.Endpoint.ToString());
    }
}
