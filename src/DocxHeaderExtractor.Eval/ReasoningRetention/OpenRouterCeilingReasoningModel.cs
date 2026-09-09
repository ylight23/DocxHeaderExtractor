using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Per-request compact telemetry for the ceiling route (section 16 of the ceiling
/// mission). Never carries the API key or private reasoning text.</summary>
public sealed record RequestPacketTelemetry
{
    [JsonPropertyName("requestIdHash")] public required string RequestIdHash { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("passType")] public required string PassType { get; init; }
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("reasoningEffort")] public required string ReasoningEffort { get; init; }
    [JsonPropertyName("reasoningExcludedFromResponse")] public required bool ReasoningExcludedFromResponse { get; init; }
    [JsonPropertyName("contextLength")] public required int ContextLength { get; init; }
    [JsonPropertyName("maxPromptTokens")] public required int MaxPromptTokens { get; init; }
    [JsonPropertyName("maxCompletionTokens")] public required int MaxCompletionTokens { get; init; }
    [JsonPropertyName("sourceTextCharacters")] public int SourceTextCharacters { get; init; }
    [JsonPropertyName("packetCharacters")] public int PacketCharacters { get; init; }
    [JsonPropertyName("packetOverheadCharacters")] public int PacketOverheadCharacters { get; init; }
    [JsonPropertyName("sourcePayloadRatio")] public double SourcePayloadRatio { get; init; }
    [JsonPropertyName("estimatedInputTokens")] public int EstimatedInputTokens { get; init; }
    [JsonPropertyName("reportedInputTokens")] public int? ReportedInputTokens { get; set; }
    [JsonPropertyName("reportedOutputTokens")] public int? ReportedOutputTokens { get; set; }
    [JsonPropertyName("reportedReasoningTokens")] public int? ReportedReasoningTokens { get; set; }
    [JsonPropertyName("segmentCount")] public int SegmentCount { get; init; } = 1;
    [JsonPropertyName("ownedOccurrences")] public int OwnedOccurrences { get; init; }
    [JsonPropertyName("visibleOccurrences")] public int VisibleOccurrences { get; init; }
    [JsonPropertyName("headingOutputCount")] public int HeadingOutputCount { get; set; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
    [JsonPropertyName("finishReason")] public string? FinishReason { get; set; }
    [JsonPropertyName("httpStatus")] public int? HttpStatus { get; set; }
    [JsonPropertyName("failureClass")] public string? FailureClass { get; set; }
    [JsonPropertyName("providerCallId")] public string? ProviderCallId { get; set; }
    [JsonPropertyName("responseContentPresent")] public bool? ResponseContentPresent { get; set; }
    [JsonPropertyName("structuredOutputParsed")] public bool? StructuredOutputParsed { get; set; }
    [JsonPropertyName("timeoutDetected")] public bool? TimeoutDetected { get; set; }
    [JsonPropertyName("streamStallDetected")] public bool? StreamStallDetected { get; set; }
}

/// <summary>OpenRouter adapter for the reasoning-ceiling route only: qwen/qwen3.5-9b, no
/// fallback, reasoning enabled at the highest supported effort with exclude=true (the model
/// reasons internally but chain-of-thought text is never requested/parsed/stored), and a
/// context/output budget resolved from real provider capability instead of a hard-coded 32K.</summary>
public sealed class OpenRouterCeilingReasoningModel : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly RemoteInferenceOptions _options;
    private readonly OpenRouterModelCapability _capability;
    private readonly int _safetyTokens;
    private readonly TimeSpan _attemptDeadline;
    private readonly TimeSpan _streamStallDeadline;
    private int _providerCalls;
    private readonly List<RequestPacketTelemetry> _telemetry = [];

    public OpenRouterCeilingReasoningModel(
        RemoteInferenceOptions options,
        OpenRouterModelCapability capability,
        HttpClient? http = null,
        int safetyTokens = 4_000,
        TimeSpan? attemptDeadline = null,
        TimeSpan? streamStallDeadline = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new InvalidOperationException("PROVIDER_AUTH_FAILURE");
        _safetyTokens = safetyTokens;
        _attemptDeadline = attemptDeadline ?? TimeSpan.FromSeconds(Math.Max(600, _options.RequestTimeoutSeconds));
        _streamStallDeadline = streamStallDeadline ?? TimeSpan.FromSeconds(120);
        _http = http ?? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = http is null;
    }

    /// <summary>This adapter always executes in <see cref="ReasoningExecutionMode.Ceiling"/> --
    /// it never sends reasoning=none and results from it may always be reported as ceiling
    /// metrics.</summary>
    public ReasoningExecutionMode ExecutionMode => ReasoningExecutionMode.Ceiling;

    public int ProviderCalls => Volatile.Read(ref _providerCalls);
    public IReadOnlyList<RequestPacketTelemetry> Telemetry => _telemetry;
    public OpenRouterModelCapability Capability => _capability;

    /// <summary>Resolved output budget for the semantic pass: bounded by real provider
    /// max-completion-tokens if reported, otherwise a generous default -- never a blind
    /// fixed 16384 regardless of reasoning-effort consumption.</summary>
    public int SemanticMaxCompletionTokens => _capability.MaxCompletionTokens is { } max ? Math.Min(max, 48_000) : 48_000;

    /// <summary>Hierarchy pass needs less output; still capability-bounded.</summary>
    public int HierarchyMaxCompletionTokens => _capability.MaxCompletionTokens is { } max ? Math.Min(max, 8_000) : 8_000;

    public int MaxPromptTokens(int maxCompletionTokens) =>
        ReasoningTokenBudget.MaxPromptTokens(_capability.ContextLength > 0 ? _capability.ContextLength : _options.ContextSize, maxCompletionTokens, _safetyTokens);

    public async Task<(CeilingSemanticResponse Response, RequestPacketTelemetry Telemetry)> CompleteSemanticAsync(
        string documentId,
        string route,
        string requestId,
        string packetJson,
        int sourceTextCharacters,
        int ownedOccurrences,
        int visibleOccurrences,
        CancellationToken ct = default)
    {
        var maxCompletion = SemanticMaxCompletionTokens;
        var telemetry = NewTelemetry(documentId, "SEMANTIC", requestId, packetJson, sourceTextCharacters, maxCompletion, ownedOccurrences, visibleOccurrences);
        var system = CeilingSemanticPrompt.System;
        var user = CeilingSemanticPrompt.BuildUser(packetJson, route);
        var (content, finishReason) = await SendAsync(system, user, maxCompletion, CeilingSemanticPrompt.Schema(), "ceiling_semantic_v3", telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Ceiling semantic pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        var response = CeilingSemanticResponseParser.Parse(content);
        telemetry.StructuredOutputParsed = true;
        telemetry.HeadingOutputCount = response.Headings.Count;
        _telemetry.Add(telemetry);
        return (response, telemetry);
    }

    /// <summary>Strategy S1's omission-review pass (Pass B). Same reasoning route/model/effort as
    /// the semantic pass -- reasoning is never disabled for this call.</summary>
    public async Task<(OmissionReviewResponse Response, RequestPacketTelemetry Telemetry)> CompleteOmissionReviewAsync(
        string documentId, string route, string requestId, string packetJson, string inventoryJson,
        int sourceTextCharacters, int ownedOccurrences, int visibleOccurrences, CancellationToken ct = default)
    {
        var maxCompletion = SemanticMaxCompletionTokens;
        var telemetry = NewTelemetry(documentId, "OMISSION_REVIEW", requestId, packetJson + inventoryJson, sourceTextCharacters, maxCompletion, ownedOccurrences, visibleOccurrences);
        var user = OmissionReviewPrompt.BuildUser(packetJson, inventoryJson, route);
        var (content, finishReason) = await SendAsync(OmissionReviewPrompt.System, user, maxCompletion, OmissionReviewPrompt.Schema(), "ceiling_omission_review_v1", telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Omission-review pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        var response = OmissionReviewResponseParser.Parse(content);
        telemetry.StructuredOutputParsed = true;
        telemetry.HeadingOutputCount = response.Items.Count;
        _telemetry.Add(telemetry);
        return (response, telemetry);
    }

    /// <summary>Strategy S2's independent second discovery extractor ("Extractor B"). Sees ONLY the
    /// source packet -- never Extractor A's (S0's) proposals.</summary>
    public async Task<(CeilingSemanticResponse Response, RequestPacketTelemetry Telemetry)> CompleteCoverageSemanticAsync(
        string documentId, string route, string requestId, string packetJson,
        int sourceTextCharacters, int ownedOccurrences, int visibleOccurrences, CancellationToken ct = default)
    {
        var maxCompletion = SemanticMaxCompletionTokens;
        var telemetry = NewTelemetry(documentId, "COVERAGE_SEMANTIC", requestId, packetJson, sourceTextCharacters, maxCompletion, ownedOccurrences, visibleOccurrences);
        var user = ExhaustiveCoverageSemanticPrompt.BuildUser(packetJson, route);
        var (content, finishReason) = await SendAsync(ExhaustiveCoverageSemanticPrompt.System, user, maxCompletion, ExhaustiveCoverageSemanticPrompt.Schema(), "ceiling_coverage_semantic_v1", telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Coverage semantic pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        var response = CeilingSemanticResponseParser.Parse(content);
        telemetry.StructuredOutputParsed = true;
        telemetry.HeadingOutputCount = response.Headings.Count;
        _telemetry.Add(telemetry);
        return (response, telemetry);
    }

    /// <summary>Strategy S3's advisory verifier/critic pass. Its KEEP/REJECT/CORRECT_SPAN decisions
    /// never themselves bypass the hard validator -- callers must still run every surviving
    /// candidate through <see cref="CeilingProposalBinder"/> and
    /// <see cref="ReasoningHardInvariantValidator"/> afterwards.</summary>
    public async Task<(VerifierResponse Response, RequestPacketTelemetry Telemetry)> CompleteVerifierAsync(
        string documentId, string route, string requestId, string occurrencesJson, string candidatesJson,
        int sourceTextCharacters, int ownedOccurrences, int visibleOccurrences, CancellationToken ct = default)
    {
        var maxCompletion = SemanticMaxCompletionTokens;
        var telemetry = NewTelemetry(documentId, "VERIFIER", requestId, occurrencesJson + candidatesJson, sourceTextCharacters, maxCompletion, ownedOccurrences, visibleOccurrences);
        var user = VerifierPrompt.BuildUser(occurrencesJson, candidatesJson, route);
        var (content, finishReason) = await SendAsync(VerifierPrompt.System, user, maxCompletion, VerifierPrompt.Schema(), "ceiling_verifier_v1", telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Verifier pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        var response = VerifierResponseParser.Parse(content);
        telemetry.StructuredOutputParsed = true;
        telemetry.HeadingOutputCount = response.Decisions.Count;
        _telemetry.Add(telemetry);
        return (response, telemetry);
    }

    public async Task<(CeilingHierarchyResponse Response, RequestPacketTelemetry Telemetry)> CompleteHierarchyAsync(
        string documentId,
        string route,
        string requestId,
        string packetJson,
        int inventoryCount,
        CancellationToken ct = default)
    {
        var maxCompletion = HierarchyMaxCompletionTokens;
        var telemetry = NewTelemetry(documentId, "HIERARCHY", requestId, packetJson, packetJson.Length, maxCompletion, inventoryCount, inventoryCount);
        var system = CeilingHierarchyPrompt.System;
        var user = CeilingHierarchyPrompt.BuildUser(packetJson);
        var (content, finishReason) = await SendAsync(system, user, maxCompletion, CeilingHierarchyPrompt.Schema(inventoryCount), "ceiling_hierarchy_v2", telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Ceiling hierarchy pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        var response = CeilingHierarchyResponseParser.Parse(content, inventoryCount);
        telemetry.StructuredOutputParsed = true;
        telemetry.HeadingOutputCount = response.Parents.Count;
        _telemetry.Add(telemetry);
        return (response, telemetry);
    }

    private RequestPacketTelemetry NewTelemetry(
        string documentId, string passType, string requestId, string packetJson,
        int sourceTextCharacters, int maxCompletionTokens, int ownedOccurrences, int visibleOccurrences)
    {
        var contextLength = _capability.ContextLength > 0 ? _capability.ContextLength : _options.ContextSize;
        var maxPrompt = MaxPromptTokens(maxCompletionTokens);
        var overhead = Math.Max(0, packetJson.Length - sourceTextCharacters);
        return new RequestPacketTelemetry
        {
            RequestIdHash = Sha256(requestId),
            DocumentId = documentId,
            PassType = passType,
            Model = _options.Model,
            ReasoningEffort = ReasoningEffortValue,
            ReasoningExcludedFromResponse = _capability.ReasoningEnabled,
            ContextLength = contextLength,
            MaxPromptTokens = maxPrompt,
            MaxCompletionTokens = maxCompletionTokens,
            SourceTextCharacters = sourceTextCharacters,
            PacketCharacters = packetJson.Length,
            PacketOverheadCharacters = overhead,
            SourcePayloadRatio = packetJson.Length == 0 ? 0 : (double)sourceTextCharacters / packetJson.Length,
            EstimatedInputTokens = ReasoningTokenBudget.EstimateTokens(packetJson.Length),
            OwnedOccurrences = ownedOccurrences,
            VisibleOccurrences = visibleOccurrences,
        };
    }

    private string ReasoningEffortValue => _capability.ReasoningEnabled ? _capability.SelectedReasoningEffort : "none";

    private async Task<(string Content, string? FinishReason)> SendAsync(
        string systemPrompt, string userPrompt, int maxCompletionTokens, object schema, string schemaName,
        RequestPacketTelemetry telemetry, CancellationToken ct)
    {
        Interlocked.Increment(ref _providerCalls);
        var reasoning = BuildReasoningParameter();
        object body = reasoning is null
            ? new
            {
                model = _options.Model,
                temperature = 0,
                max_tokens = maxCompletionTokens,
                messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } },
                response_format = new { type = "json_schema", json_schema = new { name = schemaName, strict = true, schema } },
                provider = new { zdr = true, data_collection = "deny", require_parameters = true, allow_fallbacks = false },
            }
            : new
            {
                model = _options.Model,
                temperature = 0,
                max_tokens = maxCompletionTokens,
                reasoning,
                messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } },
                response_format = new { type = "json_schema", json_schema = new { name = schemaName, strict = true, schema } },
                provider = new { zdr = true, data_collection = "deny", require_parameters = true, allow_fallbacks = false },
            };

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint) { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor Accuracy99 Ceiling");

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Ceiling-mode reasoning (highest supported effort, large context) can genuinely take
        // several minutes on OpenRouter; a short attempt timeout would misclassify slow-but-valid
        // reasoning as a transport failure. Use a generous floor and still respect a larger
        // explicit RequestTimeoutSeconds.
        timeout.CancelAfter(_attemptDeadline);
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            telemetry.HttpStatus = (int)response.StatusCode;
            var raw = await ReadResponseBodyAsync(response, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                telemetry.FailureClass = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? ReasoningCompletionFailureClass.ProviderAuthFailure : ReasoningCompletionFailureClass.ProviderUnavailable;
                throw new ReasoningCompletionException(telemetry.FailureClass, $"OpenRouter returned {(int)response.StatusCode}",
                    new ReasoningCompletionTelemetry { RequestId = telemetry.RequestIdHash, DocumentId = telemetry.DocumentId, FailureClass = telemetry.FailureClass });
            }
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            telemetry.ProviderCallId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                telemetry.ReportedInputTokens = ReadInt(usage, "prompt_tokens");
                telemetry.ReportedOutputTokens = ReadInt(usage, "completion_tokens");
                if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                    telemetry.ReportedReasoningTokens = ReadInt(details, "reasoning_tokens");
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new FormatException("ceiling-provider-response-choices-missing");
            var choice = choices[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null;
            telemetry.FinishReason = finishReason;
            // Section 13: reasoning must never starve the final JSON. When reasoning consumes the
            // whole completion budget, the provider can return finish_reason=length with an empty
            // or missing content field -- classify that as PROVIDER_OUTPUT_LIMIT (recoverable via
            // source-faithful split) BEFORE treating a missing content field as a hard schema
            // failure, never as a semantic omission.
            JsonElement contentEl = default;
            var hasContent = choice.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("content", out contentEl) && contentEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(contentEl.GetString());
            telemetry.ResponseContentPresent = hasContent;
            if (!hasContent && IsOutputLimit(finishReason))
                throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                    "Reasoning consumed the completion budget before a final response was produced.",
                    new ReasoningCompletionTelemetry { RequestId = telemetry.RequestIdHash, DocumentId = telemetry.DocumentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
            if (!hasContent)
                throw new FormatException("ceiling-provider-response-content-missing");
            if (stopwatch.Elapsed >= _attemptDeadline)
            {
                telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderTotalTimeout;
                telemetry.TimeoutDetected = true;
                throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderTotalTimeout,
                    "Ceiling request exceeded its attempt deadline before parsing completed.",
                    new ReasoningCompletionTelemetry { RequestId = telemetry.RequestIdHash, DocumentId = telemetry.DocumentId, FailureClass = ReasoningCompletionFailureClass.ProviderTotalTimeout });
            }
            return (contentEl.GetString() ?? "", finishReason);
        }
        catch (ReasoningCompletionException)
        {
            _telemetry.Add(telemetry);
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The attempt-level timeout fired, not the caller's cancellation -- this is a slow
            // (but not necessarily failed) reasoning call and must be retryable, never silently
            // reclassified as a semantic omission.
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderTotalTimeout;
            telemetry.TimeoutDetected = true;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderTotalTimeout,
                "Ceiling request exceeded its attempt timeout.",
                new ReasoningCompletionTelemetry { RequestId = telemetry.RequestIdHash, DocumentId = telemetry.DocumentId, FailureClass = ReasoningCompletionFailureClass.ProviderTotalTimeout });
        }
        catch (StreamStallException ex)
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout;
            telemetry.StreamStallDetected = true;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout,
                "Ceiling response stream stalled before a complete response was received.",
                new ReasoningCompletionTelemetry { RequestId = telemetry.RequestIdHash, DocumentId = telemetry.DocumentId, FailureClass = ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout }, ex);
        }
        catch (HttpRequestException ex)
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderUnavailable;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderUnavailable, ex.Message,
                new ReasoningCompletionTelemetry { RequestId = telemetry.RequestIdHash, DocumentId = telemetry.DocumentId, FailureClass = ReasoningCompletionFailureClass.ProviderUnavailable }, ex);
        }
        finally
        {
            telemetry.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }
    }

    private async Task<string> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var readTask = stream.ReadAsync(chunk.AsMemory(), ct).AsTask();
            var stallTask = Task.Delay(_streamStallDeadline, ct);
            var completed = await Task.WhenAny(readTask, stallTask).ConfigureAwait(false);
            if (completed != readTask)
                throw new StreamStallException();
            var count = await readTask.ConfigureAwait(false);
            if (count == 0) break;
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed class StreamStallException : Exception { }

    /// <summary>Section 3: enable internal reasoning without returning chain-of-thought.
    /// Uses effort+exclude when an explicit effort is supported; falls back to enabled+exclude
    /// when reasoning is supported but no effort list was reported; omits the parameter (no
    /// reasoning object at all) only when the provider does not support reasoning for this
    /// model.</summary>
    private object? BuildReasoningParameter()
    {
        if (!_capability.ReasoningEnabled) return null;
        if (_capability.EffortListReported && _capability.SelectedReasoningEffort != "enabled" && _capability.SelectedReasoningEffort != "none")
            return new { effort = _capability.SelectedReasoningEffort, exclude = true };
        return new { enabled = true, exclude = true };
    }

    private static bool IsOutputLimit(string? finishReason) => finishReason is not null &&
        (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase) ||
         finishReason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase) ||
         finishReason.Equals("max_output_tokens", StringComparison.OrdinalIgnoreCase));

    private static int? ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
