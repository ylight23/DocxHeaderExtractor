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
    [JsonPropertyName("reasoningRequested")] public bool ReasoningRequested { get; init; }
    [JsonPropertyName("reasoningAccepted")] public bool? ReasoningAccepted { get; set; }
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
    [JsonPropertyName("cachedInputTokens")] public int? CachedInputTokens { get; set; }
    [JsonPropertyName("cacheMode")] public string CacheMode { get; set; } = "NOT_MEASURED";
    [JsonPropertyName("segmentCount")] public int SegmentCount { get; init; } = 1;
    [JsonPropertyName("ownedOccurrences")] public int OwnedOccurrences { get; init; }
    [JsonPropertyName("visibleOccurrences")] public int VisibleOccurrences { get; init; }
    [JsonPropertyName("headingOutputCount")] public int HeadingOutputCount { get; set; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
    [JsonPropertyName("finishReason")] public string? FinishReason { get; set; }
    [JsonPropertyName("httpStatus")] public int? HttpStatus { get; set; }
    [JsonPropertyName("failureClass")] public string? FailureClass { get; set; }
    [JsonPropertyName("providerCallId")] public string? ProviderCallId { get; set; }
    [JsonPropertyName("providerRoute")] public string? ProviderRoute { get; set; }
    [JsonPropertyName("canonicalRequestHash")] public string? CanonicalRequestHash { get; set; }
    [JsonPropertyName("requestBodyHash")] public string? RequestBodyHash { get; set; }
    [JsonPropertyName("responseContentPresent")] public bool? ResponseContentPresent { get; set; }
    [JsonPropertyName("structuredOutputParsed")] public bool? StructuredOutputParsed { get; set; }
    [JsonPropertyName("timeoutDetected")] public bool? TimeoutDetected { get; set; }
    [JsonPropertyName("streamStallDetected")] public bool? StreamStallDetected { get; set; }
    [JsonPropertyName("requestStartedUtc")] public DateTimeOffset? RequestStartedUtc { get; set; }
    [JsonPropertyName("headersReceivedUtc")] public DateTimeOffset? HeadersReceivedUtc { get; set; }
    [JsonPropertyName("firstResponseByteUtc")] public DateTimeOffset? FirstResponseByteUtc { get; set; }
    [JsonPropertyName("firstModelContentUtc")] public DateTimeOffset? FirstModelContentUtc { get; set; }
    [JsonPropertyName("responseCompletedUtc")] public DateTimeOffset? ResponseCompletedUtc { get; set; }
    [JsonPropertyName("ttfbMs")] public long? TtfbMs { get; set; }
    [JsonPropertyName("ttftMs")] public long? TtftMs { get; set; }
    [JsonPropertyName("generationElapsedMs")] public long? GenerationElapsedMs { get; set; }
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

    /// <summary>Normal callers use the reasoning ceiling. An explicit benchmark control may
    /// request the provider's reasoning.enabled=false mode without changing semantic logic.</summary>
    public ReasoningExecutionMode ExecutionMode => _options.OpenRouterReasoningEnabledOverride == false
        ? ReasoningExecutionMode.Fast : ReasoningExecutionMode.Ceiling;

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

    /// <summary>Runs one custom structured semantic request and returns the provider content
    /// without imposing the numeric-span parser.  The caller owns only its deterministic
    /// post-processing contract; transport, completion-limit handling, and telemetry remain
    /// identical to the ceiling route.</summary>
    public async Task<(string Content, RequestPacketTelemetry Telemetry)> CompleteRawStructuredSemanticAsync(
        string documentId, string route, string requestId, string packetJson, int sourceTextCharacters,
        int ownedOccurrences, int visibleOccurrences, string systemPrompt, string userPrompt,
        object schema, string schemaName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        ArgumentNullException.ThrowIfNull(schema);
        var maxCompletion = SemanticMaxCompletionTokens;
        var telemetry = NewTelemetry(documentId, "SEMANTIC", requestId, packetJson, sourceTextCharacters, maxCompletion, ownedOccurrences, visibleOccurrences);
        var (content, finishReason) = await SendAsync(systemPrompt, userPrompt, maxCompletion, schema, schemaName, telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Custom semantic pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        telemetry.ResponseContentPresent = !string.IsNullOrWhiteSpace(content);
        _telemetry.Add(telemetry);
        return (content, telemetry);
    }

    public async Task<(StructurePreservingSemanticResponse Response, RequestPacketTelemetry Telemetry)> CompleteStructurePreservingSemanticAsync(
        string documentId, string route, string requestId, string packetJson, int sourceTextCharacters,
        int ownedOccurrences, int visibleOccurrences, CancellationToken ct = default)
    {
        var maxCompletion = SemanticMaxCompletionTokens;
        var telemetry = NewTelemetry(documentId, "STRUCTURE_PRESERVING_SEMANTIC", requestId, packetJson,
            sourceTextCharacters, maxCompletion, ownedOccurrences, visibleOccurrences);
        var user = StructurePreservingSemanticPrompt.BuildUser(packetJson, route);
        var (content, finishReason) = await SendAsync(StructurePreservingSemanticPrompt.System, user,
            maxCompletion, StructurePreservingSemanticPrompt.Schema(), "ceiling_structure_preserving_v1", telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Structure-preserving semantic pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        var response = StructurePreservingSemanticResponseParser.Parse(content);
        telemetry.StructuredOutputParsed = true;
        telemetry.HeadingOutputCount = response.Headings.Count;
        _telemetry.Add(telemetry);
        return (response, telemetry);
    }

    public async Task<(StructurePreservingSemanticResponse Response, RequestPacketTelemetry Telemetry)> CompleteStructurePreservingVisualSemanticAsync(
        string documentId, string route, string requestId, string packetJson, IReadOnlyList<VisualPageEvidence> pages,
        int sourceTextCharacters, int ownedOccurrences, int visibleOccurrences, CancellationToken ct = default)
    {
        if (pages is null || pages.Count == 0) throw new ArgumentException("Visual page evidence is required.", nameof(pages));
        var maxCompletion = SemanticMaxCompletionTokens;
        var visualManifest = string.Join('|', pages.Select(page => $"p{page.PageIndex}:{page.ImageHash}"));
        var telemetry = NewTelemetry(documentId, "STRUCTURE_PRESERVING_VISUAL_SEMANTIC", requestId,
            packetJson + "\n" + visualManifest, sourceTextCharacters, maxCompletion, ownedOccurrences, visibleOccurrences);
        var userText = StructurePreservingSemanticPrompt.System + "\nroute=" + route +
            "\nUse the supplied page images as visual/layout evidence, while the supplied line text remains canonical for exact spans. " +
            "Return only the structure-preserving line-address JSON schema.\n" + packetJson;
        var content = new List<object> { new { type = "text", text = userText } };
        content.AddRange(pages.Select(page => (object)new
        {
            type = "image_url", image_url = new { url = "data:" + page.MimeType + ";base64," + Convert.ToBase64String(page.PngBytes) },
        }));
        var (rawContent, finishReason) = await SendAsync(StructurePreservingSemanticPrompt.System, content,
            maxCompletion, StructurePreservingSemanticPrompt.Schema(), "ceiling_structure_preserving_visual_v1", telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Structure-preserving visual semantic pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        var response = StructurePreservingSemanticResponseParser.Parse(rawContent);
        telemetry.StructuredOutputParsed = true;
        telemetry.HeadingOutputCount = response.Headings.Count;
        _telemetry.Add(telemetry);
        return (response, telemetry);
    }

    /// <summary>Contract-v2 semantic entry point. The transport, telemetry, completion-limit
    /// handling, and parser remain identical to the frozen v3 route; only the frozen
    /// model-facing contract and schema are supplied by the caller.</summary>
    public async Task<(CeilingSemanticResponse Response, RequestPacketTelemetry Telemetry)> CompleteSemanticAsync(
        string documentId,
        string route,
        string requestId,
        string packetJson,
        int sourceTextCharacters,
        int ownedOccurrences,
        int visibleOccurrences,
        string systemPrompt,
        string userPrompt,
        object schema,
        string schemaName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        ArgumentNullException.ThrowIfNull(schema);
        var maxCompletion = SemanticMaxCompletionTokens;
        var telemetry = NewTelemetry(documentId, "SEMANTIC", requestId, packetJson, sourceTextCharacters, maxCompletion, ownedOccurrences, visibleOccurrences);
        var (content, finishReason) = await SendAsync(systemPrompt, userPrompt, maxCompletion, schema, schemaName, telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Contract-v2 semantic pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        var response = CeilingSemanticResponseParser.Parse(content);
        telemetry.StructuredOutputParsed = true;
        telemetry.HeadingOutputCount = response.Headings.Count;
        _telemetry.Add(telemetry);
        return (response, telemetry);
    }

    /// <summary>Visual-evidence variant of the same single semantic pass. Images supplement the
    /// XML/source packet; they never carry source identity or final binding authority.</summary>
    public async Task<(CeilingSemanticResponse Response, RequestPacketTelemetry Telemetry)> CompleteVisualSemanticAsync(
        string documentId,
        string route,
        string requestId,
        string packetJson,
        IReadOnlyList<VisualPageEvidence> pages,
        int sourceTextCharacters,
        int ownedOccurrences,
        int visibleOccurrences,
        CancellationToken ct = default)
        => await CompleteVisualSemanticAsync(documentId, route, requestId, packetJson, pages,
            sourceTextCharacters, ownedOccurrences, visibleOccurrences,
            CeilingSemanticPrompt.System, CeilingSemanticPrompt.BuildUser(packetJson, route),
            CeilingSemanticPrompt.Schema(), "ceiling_visual_semantic_v1", ct).ConfigureAwait(false);

    /// <summary>Visual Contract-v2 entry point. The request transport and telemetry are
    /// identical to the established visual route; only the frozen semantic contract/schema
    /// are supplied by the caller.</summary>
    public async Task<(CeilingSemanticResponse Response, RequestPacketTelemetry Telemetry)> CompleteVisualSemanticAsync(
        string documentId,
        string route,
        string requestId,
        string packetJson,
        IReadOnlyList<VisualPageEvidence> pages,
        int sourceTextCharacters,
        int ownedOccurrences,
        int visibleOccurrences,
        string systemPrompt,
        string baseUserPrompt,
        object schema,
        string schemaName,
        CancellationToken ct = default)
    {
        if (pages is null || pages.Count == 0) throw new ArgumentException("Visual page evidence is required.", nameof(pages));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUserPrompt);
        ArgumentNullException.ThrowIfNull(schema);
        var maxCompletion = SemanticMaxCompletionTokens;
        var visualManifest = string.Join('|', pages.Select(page => $"p{page.PageIndex}:{page.ImageHash}"));
        var telemetry = NewTelemetry(documentId, "VISUAL_SEMANTIC", requestId, packetJson + "\n" + visualManifest,
            sourceTextCharacters, maxCompletion, ownedOccurrences, visibleOccurrences);
        var userText = baseUserPrompt + "\nVISUAL_EVIDENCE_PAGES=" +
            string.Join(',', pages.Select(page => page.PageIndex)) +
            "\nUse the supplied page images as visual evidence. XML/source text remains canonical for exact text, identity, and spans. " +
            "Do not emit text that cannot be bound to the supplied source occurrences.";
        var content = new List<object> { new { type = "text", text = userText } };
        content.AddRange(pages.Select(page => (object)new
        {
            type = "image_url",
            image_url = new { url = "data:" + page.MimeType + ";base64," + Convert.ToBase64String(page.PngBytes) },
        }));
        var (rawContent, finishReason) = await SendAsync(systemPrompt, content, maxCompletion,
            schema, schemaName, telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Visual semantic pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        CeilingSemanticResponse response;
        try
        {
            response = CeilingSemanticResponseParser.Parse(rawContent);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            telemetry.StructuredOutputParsed = false;
            telemetry.FailureClass = ReasoningCompletionFailureClass.TransportTruncation;
            _telemetry.Add(telemetry);
            throw;
        }
        telemetry.StructuredOutputParsed = true;
        telemetry.HeadingOutputCount = response.Headings.Count;
        _telemetry.Add(telemetry);
        return (response, telemetry);
    }

    /// <summary>Raw visual recovery transport. Unlike semantic visual inference this endpoint
    /// deliberately does not parse a heading contract: the caller supplies the recovery schema,
    /// while this class still owns the real image request, output-limit handling, and telemetry.</summary>
    public async Task<(string Content, RequestPacketTelemetry Telemetry)> CompleteRawVisualStructuredAsync(
        string documentId, string route, string requestId, string packetJson,
        IReadOnlyList<VisualPageEvidence> pages, int sourceTextCharacters,
        int ownedOccurrences, int visibleOccurrences, string systemPrompt,
        string userPrompt, object schema, string schemaName, CancellationToken ct = default)
    {
        if (pages is null || pages.Count == 0) throw new ArgumentException("Visual page evidence is required.", nameof(pages));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        ArgumentNullException.ThrowIfNull(schema);
        var maxCompletion = SemanticMaxCompletionTokens;
        var visualManifest = string.Join('|', pages.Select(page => $"p{page.PageIndex}:{page.ImageHash}"));
        var telemetry = NewTelemetry(documentId, "VISUAL_RECOVERY", requestId,
            packetJson + "\n" + visualManifest, sourceTextCharacters, maxCompletion,
            ownedOccurrences, visibleOccurrences);
        var content = new List<object> { new { type = "text", text = userPrompt } };
        content.AddRange(pages.Select(page => (object)new
        {
            type = "image_url",
            image_url = new { url = "data:" + page.MimeType + ";base64," + Convert.ToBase64String(page.PngBytes) },
        }));
        var (rawContent, finishReason) = await SendAsync(systemPrompt, content, maxCompletion,
            schema, schemaName, telemetry, ct).ConfigureAwait(false);
        if (IsOutputLimit(finishReason))
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderOutputLimit,
                "Visual recovery pass hit the provider output limit before a complete response.",
                new ReasoningCompletionTelemetry { RequestId = requestId, DocumentId = documentId, FailureClass = ReasoningCompletionFailureClass.ProviderOutputLimit });
        }
        telemetry.ResponseContentPresent = !string.IsNullOrWhiteSpace(rawContent);
        _telemetry.Add(telemetry);
        return (rawContent, telemetry);
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
            ReasoningExcludedFromResponse = ReasoningRequested,
            ReasoningRequested = ReasoningRequested,
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

    private bool ReasoningRequested => _options.OpenRouterReasoningEnabledOverride ?? _capability.ReasoningEnabled;
    private string ReasoningEffortValue => !ReasoningRequested
        ? "disabled" : _capability.SelectedReasoningEffort;

    private async Task<(string Content, string? FinishReason)> SendAsync(
        string systemPrompt, object userPrompt, int maxCompletionTokens, object schema, string schemaName,
        RequestPacketTelemetry telemetry, CancellationToken ct)
    {
        Interlocked.Increment(ref _providerCalls);
        var reasoning = BuildReasoningParameter();
        object body = BuildRequestBody(_options.Model, systemPrompt, userPrompt, maxCompletionTokens, schema, schemaName, reasoning,
            _options.OpenRouterProviderRoute, _options.OpenRouterAllowNonZdrPublicBenchmark);
        var canonicalBody = BuildRequestBody(_options.Model, systemPrompt, userPrompt, maxCompletionTokens, schema, schemaName, reasoning,
            null, _options.OpenRouterAllowNonZdrPublicBenchmark);
        telemetry.ProviderRoute = _options.OpenRouterProviderRoute ?? "AUTO";
        telemetry.CanonicalRequestHash = Sha256Bytes(SerializeRequestBodyForAudit(canonicalBody));
        telemetry.RequestBodyHash = Sha256Bytes(SerializeRequestBodyForAudit(body));

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint) { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor Accuracy99 Ceiling");

        var stopwatch = Stopwatch.StartNew();
        telemetry.RequestStartedUtc = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Ceiling-mode reasoning (highest supported effort, large context) can genuinely take
        // several minutes on OpenRouter; a short attempt timeout would misclassify slow-but-valid
        // reasoning as a transport failure. Use a generous floor and still respect a larger
        // explicit RequestTimeoutSeconds.
        timeout.CancelAfter(_attemptDeadline);
        try
        {
            var sendTask = _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var absoluteHeadersTask = Task.Delay(_attemptDeadline);
            if (await Task.WhenAny(sendTask, absoluteHeadersTask).ConfigureAwait(false) != sendTask)
            {
                timeout.Cancel();
                throw new AbsoluteDeadlineException();
            }
            using var response = await sendTask.ConfigureAwait(false);
            telemetry.HttpStatus = (int)response.StatusCode;
            telemetry.HeadersReceivedUtc = DateTimeOffset.UtcNow;
            telemetry.TtfbMs = stopwatch.ElapsedMilliseconds;
            var raw = await ReadResponseBodyAsync(response, timeout.Token, () =>
            {
                telemetry.FirstResponseByteUtc ??= DateTimeOffset.UtcNow;
                telemetry.TtfbMs ??= stopwatch.ElapsedMilliseconds;
            }, _attemptDeadline - stopwatch.Elapsed).ConfigureAwait(false);
            telemetry.ResponseCompletedUtc = DateTimeOffset.UtcNow;
            if (!response.IsSuccessStatusCode)
            {
                telemetry.FailureClass = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? ReasoningCompletionFailureClass.ProviderAuthFailure : ReasoningCompletionFailureClass.ProviderUnavailable;
                var errorBody = raw.Length <= 600 ? raw : raw[..600];
                throw new ReasoningCompletionException(telemetry.FailureClass, $"OpenRouter returned {(int)response.StatusCode}: {errorBody}",
                    new ReasoningCompletionTelemetry { RequestId = telemetry.RequestIdHash, DocumentId = telemetry.DocumentId, FailureClass = telemetry.FailureClass });
            }
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            telemetry.ReasoningAccepted = telemetry.ReasoningRequested;
            telemetry.ProviderCallId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
            telemetry.ProviderRoute = ReadString(root, "provider") ?? ReadString(root, "provider_name") ?? telemetry.ProviderRoute;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                telemetry.ReportedInputTokens = ReadInt(usage, "prompt_tokens");
                telemetry.ReportedOutputTokens = ReadInt(usage, "completion_tokens");
                if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                    telemetry.ReportedReasoningTokens = ReadInt(details, "reasoning_tokens");
                if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails) && promptDetails.ValueKind == JsonValueKind.Object)
                {
                    telemetry.CachedInputTokens = ReadInt(promptDetails, "cached_tokens");
                    telemetry.CacheMode = telemetry.CachedInputTokens is > 0 ? "WARM_OR_PARTIAL" : "COLD_OR_UNCACHED";
                }
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
        catch (AbsoluteDeadlineException)
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderTotalTimeout;
            telemetry.TimeoutDetected = true;
            _telemetry.Add(telemetry);
            throw new ReasoningCompletionException(ReasoningCompletionFailureClass.ProviderTotalTimeout,
                "Ceiling response exceeded its absolute attempt deadline.",
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
        catch (FormatException)
        {
            telemetry.StructuredOutputParsed = false;
            telemetry.FailureClass ??= ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid;
            _telemetry.Add(telemetry);
            throw;
        }
        catch (JsonException)
        {
            telemetry.StructuredOutputParsed = false;
            telemetry.FailureClass ??= ReasoningCompletionFailureClass.TransportTruncation;
            _telemetry.Add(telemetry);
            throw;
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
        => await ReadResponseBodyAsync(response, ct, null, _attemptDeadline).ConfigureAwait(false);

    private async Task<string> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken ct, Action? onFirstByte, TimeSpan absoluteRemaining)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        using var absoluteTimer = new CancellationTokenSource();
        absoluteTimer.CancelAfter(absoluteRemaining <= TimeSpan.Zero ? TimeSpan.Zero : absoluteRemaining);
        using var bodyToken = CancellationTokenSource.CreateLinkedTokenSource(ct, absoluteTimer.Token);
        while (true)
        {
            var readTask = stream.ReadAsync(chunk.AsMemory(), bodyToken.Token).AsTask();
            var stallTask = Task.Delay(_streamStallDeadline, bodyToken.Token);
            var absoluteTask = Task.Delay(Timeout.InfiniteTimeSpan, absoluteTimer.Token);
            var completed = await Task.WhenAny(readTask, stallTask, absoluteTask).ConfigureAwait(false);
            if (completed == absoluteTask)
                throw new AbsoluteDeadlineException();
            if (completed != readTask)
                throw new StreamStallException();
            var count = await readTask.ConfigureAwait(false);
            if (count == 0) break;
            onFirstByte?.Invoke();
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed class StreamStallException : Exception { }
    private sealed class AbsoluteDeadlineException : Exception { }

    /// <summary>Section 3: enable internal reasoning without returning chain-of-thought.
    /// Uses effort+exclude when an explicit effort is supported; falls back to enabled+exclude
    /// when reasoning is supported but no effort list was reported; omits the parameter (no
    /// reasoning object at all) only when the provider does not support reasoning for this
    /// model.</summary>
    private object? BuildReasoningParameter()
    {
        if (_options.OpenRouterReasoningEnabledOverride == false) return new { enabled = false };
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

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static object BuildRequestBodyForAudit(string model, string systemPrompt, object userPrompt, int maxCompletionTokens,
        object schema, string schemaName, object? reasoning, string? providerRoute, bool allowNonZdrPublicBenchmark) =>
        BuildRequestBody(model, systemPrompt, userPrompt, maxCompletionTokens, schema, schemaName, reasoning, providerRoute, allowNonZdrPublicBenchmark);

    internal static byte[] SerializeRequestBodyForAudit(object body) => JsonSerializer.SerializeToUtf8Bytes(body);

    private static object BuildRequestBody(string model, string systemPrompt, object userPrompt, int maxCompletionTokens,
        object schema, string schemaName, object? reasoning, string? providerRoute, bool allowNonZdrPublicBenchmark)
    {
        if (allowNonZdrPublicBenchmark && providerRoute is null)
        {
            return reasoning is null
                ? new { model, temperature = 0, max_tokens = maxCompletionTokens,
                    messages = new object[] { new { role = "system", content = (object)systemPrompt }, new { role = "user", content = userPrompt } },
                    response_format = new { type = "json_schema", json_schema = new { name = schemaName, strict = true, schema } } }
                : new { model, temperature = 0, max_tokens = maxCompletionTokens, reasoning,
                    messages = new object[] { new { role = "system", content = (object)systemPrompt }, new { role = "user", content = userPrompt } },
                    response_format = new { type = "json_schema", json_schema = new { name = schemaName, strict = true, schema } } };
        }

        object provider = providerRoute is null
            ? new { zdr = true, data_collection = "deny", require_parameters = true, allow_fallbacks = false }
            : new { order = new[] { providerRoute }, zdr = true, data_collection = "deny", require_parameters = true, allow_fallbacks = false };
        return reasoning is null
            ? new { model, temperature = 0, max_tokens = maxCompletionTokens,
                messages = new object[] { new { role = "system", content = (object)systemPrompt }, new { role = "user", content = userPrompt } },
                response_format = new { type = "json_schema", json_schema = new { name = schemaName, strict = true, schema } }, provider }
            : new { model, temperature = 0, max_tokens = maxCompletionTokens, reasoning,
                messages = new object[] { new { role = "system", content = (object)systemPrompt }, new { role = "user", content = userPrompt } },
                response_format = new { type = "json_schema", json_schema = new { name = schemaName, strict = true, schema } }, provider };
    }

    private static string Sha256(string value) => Sha256Bytes(Encoding.UTF8.GetBytes(value));
    private static string Sha256Bytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>Persistable-free in-memory page evidence. The image bytes are sent only for the
/// request; artifacts persist its hash, never the model's private reasoning.</summary>
public sealed record VisualPageEvidence(int PageIndex, string ImageHash, byte[] PngBytes, string MimeType = "image/png");
