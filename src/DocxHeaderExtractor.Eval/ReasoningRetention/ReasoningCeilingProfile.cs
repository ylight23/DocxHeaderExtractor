using System.Net.Http.Headers;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// The single canonical provider-neutral request profile for reasoning-ceiling measurement.
/// It controls only what is VISIBLE to the model in the serialized packet -- the runtime
/// SourceFacts/candidate objects retained by the harness for diagnostics are unaffected.
/// </summary>
public enum ReasoningCeilingProfileKind
{
    ModelCapabilityCeiling,
    ReasoningPreservingShadow,
}

/// <summary>Explicit reasoning-execution mode so a "fast" (reasoning-disabled) diagnostic call can
/// never be silently reported as a ceiling measurement. <see cref="OpenRouterCeilingReasoningModel"/>
/// only ever executes in <see cref="Ceiling"/> mode; <see cref="Fast"/> exists as a named,
/// clearly-labeled alternative for latency/smoke diagnostics outside the ceiling campaign.</summary>
public enum ReasoningExecutionMode
{
    /// <summary>Strongest reasoning the provider reports as supported, with exclude=true so
    /// chain-of-thought text is never requested/parsed/stored. The only mode ceiling metrics may
    /// be computed from.</summary>
    Ceiling,

    /// <summary>reasoning effort "none" (or omitted). Never valid as a ceiling result.</summary>
    Fast,
}

public sealed record ReasoningCeilingRequestProfile
{
    public required ReasoningCeilingProfileKind Kind { get; init; }
    public required bool CandidateHintVisible { get; init; }
    public required bool CandidateScoreVisible { get; init; }
    public required bool CandidateReasonsVisible { get; init; }

    /// <summary>Ceiling route: no candidate anchoring may reach the model-visible packet.</summary>
    public static readonly ReasoningCeilingRequestProfile ModelCapabilityCeiling = new()
    {
        Kind = ReasoningCeilingProfileKind.ModelCapabilityCeiling,
        CandidateHintVisible = false,
        CandidateScoreVisible = false,
        CandidateReasonsVisible = false,
    };

    /// <summary>Shadow route: retains candidate hints for attribution experiments only.</summary>
    public static readonly ReasoningCeilingRequestProfile ReasoningPreservingShadow = new()
    {
        Kind = ReasoningCeilingProfileKind.ReasoningPreservingShadow,
        CandidateHintVisible = true,
        CandidateScoreVisible = true,
        CandidateReasonsVisible = true,
    };

    public string RouteName => Kind switch
    {
        ReasoningCeilingProfileKind.ModelCapabilityCeiling => nameof(ReasoningRoute.ModelCapabilityCeiling),
        ReasoningCeilingProfileKind.ReasoningPreservingShadow => nameof(ReasoningRoute.ReasoningPreservingShadow),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };
}

/// <summary>Resolved, provider-reported capability for one OpenRouter model id. Runtime metadata
/// is authoritative -- nothing here may be silently hard-coded lower than what the provider
/// reports, and reasoning is never disabled just because a supported-efforts list is absent.</summary>
public sealed record OpenRouterModelCapability
{
    public required string ModelId { get; init; }
    public required int ContextLength { get; init; }
    public bool ReasoningSupported { get; init; }
    public IReadOnlyList<string> SupportedReasoningEfforts { get; init; } = [];
    public string? DefaultReasoningEffort { get; init; }
    public int? MaxCompletionTokens { get; init; }
    public IReadOnlyList<string> SupportedParameters { get; init; } = [];
    public bool StructuredOutputSupported { get; init; }
    public required string SelectedReasoningEffort { get; init; }
    public required bool ReasoningEnabled { get; init; }
    public required bool EffortListReported { get; init; }
}

public sealed record CapabilityPreflightResult(
    bool Available,
    string Classification,
    string Reason,
    OpenRouterModelCapability? Capability);

public static class OpenRouterModelCapabilityResolver
{
    /// <summary>Highest-first preference order; only an effort the provider actually reports
    /// as supported is ever selected.</summary>
    private static readonly string[] EffortPreference = ["max", "xhigh", "high", "medium", "low", "minimal"];

    public static async Task<CapabilityPreflightResult> ResolveAsync(
        RemoteInferenceOptions options,
        HttpClient http,
        CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, options.ModelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new CapabilityPreflightResult(false, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", $"OPENROUTER_MODELS_HTTP_{(int)response.StatusCode}", null);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return new CapabilityPreflightResult(false, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", "OPENROUTER_MODELS_SCHEMA_INVALID", null);

            JsonElement? match = null;
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && string.Equals(id.GetString(), options.Model, StringComparison.Ordinal))
                {
                    match = item;
                    break;
                }
            }
            if (match is null)
                return new CapabilityPreflightResult(false, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", "MODEL_IDENTITY_MISMATCH", null);

            var capability = FromModelElement(options.Model, match.Value);
            return new CapabilityPreflightResult(true, "", "MODEL_AVAILABLE", capability);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CapabilityPreflightResult(false, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", "MODEL_PREFLIGHT_TIMEOUT", null);
        }
        catch (HttpRequestException ex)
        {
            return new CapabilityPreflightResult(false, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", ex.GetType().Name, null);
        }
        catch (JsonException)
        {
            return new CapabilityPreflightResult(false, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", "MODEL_METADATA_SCHEMA_INVALID", null);
        }
    }

    public static OpenRouterModelCapability FromModelElement(string modelId, JsonElement model)
    {
        var contextLength = model.TryGetProperty("context_length", out var cl) && cl.TryGetInt32(out var c) ? c : 0;
        var supportedParameters = model.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array
            ? sp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : [];
        var reasoningSupported = supportedParameters.Contains("reasoning", StringComparer.OrdinalIgnoreCase)
            || supportedParameters.Contains("include_reasoning", StringComparer.OrdinalIgnoreCase);
        var structuredOutputSupported = supportedParameters.Contains("response_format", StringComparer.OrdinalIgnoreCase)
            || supportedParameters.Contains("structured_outputs", StringComparer.OrdinalIgnoreCase);

        var supportedEfforts = new List<string>();
        int? maxCompletion = model.TryGetProperty("top_provider", out var top) && top.ValueKind == JsonValueKind.Object &&
            top.TryGetProperty("max_completion_tokens", out var mct) && mct.TryGetInt32(out var mctValue)
            ? mctValue : null;
        if (model.TryGetProperty("reasoning", out var reasoningMeta) && reasoningMeta.ValueKind == JsonValueKind.Object)
        {
            if (reasoningMeta.TryGetProperty("supported_efforts", out var efforts) && efforts.ValueKind == JsonValueKind.Array)
                supportedEfforts.AddRange(efforts.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? ""));
        }

        string? selected = null;
        var effortListReported = supportedEfforts.Count > 0;
        if (reasoningSupported)
        {
            if (effortListReported)
                selected = EffortPreference.FirstOrDefault(pref => supportedEfforts.Contains(pref, StringComparer.OrdinalIgnoreCase));
            // Reasoning is supported but no explicit effort list was reported: use enabled=true
            // rather than silently disabling reasoning by defaulting to "none".
        }

        return new OpenRouterModelCapability
        {
            ModelId = modelId,
            ContextLength = contextLength > 0 ? contextLength : 0,
            ReasoningSupported = reasoningSupported,
            SupportedReasoningEfforts = supportedEfforts,
            MaxCompletionTokens = maxCompletion,
            SupportedParameters = supportedParameters,
            StructuredOutputSupported = structuredOutputSupported,
            SelectedReasoningEffort = selected ?? (reasoningSupported ? "enabled" : "none"),
            ReasoningEnabled = reasoningSupported,
            EffortListReported = effortListReported,
        };
    }
}

/// <summary>Token-aware input budget derived from real provider capability, never a
/// hard-coded fraction of an assumed context window.</summary>
public static class ReasoningTokenBudget
{
    /// <summary>Conservative planning estimator: ~3.4 UTF-16 characters per token for mixed
    /// English/Vietnamese/structured-JSON text. Verified against reported prompt_tokens during
    /// live smoke execution; this is a planning ceiling, never a silent truncation authority.</summary>
    public const double CharactersPerToken = 3.4;

    public static int EstimateTokens(int characters) => (int)Math.Ceiling(characters / CharactersPerToken);

    public static int MaxPromptTokens(int modelContextTokens, int maxCompletionTokens, int safetyTokens) =>
        Math.Max(0, modelContextTokens - maxCompletionTokens - safetyTokens);
}
