using System.Globalization;
using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace DocxHeaderExtractor.Core.Llm;

/// <summary>
/// Bọc LLamaSharp: nạp mô hình .gguf lượng tử hoá, chạy suy luận trên CPU cho từng khối XML.
/// Dùng <see cref="StatelessExecutor"/> nên mỗi khối là một lượt độc lập, không bị nhiễm ngữ cảnh khối trước.
/// </summary>
public sealed class LlamaHeaderExtractor : IHeaderClassifier
{
    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;
    private readonly StatelessExecutor _executor;
    private readonly LlamaOptions _options;
    private readonly bool _hasBuiltInTemplate;
    private PrefixCachedRunner? _prefixRunner;

    /// <summary>Số token dành sẵn cho system prompt (kèm ví dụ one-shot) và phần đệm template.</summary>
    private const int SystemPromptReserve = LlamaOptions.FixedPromptTokens;

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

    /// <summary>
    /// Đếm token THẬT bằng tokenizer của chính mô hình đang nạp.
    /// <para>
    /// Cần thiết vì ước lượng theo ký tự lệch rất xa và lệch KHÔNG ĐỀU: đo trên Qwen2.5-7B,
    /// prompt cố định đạt 3.10 ký tự/token còn thân bài tiếng Việt chỉ 1.85. Ngân sách khối
    /// tính bằng đơn vị ước lượng sẽ vượt cửa sổ ngữ cảnh đúng ở tài liệu tiếng Việt dày chữ.
    /// </para>
    /// </summary>
    public int CountTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : _weights.Tokenize(text, false, false, Encoding.UTF8).Length;

    private LlamaHeaderExtractor(LLamaWeights weights, ModelParams modelParams, LlamaOptions options, bool hasTemplate)
    {
        _weights = weights;
        _modelParams = modelParams;
        _options = options;
        _hasBuiltInTemplate = hasTemplate;
        _executor = new StatelessExecutor(weights, modelParams) { ApplyTemplate = false };
        ModelName = Path.GetFileName(options.ModelPath);
    }

    private static int _logConfigured;

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
    public static void ConfigureNativeLogging(bool verbose)
    {
        if (verbose) return;
        if (Interlocked.Exchange(ref _logConfigured, 1) == 1) return;

        try
        {
            NativeLibraryConfig.All.WithLogCallback((level, message) =>
            {
                if (level >= LLamaLogLevel.Warning && level != LLamaLogLevel.Continue)
                    Console.Error.Write(message);
            });
        }
        catch (InvalidOperationException)
        {
            // Native lib đã nạp — không đổi được cấu hình nữa, nhưng cũng không phải lỗi chí mạng.
        }
    }

    /// <summary>
    /// Nạp weights. Profile model được áp lên BẢN SAO: cả hai người gọi (pipeline qua
    /// <c>PrepareLocalModelProfile</c>, web qua <c>LlamaModelCache</c>) đều đã áp profile lên
    /// <c>ChunkingOptions</c> thật trước khi tới đây, nên đây chỉ còn là lưới an toàn cho người gọi
    /// thứ ba. Áp lên chính <paramref name="options"/> thì một hàm tên "Load" lại âm thầm sửa
    /// context/ngân sách khối của đối tượng người gọi đang giữ và dùng lại cho lượt chạy sau.
    /// </summary>
    public static async Task<LlamaHeaderExtractor> LoadAsync(LlamaOptions options, CancellationToken ct = default)
    {
        // Giữ tham chiếu bản GỐC để ghi lại context đã CHỐT (xem khối AutoContextSize bên dưới).
        // Không ghi lại thì PrecisionCalibrationProfile.ConfigurationFor đọc bản gốc và ghi
        // ctx=4096 cho lượt chạy thật sự dùng 32768 — chữ ký cấu hình nói dối, và kỷ luật "mọi con
        // số ghi kèm cấu hình đo" mất hiệu lực đúng lúc nó cần nhất.
        var caller = options;
        options = options.Clone();
        options.ApplyRecommendedModelProfile(new Chunking.ChunkingOptions { TokenBudget = options.ChunkTokenBudget });
        options.Validate();

        ConfigureNativeLogging(options.VerboseNativeLog);

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
            var target = Math.Min(declared, LlamaOptions.MaxAutoContextSize);
            if (target > modelParams.ContextSize)
            {
                options.ContextSize = target;
                modelParams.ContextSize = target;
                caller.ContextSize = target;   // để chữ ký cấu hình nói đúng con số đã dùng
            }
        }

        bool hasTemplate = weights.Metadata.TryGetValue("tokenizer.chat_template", out var tpl)
                           && !string.IsNullOrWhiteSpace(tpl);

        var extractor = new LlamaHeaderExtractor(weights, modelParams, options, hasTemplate);

        if (options.ReusePromptPrefix)
        {
            if (RecurrentStateReason(weights.Metadata) is { } reason)
                extractor.PrefixReuseBlockedReason = reason;
            else
                extractor._prefixRunner = await PrefixCachedRunner.CreateAsync(
                    weights, modelParams, extractor.BuildPrompt, ct);
        }

        return extractor;
    }

    /// <summary>Số token của phần prompt dùng chung, 0 nếu không bật tái dùng.</summary>
    public int SharedPrefixTokens => _prefixRunner?.SharedPrefixTokens ?? 0;

    /// <summary>
    /// Vì sao tái dùng prefill bị TỪ CHỐI dù người dùng bật; <c>null</c> nếu không từ chối.
    /// Tách khỏi "cắt không được phần chung" vì hai ca cần hai câu trả lời khác nhau.
    /// </summary>
    public string? PrefixReuseBlockedReason { get; private set; }

    /// <summary>
    /// Mô hình có lớp mang TRẠNG THÁI HỒI QUY không — nếu có thì tái dùng prefill là sai về bản chất,
    /// không phải chậm hay kém tối ưu.
    /// <para>
    /// Tái dùng prefill giữ lại KV của phần prompt chung rồi nối phần riêng của từng khối. Với
    /// attention thuần thì đúng: KV của một token chỉ phụ thuộc các token trước nó. Với lớp
    /// state-space (SSM / linear attention), trạng thái được CUỘN theo toàn bộ chuỗi và không tách
    /// ra thành từng token được, nên "phần chung" không tái dùng được.
    /// </para>
    /// <para>
    /// ĐO ĐƯỢC (§35): trên khoá luận thật với cấu hình mặc định của Web (ctx 8192, 5000 token/khối,
    /// 30 khối), Qwen3.5-9B + tái dùng prefill chết ở khối ĐẦU TIÊN với
    /// <c>llama_decode failed: 'NoKvSlot'</c> — 0/30 khối. Tắt tái dùng, cùng mọi tham số khác:
    /// 30/30 khối chạy hết. Đường CLI không bao giờ chạm phải vì mọi phép đo đều truyền
    /// <c>--no-reuse-prefix</c>; đường Web thì bật mặc định, nên người dùng lãnh trọn.
    /// </para>
    /// <para>
    /// Nhận biết bằng METADATA của GGUF chứ không bằng tên file — tên file là anti-pattern đã bị
    /// <c>ChunkingOptions</c> phê. Khoá <c>{arch}.ssm.*</c> có ở Qwen3.5 (<c>qwen35.ssm.state_size</c>…)
    /// và KHÔNG có ở Qwen2.5, vốn tái dùng prefill bình thường. Luật này vì thế phủ luôn Mamba,
    /// Jamba, Falcon-H1, RWKV và mọi kiến trúc lai sau này, không chỉ riêng <c>qwen35</c>.
    /// </para>
    /// </summary>
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

    internal static string? RecurrentStateReason(IReadOnlyDictionary<string, string> metadata)
    {
        var marker = metadata.Keys.FirstOrDefault(
            k => k.Contains(".ssm.", StringComparison.OrdinalIgnoreCase));
        if (marker is null) return null;

        metadata.TryGetValue("general.architecture", out var arch);
        return $"mô hình {arch ?? "này"} có lớp trạng thái hồi quy ({marker}) — " +
               "phần prompt chung không tách ra tái dùng được";
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

    // Dòng ràng buộc ID được ghép NGAY SAU document view, không phải ở cuối user message như bản
    // RPC: phần đuôi của user message nằm trong prefix cache dùng chung cho mọi khối, nên thứ thay
    // đổi theo khối phải nằm trong phần chunk. Nội dung thông tin là như nhau.
    /// <summary>Chạy một khối XML tinh gọn, trả về các mục mô hình cho là tiêu đề.</summary>
    public async Task<ChunkResult> ClassifyAsync(
        string chunkXml,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct = default)
    {
        var view = HeaderPrompt.WithIdConstraint(chunkXml, allowedIndexes);
        return await ClassifyRolesAsync(
            HeaderPrompt.System, Think(HeaderPrompt.BuildUser(view)), view, allowedIndexes, ct);
    }

    public async Task<ChunkResult> CritiqueAsync(
        string chunkXml,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct = default)
    {
        var view = HeaderPrompt.WithIdConstraint(chunkXml, allowedIndexes);
        return await ClassifyRolesAsync(
            HeaderPrompt.CriticSystem, Think(HeaderPrompt.BuildCriticUser(view)), view, allowedIndexes, ct);
    }

    /// <summary>Chỉ thị bật thinking của họ Qwen3; không có tác dụng với model không hiểu nó.</summary>
    private static readonly string ThinkDirective = Environment.NewLine + Environment.NewLine + "/think";

    /// <summary>
    /// Lượt phân loại KHÔNG bao giờ bật thinking — §24.2 đo được nó làm 5/10 khối trả về rỗng và
    /// recall tụt 10 điểm, vì phải tắt grammar ở đúng nơi recall được quyết định.
    /// </summary>
    private static string Think(string user) => user;

    private async Task<ChunkResult> ClassifyRolesAsync(
        string system,
        string user,
        string chunkXml,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct)
    {
        var grammar = _options.GrammarMode switch
        {
            GrammarMode.Enumerated => new Grammar(HeaderPrompt.BuildRoleEnumeratedGbnf(allowedIndexes), HeaderPrompt.GrammarRoot),
            GrammarMode.Free => new Grammar(HeaderPrompt.Gbnf, HeaderPrompt.GrammarRoot),
            _ => null,
        };

        using var pipeline = new DefaultSamplingPipeline
        {
            Temperature = _options.Temperature,
            TopK = _options.Temperature <= 0 ? 1 : 40,
            TopP = 0.9f,
            Seed = _options.Seed,
            RepeatPenalty = 1.0f,
            Grammar = grammar,
        };

        // Ở chế độ liệt kê, độ dài đầu ra tỉ lệ thuận với số ứng viên nên tính trước được,
        // nhưng vẫn phải chừa chỗ cho prompt trong cửa sổ ngữ cảnh.
        var maxTokens = _options.MaxOutputTokens;
        if (_options.GrammarMode == GrammarMode.Enumerated)
        {
            var headroom = (int)_options.ContextSize - _options.ChunkTokenBudget - SystemPromptReserve;
            var ceiling = Math.Max(64, Math.Min(_options.MaxOutputTokens, headroom));
            // Đa nhãn dài hơn lược đồ i/l cũ, nhưng 24 token/mục vẫn dư rộng. MaxOutputTokens
            // là trần, không phải sàn (code cũ vô tình luôn cấp ít nhất 900 token).
            maxTokens = Math.Clamp(allowedIndexes.Count * 24 + 32, 64, ceiling);
        }

        var sw = Stopwatch.StartNew();
        string raw;

        // Prefix cache chỉ được dựng cho prompt phân loại chính. Critic có system/user khác,
        // nên phải chạy stateless để không tái dùng nhầm KV cache của nhiệm vụ trước.
        if (_prefixRunner is { } runner && system == HeaderPrompt.System)
        {
            raw = await runner.RunAsync(chunkXml, pipeline, maxTokens, ct);
        }
        else
        {
            var prompt = BuildPrompt(system, user);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = [.. HeaderPrompt.AntiPrompts],
                SamplingPipeline = pipeline,
            };

            var sb = new StringBuilder();
            await foreach (var token in _executor.InferAsync(prompt, inferenceParams, ct))
                sb.Append(token);
            raw = sb.ToString();
        }

        sw.Stop();
        var parsed = ModelJson.Parse(raw, includeNonHeadings: true);

        // Chốt chặn chống ảo giác: bỏ mọi chỉ số không nằm trong khối, kẹp cấp về 1..9, khử trùng lặp.
        var seen = new HashSet<int>();
        var kept = new List<ModelHeading>();
        var explicitNonHeadings = new HashSet<int>();
        var rejectedRoles = new Dictionary<int, SemanticRole>();
        int rejected = 0;

        foreach (var h in parsed)
        {
            if (!allowedIndexes.Contains(h.Index)) { rejected++; continue; }
            if (!seen.Add(h.Index)) continue;
            if (h.Level <= 0)
            {
                // uncertain không phải lời bác dứt khoát; hậu kiểm cấu trúc vẫn được phép cứu.
                if (h.Role != SemanticRole.Uncertain)
                {
                    explicitNonHeadings.Add(h.Index);
                    rejectedRoles[h.Index] = h.Role;
                }
                continue;
            }
            h.Level = Math.Clamp(h.Level, 1, 9);
            kept.Add(h);
        }

        return new ChunkResult(kept, raw, rejected, sw.ElapsedMilliseconds, explicitNonHeadings, rejectedRoles);
    }

    /// <summary>
    /// Lượt hai chỉ gán cấp cho heading đã xác nhận. Grammar không cho l=0 nên mô hình không thể
    /// làm mất một heading vì ngữ cảnh chunk trước đó.
    /// </summary>
    public async Task<ChunkResult> ClassifyHierarchyAsync(
        IReadOnlyList<HierarchyItem> headings,
        CancellationToken ct = default)
        => await ClassifyHierarchyAsync([], headings, ct);

    /// <summary>Gán cấp cho batch hiện tại, dùng các heading trước đó làm mốc nhưng không trả lại chúng.</summary>
    public async Task<ChunkResult> ClassifyHierarchyAsync(
        IReadOnlyList<HierarchyItem> context,
        IReadOnlyList<HierarchyItem> headings,
        CancellationToken ct = default)
    {
        var indexes = headings.Select(h => h.Index).ToArray();

        // Thinking CHỈ bật ở lượt này, và grammar tắt CỤC BỘ tại đây.
        //
        // ĐO ĐƯỢC (§24.2): bật thinking cho cả lượt phân loại làm 5/10 khối trả về 0 tiêu đề —
        // recall 96,4% → 86,4% — vì nó tắt grammar ở đúng nơi recall được quyết định. Lượt gán cấp
        // thì khác: tập heading đã chốt xong, lượt này chỉ đổi CẤP nên recall không còn gì để mất.
        // Cùng phép đo cho thấy thinking nâng đúng cấp 66,0% → 70,5%; đây là cách lấy phần đó mà
        // không trả giá.
        var thinking = _options.EnableThinking;
        var prompt = BuildPrompt(
            HeaderPrompt.HierarchySystem,
            HeaderPrompt.BuildHierarchyUser(context, headings) + (thinking ? ThinkDirective : ""));
        using var pipeline = new DefaultSamplingPipeline
        {
            Temperature = 0,
            TopK = 1,
            TopP = 0.9f,
            Seed = _options.Seed,
            RepeatPenalty = 1.0f,
            Grammar = thinking
                ? null
                : new Grammar(HeaderPrompt.BuildEnumeratedGbnf(indexes, allowZero: false), HeaderPrompt.GrammarRoot),
        };
        var parameters = new InferenceParams
        {
            // Thinking cần chỗ cho phần <think> trước JSON; trần cũ tính vừa đủ cho JSON thôi.
            MaxTokens = thinking
                ? _options.MaxOutputTokens
                : Math.Clamp(indexes.Length * 16 + 32, 256, _options.MaxOutputTokens),
            AntiPrompts = [.. HeaderPrompt.AntiPrompts],
            SamplingPipeline = pipeline,
        };
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        await foreach (var token in _executor.InferAsync(prompt, parameters, ct)) sb.Append(token);
        sw.Stop();
        var raw = sb.ToString();
        var parsed = ModelJson.Parse(raw, includeNonHeadings: true);
        return new ChunkResult(parsed, raw, 0, sw.ElapsedMilliseconds, new HashSet<int>());
    }

    /// <summary>
    /// Nhiệm vụ hẹp — xem <see cref="IHeaderClassifier.BoundaryCutAsync"/>. Stateless, không
    /// grammar, không prefix cache (system prompt đổi theo domain nên không có phần chung ổn định
    /// giữa các lượt gọi). Cùng cấu hình sampler greedy với <see cref="ClassifyRolesAsync"/>
    /// (Temperature=0, TopK=1, Seed cố định) để tái lập được số đã đo trong harness thử nghiệm.
    /// </summary>
    public async Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(systemPrompt, userMessage);
        using var pipeline = new DefaultSamplingPipeline { Temperature = 0f, TopK = 1, Seed = _options.Seed };
        var inferenceParams = new InferenceParams
        {
            MaxTokens = 120,
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
        if (!_hasBuiltInTemplate) return HeaderPrompt.BuildLlama3Prompt(system, user);

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
            return HeaderPrompt.BuildLlama3Prompt(system, user);
        }
    }

    public void Dispose()
    {
        _prefixRunner?.Dispose();
        _weights.Dispose();
    }
}

public sealed record ChunkResult(
    IReadOnlyList<ModelHeading> Headings,
    string RawOutput,
    int RejectedIndexes,
    long ElapsedMs,
    IReadOnlySet<int> ExplicitNonHeadings,
    /// <summary>
    /// Vai trò mô hình đã gán cho các đoạn bị bác. Cần thiết vì không phải phủ định nào cũng
    /// đáng tin như nhau: "document_title" gán cho nhiều đoạn trong cùng một tài liệu là mâu
    /// thuẫn tự thân, còn form_label/table_header thì không.
    /// </summary>
    IReadOnlyDictionary<int, SemanticRole>? RejectedRoles = null);

/// <summary>Một heading trong lượt dựng hierarchy toàn cục.</summary>
public sealed record HierarchyItem(
    int Index,
    string Text,
    int? StyleLevel,
    int? OutlineLevel,
    int? HintLevel,
    string? Numbering);
