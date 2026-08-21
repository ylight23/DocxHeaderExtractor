using DocxHeaderExtractor.Cli;

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
}
