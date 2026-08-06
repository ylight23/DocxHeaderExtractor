using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Giá trị mặc định đọc thẳng từ <see cref="LlamaOptions"/> và <see cref="ExtractionOptions"/>,
/// để giao diện không tự chép hằng số rồi lệch khỏi CLI khi Core đổi.
/// </summary>
public sealed record Defaults(
    int ChunkTokens,
    int ChunkCandidates,
    double Threshold,
    bool StructuralOnly,
    int GpuLayers,
    bool GpuBackend,
    bool OpenRouterAvailable,
    string OpenRouterModel,
    string LmStudioEndpoint,
    string LmStudioModel,
    int LmStudioContextSize)
{
    public static Defaults Current()
    {
        var llama = new LlamaOptions();
        var chunking = new ChunkingOptions();
        var extraction = new ExtractionOptions();
        var gpu = HasGpuBackend();
        var lmStudio = LmStudioOptions.FromEnvironment();
        return new Defaults(
            ChunkTokens: chunking.TokenBudget,
            // 6 là mức cân bằng giữa số request và độ chính xác ID/cấp trên Qwen 7B.
            ChunkCandidates: chunking.MaxCandidatesPerChunk,
            Threshold: extraction.CandidateThreshold,
            // Đo được: bật luật từ ngữ không đổi kết quả trên cả hai bộ test, nhưng luật loại
            // chú thích có thể chém nhầm tiêu đề dạng "Bảng 2 cột dữ liệu" mà không cho gỡ.
            StructuralOnly: true,
            // Mặc định bảo thủ cho GPU 4 GB: Qwen 7B Q4 không vừa nếu offload 99 lớp, Vulkan sẽ
            // tràn sang shared RAM và chậm dần. Máy 8 GB+ có thể đặt DHX_GPU_LAYERS=99.
            GpuLayers: gpu ? GpuLayersFromEnvironment(defaultValue: 20) : 0,
            GpuBackend: gpu,
            OpenRouterAvailable: !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")),
            OpenRouterModel: Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "qwen/qwen-2.5-7b-instruct",
            LmStudioEndpoint: lmStudio.Endpoint.GetLeftPart(UriPartial.Authority),
            LmStudioModel: lmStudio.Model,
            LmStudioContextSize: lmStudio.ContextSize);
    }

    private static int GpuLayersFromEnvironment(int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable("DHX_GPU_LAYERS"), out var layers) && layers >= 0
            ? layers
            : defaultValue;

    /// <summary>
    /// Backend GPU chỉ có mặt khi build với <c>-p:UseVulkan=true</c> hoặc <c>-p:UseCuda=true</c>:
    /// gói backend đổ native lib vào <c>runtimes/&lt;rid&gt;/native/vulkan</c> (hoặc <c>cuda12</c>),
    /// bản CPU không có thư mục đó. Dò theo thư mục thay vì thử nạp native lib, vì việc nạp phải
    /// xảy ra đúng một lần cho cả tiến trình và đã do LlamaHeaderExtractor giữ.
    /// </summary>
    private static bool HasGpuBackend()
    {
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (!Directory.Exists(runtimes)) return false;

        return Directory.EnumerateDirectories(runtimes, "vulkan", SearchOption.AllDirectories).Any()
            || Directory.EnumerateDirectories(runtimes, "cuda*", SearchOption.AllDirectories).Any();
    }
}
