using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Giá trị mặc định đọc thẳng từ Core và môi trường server,
/// để giao diện không tự chép hằng số rồi lệch khỏi CLI khi Core đổi.
/// </summary>
public sealed record Defaults(
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
        var gpu = HasGpuBackend();
        var lmStudio = RemoteInferenceOptions.FromEnvironment("lmstudio");
        return new Defaults(
            // Mặc định bảo thủ cho GPU 4 GB: Qwen 7B Q4 không vừa nếu offload 99 lớp, Vulkan sẽ
            // tràn sang shared RAM và chậm dần. Máy 8 GB+ có thể đặt DHX_GPU_LAYERS=99.
            GpuLayers: gpu ? GpuLayersFromEnvironment(defaultValue: 20) : 0,
            GpuBackend: gpu,
            OpenRouterAvailable: !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")),
            OpenRouterModel: Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? RemoteInferenceOptions.DefaultModel,
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
    /// xảy ra đúng một lần cho cả tiến trình và đã do LlamaInferenceTransport giữ.
    /// </summary>
    private static bool HasGpuBackend()
    {
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (!Directory.Exists(runtimes)) return false;

        return Directory.EnumerateDirectories(runtimes, "vulkan", SearchOption.AllDirectories).Any()
            || Directory.EnumerateDirectories(runtimes, "cuda*", SearchOption.AllDirectories).Any();
    }
}
