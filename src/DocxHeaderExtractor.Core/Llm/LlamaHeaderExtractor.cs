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
public sealed class LlamaHeaderExtractor : IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;
    private readonly StatelessExecutor _executor;
    private readonly LlamaOptions _options;
    private readonly bool _hasBuiltInTemplate;

    /// <summary>Số token dành sẵn cho system prompt (kèm ví dụ one-shot) và phần đệm template.</summary>
    private const int SystemPromptReserve = 800;

    public string ModelName { get; }
    public int ContextSize => (int)(_modelParams.ContextSize ?? 0);

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

    public static async Task<LlamaHeaderExtractor> LoadAsync(LlamaOptions options, CancellationToken ct = default)
    {
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

        bool hasTemplate = weights.Metadata.TryGetValue("tokenizer.chat_template", out var tpl)
                           && !string.IsNullOrWhiteSpace(tpl);

        return new LlamaHeaderExtractor(weights, modelParams, options, hasTemplate);
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

    /// <summary>Chạy một khối XML tinh gọn, trả về các mục mô hình cho là tiêu đề.</summary>
    public async Task<ChunkResult> ClassifyAsync(
        string chunkXml,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(HeaderPrompt.System, HeaderPrompt.BuildUser(chunkXml));

        var grammar = _options.GrammarMode switch
        {
            GrammarMode.Enumerated => new Grammar(HeaderPrompt.BuildEnumeratedGbnf(allowedIndexes), HeaderPrompt.GrammarRoot),
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
            maxTokens = Math.Clamp(allowedIndexes.Count * 16 + 32, _options.MaxOutputTokens, Math.Max(256, headroom));
        }

        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = [.. HeaderPrompt.AntiPrompts],
            SamplingPipeline = pipeline,
        };

        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        await foreach (var token in _executor.InferAsync(prompt, inferenceParams, ct))
            sb.Append(token);
        sw.Stop();

        var raw = sb.ToString();
        var parsed = ModelJson.Parse(raw);

        // Chốt chặn chống ảo giác: bỏ mọi chỉ số không nằm trong khối, kẹp cấp về 1..9, khử trùng lặp.
        var seen = new HashSet<int>();
        var kept = new List<ModelHeading>();
        int rejected = 0;

        foreach (var h in parsed)
        {
            if (!allowedIndexes.Contains(h.Index)) { rejected++; continue; }
            if (!seen.Add(h.Index)) continue;
            h.Level = Math.Clamp(h.Level, 1, 9);
            kept.Add(h);
        }

        return new ChunkResult(kept, raw, rejected, sw.ElapsedMilliseconds);
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

    public void Dispose() => _weights.Dispose();
}

public sealed record ChunkResult(
    IReadOnlyList<ModelHeading> Headings,
    string RawOutput,
    int RejectedIndexes,
    long ElapsedMs);
