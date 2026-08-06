using System.Net.Http.Headers;
using System.Text.Json;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Mcp;

/// <summary>
/// Composition service của MCP adapter. Agent harness vẫn sở hữu workflow; lớp này chỉ khóa
/// configuration, tái sử dụng HTTP connection và chuyển kết quả thành hợp đồng MCP nhỏ gọn.
/// </summary>
public sealed class McpExtractionService : IDisposable
{
    private readonly DhxMcpOptions _options;
    private readonly McpPathPolicy _paths;
    private readonly DocumentAgentHarnessFactory _harnessFactory;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public McpExtractionService(
        DhxMcpOptions options,
        McpPathPolicy paths,
        DocumentAgentHarnessFactory harnessFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _harnessFactory = harnessFactory ?? throw new ArgumentNullException(nameof(harnessFactory));
    }

    public async Task<McpBackendStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (_options.RulesOnly)
            return new McpBackendStatus(true, "rules-only", "local", null, [], _options.AllowedRoots,
                "Parser, rule và validator chạy cục bộ; LLM đã tắt bởi DHX_MCP_RULES_ONLY.");

        LmStudioOptions lm;
        try
        {
            lm = LmStudioOptions.FromEnvironment();
            lm.Validate(requireModel: false);
            var models = await ListModelsAsync(lm, ct);
            var selected = SelectModel(lm.Model, models, throwWhenAmbiguous: false);
            var ready = selected is not null;
            return new McpBackendStatus(
                ready,
                "lmstudio",
                lm.Endpoint.GetLeftPart(UriPartial.Authority),
                selected,
                models,
                _options.AllowedRoots,
                ready
                    ? $"LM Studio sẵn sàng với model {selected}."
                    : models.Count == 0
                        ? "LM Studio đã kết nối nhưng /v1/models không có model."
                        : "Có nhiều model; đặt LMSTUDIO_MODEL để chọn rõ ràng.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or
                                   InvalidOperationException)
        {
            var endpoint = Environment.GetEnvironmentVariable("LMSTUDIO_ENDPOINT")
                           ?? "http://127.0.0.1:1234/v1/chat/completions";
            return new McpBackendStatus(false, "lmstudio", endpoint, null, [], _options.AllowedRoots,
                $"Không kết nối được LM Studio API: {Safe(ex.Message)}");
        }
    }

    public async Task<McpExtractionResult> ExtractAsync(string inputPath, CancellationToken ct = default)
    {
        var resolved = _paths.ResolveReadableDocument(inputPath);
        var pipeline = BuildPipelineOptions();

        IHeaderClassifier? classifier = null;
        if (!_options.RulesOnly)
        {
            var lm = pipeline.LmStudio;
            var models = await ListModelsAsync(lm, ct);
            lm.Model = SelectModel(lm.Model, models, throwWhenAmbiguous: true)!;
            classifier = new LmStudioHeaderExtractor(_http, lm);
        }

        using var extractionTool = classifier is null
            ? new PipelineDocumentExtractionTool(pipeline)
            : new PipelineDocumentExtractionTool(pipeline, classifier);
        var harness = _harnessFactory.Create(extractionTool);
        var run = await harness.RunAsync(new DocumentAgentRequest(resolved), ct);
        var outline = run.Outline;

        return new McpExtractionResult(
            run.RunId.ToString("D"),
            run.Outcome.ToString(),
            Path.GetFileName(resolved),
            _options.RulesOnly ? "rules-only" : "lmstudio",
            outline.ParagraphCount,
            outline.CandidateCount,
            outline.Headings.Count,
            run.RequiresReview,
            run.RepairAttempts,
            outline.ElapsedMs,
            outline.Model,
            outline.Headings.Select(h => new McpHeadingResult(
                h.Index, h.StableId, h.Level, h.Text, h.Source.ToString(), h.Confidence,
                h.DecisionStatus.ToString(), h.Disputed, h.ModelConfirmed, h.CriticConfirmed, h.Evidence)).ToArray(),
            run.Trace.Select(e => new McpTraceResult(
                e.Sequence, e.Stage, e.Kind.ToString(), e.Message)).ToArray());
    }

    private PipelineOptions BuildPipelineOptions()
    {
        var lm = LmStudioOptions.FromEnvironment();
        lm.Validate(requireModel: false);

        var maxOutput = Math.Min(768, lm.MaxOutputTokens);
        var maxChunk = Math.Max(400, lm.ContextSize - maxOutput - LlamaOptions.FixedPromptTokens);
        var chunk = Math.Min(5_000, maxChunk);

        return new PipelineOptions
        {
            Backend = InferenceBackend.LmStudio,
            DisableLlm = _options.RulesOnly,
            LmStudio = lm,
            Llama = new LlamaOptions
            {
                ContextSize = checked((uint)lm.ContextSize),
                
                MaxOutputTokens = maxOutput,
            },
            // Giữ batch vừa đủ lớn để giảm số request nhưng không làm Qwen nhầm ID/cấp khi mỗi
            // ứng viên mang theo lân cận. Với context 4096, 5–6 ứng viên ổn định hơn 12.
            Chunking = BuildChunking(chunk),
            ReviewAllParagraphs = false,
            TrustStyles = true,
            // Heading built-in đã có bằng chứng OOXML chắc chắn; giữ trong context
            // làm mốc nhưng không gửi lại cho LLM như ứng viên cần quyết định.
            SkipStyledCandidates = true,
            AuditNumbering = true,
            RecoverNumberedSiblings = true,
            GlobalHierarchy = true,
        };
    }

    private async Task<IReadOnlyList<string>> ListModelsAsync(LmStudioOptions lm, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, lm.ModelsEndpoint);
        if (!string.IsNullOrWhiteSpace(lm.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lm.ApiKey);

        using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, discoveryTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"LM Studio /v1/models không phản hồi trong 5 giây tại {lm.ModelsEndpoint.Authority}.");
        }
        using (response)
        {
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"LM Studio trả {(int)response.StatusCode} {response.ReasonPhrase}: {Safe(content)}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("LM Studio /v1/models không có mảng data.");

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        }
    }

    private static string? SelectModel(
        string configured,
        IReadOnlyList<string> models,
        bool throwWhenAmbiguous)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (models.Contains(configured, StringComparer.Ordinal)) return configured;
            throw new InvalidOperationException(
                $"LMSTUDIO_MODEL='{configured}' không có trong /v1/models. Model hiện có: " +
                (models.Count == 0 ? "<không có>" : string.Join(", ", models)));
        }

        if (models.Count == 1) return models[0];
        if (!throwWhenAmbiguous) return null;
        if (models.Count == 0)
            throw new InvalidOperationException("LM Studio /v1/models không có model nào đang khả dụng.");
        throw new InvalidOperationException(
            "LM Studio có nhiều model; đặt LMSTUDIO_MODEL bằng đúng một ID từ /v1/models: " +
            string.Join(", ", models));
    }

    private static string Safe(string value)
    {
        var oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Tham số MCP là yêu cầu tường minh, nên khoá lại để pipeline không suy lại từ context.</summary>
    private static ChunkingOptions BuildChunking(int chunkTokens)
    {
        var chunking = new ChunkingOptions { MaxCandidatesPerChunk = 6 };
        chunking.SetExplicitTokenBudget(chunkTokens);
        return chunking;
    }
}
