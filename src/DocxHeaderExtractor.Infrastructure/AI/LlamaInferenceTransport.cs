using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Chunking;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// Bọc LLamaSharp: nạp mô hình .gguf lượng tử hoá, chạy suy luận trên CPU cho từng khối XML.
/// Dùng <see cref="StatelessExecutor"/> nên mỗi khối là một lượt độc lập, không bị nhiễm ngữ cảnh khối trước.
/// </summary>
public sealed class LlamaInferenceTransport : IInferenceTransport
{
    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;
    private readonly StatelessExecutor _executor;
    private readonly LocalModelOptions _options;
    private readonly bool _hasBuiltInTemplate;
    private readonly bool _usesQwen35Template;

    public string ModelName { get; }
    public int ContextSize => (int)(_modelParams.ContextSize ?? 0);
    // Nguồn template nằm trong mô tả runtime vì nó quyết định chuỗi token thật sự đưa vào model:
    // rơi về template Llama-3 dựng tay trong khi đang chạy Qwen là lệch hẳn định dạng hội thoại,
    // mà trước đây không có cách nào nhìn thấy điều đó từ log.
    /// <summary>
    /// Mô tả backend THẬT SỰ nạp được, không phải cờ người dùng gõ.
    /// <para>
    /// Bản cũ in "GPU {GpuLayerCount} lớp" ngay khi cờ khác 0. Nhưng thư viện native có thể rơi về
    /// CPU trong im lặng — bản dựng CUDA thiếu `cudart64_12.dll`, hoặc bản dựng Vulkan chạy trên máy
    /// không có `vulkan-1.dll` dùng được. Khi đó log nói "GPU 20 lớp" còn thực tế mỗi khối mất 48 s
    /// thay vì 2,7 s. ĐÃ DẪN TỚI KẾT LUẬN SAI HAI LẦN trong dự án này: một lượt đo CPU suýt được ghi
    /// vào bảng như số GPU, và thứ lộ ra sự thật là thời gian mỗi khối chứ không phải log.
    /// </para>
    /// <para>
    /// <c>llama_supports_gpu_offload()</c> hỏi chính thư viện native đã nạp, nên nó trả lời được câu
    /// "có offload được không" mà cờ không trả lời được. Cùng nguyên tắc với
    /// <c>RunProvenanceValidator</c>: đối chiếu lời hứa bằng cái đã xảy ra.
    /// </para>
    /// </summary>
    public string RuntimeDescription => Describe(
        _options.GpuLayerCount, _options.Threads ?? DefaultThreads(),
        SupportsGpuOffload(), _hasBuiltInTemplate);

    /// <summary>
    /// Phần thuần của <see cref="RuntimeDescription"/>, tách ra để kiểm được mà không phải nạp
    /// 4,4 GB trọng số — nếu không thì đúng cái nhánh "đã yêu cầu GPU nhưng rơi về CPU" sẽ không bao
    /// giờ có test, vì nó chỉ xảy ra trên máy thiếu thư viện native.
    /// </summary>
    internal static string Describe(int gpuLayers, int threads, bool supportsOffload, bool hasTemplate)
    {
        var template = hasTemplate ? ", chat template của GGUF" : ", chat template Llama-3 dựng tay";
        if (gpuLayers <= 0) return $"CPU {threads} luồng" + template;

        return (supportsOffload
                   ? $"GPU {gpuLayers} lớp"
                   : $"CPU {threads} luồng — ĐÃ YÊU CẦU GPU {gpuLayers} lớp nhưng thư viện native "
                     + "không hỗ trợ offload, đang chạy CPU")
               + template;
    }

    private static bool SupportsGpuOffload()
    {
        try
        {
            return NativeApi.llama_supports_gpu_offload();
        }
        catch (Exception)
        {
            // Không hỏi được thì đừng khẳng định gì: giữ nguyên cách đọc cũ còn hơn báo sai chiều.
            return true;
        }
    }

    private LlamaInferenceTransport(
        LLamaWeights weights,
        ModelParams modelParams,
        LocalModelOptions options,
        bool hasTemplate,
        bool usesQwen35Template)
    {
        _weights = weights;
        _modelParams = modelParams;
        _options = options;
        _hasBuiltInTemplate = hasTemplate;
        _usesQwen35Template = usesQwen35Template;
        _executor = new StatelessExecutor(weights, modelParams) { ApplyTemplate = false };
        ModelName = Path.GetFileName(options.ModelPath);
    }

    private static int _logConfigured;

    internal enum NativeBackendPreference { Default, Cuda, Vulkan }

    internal static NativeBackendPreference SelectNativeBackend(
        int gpuLayerCount,
        bool hasCudaBackend,
        bool hasVulkanBackend) =>
        gpuLayerCount <= 0 ? NativeBackendPreference.Default
        : hasCudaBackend ? NativeBackendPreference.Cuda
        : hasVulkanBackend ? NativeBackendPreference.Vulkan
        : NativeBackendPreference.Default;

    /// <summary>
    /// Chặn log của llama.cpp. Phải gọi TRƯỚC lần chạm native đầu tiên.
    /// LLamaLogLevel xếp Debug=1 &lt; Info=2 &lt; Warning=3 &lt; Error=4, nên điều kiện là ≥ Warning.
    /// <para>
    /// LLamaSharp chỉ cho cấu hình MỘT LẦN cho mỗi tiến trình: gọi lại sau khi native lib đã nạp
    /// sẽ ném lỗi. Tiến trình chạy một lượt (CLI) không thấy, nhưng máy chủ web gọi lại ở mỗi
    /// request thì request thứ hai trở đi chết. Chốt bằng cờ, và vẫn nuốt lỗi phòng khi có thành
    /// phần khác đã nạp native lib trước.
    /// </para>
    /// </summary>
    public static void ConfigureNativeLogging(bool verbose, int gpuLayerCount = 0)
    {
        if (Interlocked.Exchange(ref _logConfigured, 1) == 1) return;

        try
        {
            var nativeRoot = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
            var preferred = SelectNativeBackend(
                gpuLayerCount,
                File.Exists(Path.Combine(nativeRoot, "cuda12", "llama.dll")),
                File.Exists(Path.Combine(nativeRoot, "vulkan", "llama.dll")));

            // LLamaSharp defaults both CUDA and Vulkan preferences to true. If a stale or partial
            // Vulkan folder is present beside a CUDA build, it tries Vulkan first and silently falls
            // back to CPU. Choose exactly the backend shipped with this executable.
            if (preferred == NativeBackendPreference.Cuda)
            {
                var cudaDir = Path.Combine(nativeRoot, "cuda12");
                PreloadCudaDependencies(nativeRoot, cudaDir);
                NativeLibraryConfig.All.WithLibrary(
                    Path.Combine(cudaDir, "llama.dll"),
                    Path.Combine(cudaDir, "mtmd.dll"));
            }
            else if (preferred == NativeBackendPreference.Vulkan)
            {
                NativeLibraryConfig.All.WithCuda(false);
                NativeLibraryConfig.All.WithVulkan(true);
            }

            NativeLibraryConfig.All.WithLogCallback((level, message) =>
            {
                if (verbose || (level >= LLamaLogLevel.Warning && level != LLamaLogLevel.Continue))
                    Console.Error.Write(message);
            });
        }
        catch (InvalidOperationException)
        {
            // Native lib đã nạp — không đổi được cấu hình nữa, nhưng cũng không phải lỗi chí mạng.
        }
    }

    private static void PreloadCudaDependencies(string nativeRoot, string cudaDir)
    {
        var cpuDir = Path.Combine(nativeRoot,
            Avx512F.IsSupported ? "avx512" : Avx2.IsSupported ? "avx2" : Avx.IsSupported ? "avx" : "noavx");
        var dependencies = new[]
        {
            // The CUDA package places these beside the executable, while ggml-cuda lives
            // under runtimes/. Load them explicitly so Windows resolves its imports reliably.
            Path.Combine(AppContext.BaseDirectory, "cudart64_12.dll"),
            Path.Combine(AppContext.BaseDirectory, "cublasLt64_12.dll"),
            Path.Combine(AppContext.BaseDirectory, "cublas64_12.dll"),
            Path.Combine(cudaDir, "ggml-base.dll"),
            Path.Combine(cpuDir, "ggml-cpu.dll"),
            Path.Combine(cudaDir, "ggml-cuda.dll"),
            Path.Combine(cudaDir, "ggml.dll"),
        };

        foreach (var dependency in dependencies)
        {
            if (!File.Exists(dependency))
                throw new FileNotFoundException("Thiếu native dependency cho CUDA backend.", dependency);
            NativeLibrary.Load(dependency);
        }
    }

    /// <summary>
    /// Nạp weights. Làm việc trên BẢN SAO của <paramref name="options"/>; chỉ context đã chốt được
    /// ghi ngược lại để người gọi thấy đúng con số đã dùng.
    /// </summary>
    public static async Task<LlamaInferenceTransport> LoadAsync(LocalModelOptions options, CancellationToken ct = default)
    {
        // Giữ tham chiếu bản GỐC để ghi lại context đã CHỐT (xem khối AutoContextSize bên dưới).
        var caller = options;
        options = options.Clone();
        options.Validate();

        ConfigureNativeLogging(options.VerboseNativeLog, options.GpuLayerCount);

        var modelParams = new ModelParams(options.ModelPath)
        {
            ContextSize = options.ContextSize,
            GpuLayerCount = options.GpuLayerCount,
            Threads = options.Threads ?? DefaultThreads(),
            BatchThreads = options.BatchThreads ?? options.Threads ?? DefaultThreads(),
            BatchSize = options.BatchSize,
            UseMemorymap = true,
        };

        var weights = await LLamaWeights.LoadFromFileAsync(modelParams, ct);

        // Context đọc từ chính GGUF thay vì từ một allowlist tên model. Chỉ NÂNG, không bao giờ
        // hạ: sàn do người dùng/profile đặt vẫn được tôn trọng.
        if (options.AutoContextSize && DeclaredContextLength(weights.Metadata) is { } declared)
        {
            var target = Math.Min(declared, LocalModelOptions.MaxAutoContextSize);
            if (target > modelParams.ContextSize)
            {
                options.ContextSize = target;
                modelParams.ContextSize = target;
                caller.ContextSize = target;
            }
        }

        bool hasTemplate = weights.Metadata.TryGetValue("tokenizer.chat_template", out var tpl)
                           && !string.IsNullOrWhiteSpace(tpl);
        var usesQwen35Template = weights.Metadata.TryGetValue("general.architecture", out var architecture)
                                  && string.Equals(architecture, "qwen35", StringComparison.OrdinalIgnoreCase);

        var extractor = new LlamaInferenceTransport(weights, modelParams, options, hasTemplate, usesQwen35Template);

        return extractor;
    }

    /// <summary>Không có prefix dùng chung giữa các lượt gọi.</summary>
    public int SharedPrefixTokens => 0;

    /// <summary>
    /// <c>{arch}.context_length</c> của GGUF, ví dụ <c>qwen35.context_length = 262144</c>. Không
    /// hardcode tên kiến trúc: đọc <c>general.architecture</c> rồi ghép, nên chạy với model mới mà
    /// không phải sửa gì.
    /// </summary>
    internal static uint? DeclaredContextLength(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("general.architecture", out var arch) || string.IsNullOrWhiteSpace(arch))
            return null;
        if (!metadata.TryGetValue($"{arch}.context_length", out var raw)) return null;
        return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
               && value > 0 ? value : null;
    }

    /// <summary>
    /// llama.cpp chạy chậm đi khi số luồng vượt số nhân vật lý (siêu phân luồng làm tranh chấp
    /// đơn vị SIMD). Ước lượng nhân vật lý = một nửa số luồng logic khi máy có SMT.
    /// </summary>
    public static int DefaultThreads()
    {
        var logical = Environment.ProcessorCount;
        return logical > 4 ? Math.Max(2, logical / 2) : Math.Max(1, logical);
    }

    /// <summary>
    /// Xem <see cref="IInferenceTransport.BoundaryCutAsync"/>. Stateless, không grammar, không
    /// prefix cache; sampler greedy (Temperature=0, TopK=1, Seed cố định) để tái lập được.
    /// </summary>
    public async Task<string> BoundaryCutAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default,
        int expectedItemCount = 0)
    {
        var prompt = BuildBoundaryPrompt(systemPrompt, userMessage);
        using var pipeline = new DefaultSamplingPipeline { Temperature = 0f, TopK = 1, Seed = _options.Seed };
        var inferenceParams = new InferenceParams
        {
            // Besides the short title/body cut, this narrow interface powers the PDF
            // semantic analysts. A 12-block strict JSON response needs more than 120 tokens.
            MaxTokens = 512,
            AntiPrompts = ["<|eot_id|>", "\n\n"],
            SamplingPipeline = pipeline,
        };

        var sb = new StringBuilder();
        await foreach (var token in _executor.InferAsync(prompt, inferenceParams, ct))
            sb.Append(token);
        return sb.ToString().Trim();
    }

    private string BuildPrompt(string system, string user)
    {
        if (!_hasBuiltInTemplate) return BuildLlama3Prompt(system, user);

        try
        {
            var template = new LLamaTemplate(_weights, strict: false) { AddAssistant = true };
            template.Add("system", system);
            template.Add("user", user);
            return Encoding.UTF8.GetString(template.Apply());
        }
        catch (Exception)
        {
            // GGUF có template nhưng llama.cpp không render được → quay về template Llama 3 dựng tay.
            return BuildLlama3Prompt(system, user);
        }
    }

    private string BuildBoundaryPrompt(string system, string user)
    {
        if (!_usesQwen35Template) return BuildPrompt(system, user);

        // LLamaSharp 0.27 cannot pass chat-template kwargs. Qwen3.5's documented non-thinking
        // form is an assistant turn with an already-closed empty thought, which preserves tokens
        // for the strict JSON returned by PDF analysts.
        return $"<|im_start|>system\n{system}<|im_end|>\n" +
               $"<|im_start|>user\n{user}<|im_end|>\n" +
               "<|im_start|>assistant\n<think>\n\n</think>\n\n";
    }

    /// <summary>Template Llama 3 dựng tay, dùng khi GGUF không kèm chat template dùng được.</summary>
    private static string BuildLlama3Prompt(string system, string user)
    {
        var sb = new StringBuilder();
        sb.Append("<|start_header_id|>system<|end_header_id|>\n\n");
        sb.Append(system).Append("<|eot_id|>");
        sb.Append("<|start_header_id|>user<|end_header_id|>\n\n");
        sb.Append(user).Append("<|eot_id|>");
        sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
        return sb.ToString();
    }

    public void Dispose() => _weights.Dispose();
}
