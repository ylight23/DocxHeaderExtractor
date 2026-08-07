using DocxHeaderExtractor.Core.Llm;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Log mô tả runtime phải nói backend THẬT SỰ nạp được, không nói lại cờ người dùng gõ.
/// <para>
/// Đã dẫn tới kết luận sai hai lần trong dự án: bản dựng CUDA thiếu `cudart64_12.dll` và bản dựng
/// Vulkan trên máy không có `vulkan-1.dll` dùng được đều rơi về CPU trong im lặng, trong khi log vẫn
/// in "GPU 20 lớp". Thứ lộ ra sự thật là thời gian mỗi khối (48 s thay vì 2,7 s), không phải log —
/// nên suýt có một lượt đo CPU được ghi vào bảng như số GPU.
/// </para>
/// </summary>
public sealed class RuntimeDescriptionTests
{
    [Fact]
    public void Xin_GPU_ma_native_khong_offload_duoc_thi_log_phai_noi_dang_chay_CPU()
    {
        var text = LlamaHeaderExtractor.Describe(
            gpuLayers: 20, threads: 8, supportsOffload: false, hasTemplate: true);

        Assert.Contains("đang chạy CPU", text);
        Assert.Contains("ĐÃ YÊU CẦU GPU 20 lớp", text);
        Assert.DoesNotContain("GPU 20 lớp,", text); // không được đọc thành một lời khẳng định GPU
    }

    [Fact]
    public void Xin_GPU_va_native_offload_duoc_thi_bao_GPU()
    {
        var text = LlamaHeaderExtractor.Describe(
            gpuLayers: 20, threads: 8, supportsOffload: true, hasTemplate: true);

        Assert.StartsWith("GPU 20 lớp", text);
        Assert.DoesNotContain("CPU", text);
    }

    [Fact]
    public void Khong_xin_GPU_thi_khong_canh_bao_gi()
    {
        var text = LlamaHeaderExtractor.Describe(
            gpuLayers: 0, threads: 4, supportsOffload: false, hasTemplate: false);

        Assert.StartsWith("CPU 4 luồng", text);
        Assert.DoesNotContain("ĐÃ YÊU CẦU", text);
        Assert.Contains("Llama-3 dựng tay", text);
    }
}
