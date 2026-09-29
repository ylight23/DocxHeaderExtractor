using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// V5's own completion-token budget for a source-backed claim response - independent of the legacy
/// boundary-cut formula (<c>96 + expectedItemCount * 128</c>), which the v2.1 canary at commit
/// 72bb954 showed truncates a claim-shaped reply mid-JSON: SRC-095 PACK_001's 90 owned items produced
/// an 11,616-token budget under that formula, and the response was cut off before valid JSON closed.
/// <para>
/// A claim is not one row per owned item: it can carry two multi-part endpoints (subject and object),
/// each with left/right exact-context strings, so the per-item allowance here is deliberately
/// generous rather than tight, and a byte-based floor guards a pack whose owned count is small but
/// whose surrounding text is large.
/// </para>
/// </summary>
public static class V5SemanticCompletionBudget
{
    public const int BaseTokens = 512;
    public const int PerOwnedItemTokens = 256;
    public const int MinCompletionTokens = 1024;

    public static int Compute(int ownedCount, int visibleCount, int requestBytes, int providerMaxTokens)
    {
        if (ownedCount < 0) throw new ArgumentOutOfRangeException(nameof(ownedCount));
        if (visibleCount < ownedCount) throw new ArgumentOutOfRangeException(nameof(visibleCount));
        if (requestBytes < 0) throw new ArgumentOutOfRangeException(nameof(requestBytes));
        if (providerMaxTokens < MinCompletionTokens) throw new ArgumentOutOfRangeException(nameof(providerMaxTokens));

        var byOwnedItems = BaseTokens + ownedCount * PerOwnedItemTokens;
        var byRequestBytes = requestBytes / 6;
        var budget = Math.Max(byOwnedItems, byRequestBytes);
        return Math.Clamp(Math.Max(budget, MinCompletionTokens), MinCompletionTokens, providerMaxTokens);
    }
}

public sealed record ClaimProvenance(
    [property: JsonPropertyName("claimId")] string ClaimId,
    [property: JsonPropertyName("taskContractHash")] string TaskContractHash,
    [property: JsonPropertyName("sourceUniverseHash")] string SourceUniverseHash,
    [property: JsonPropertyName("evidenceIds")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("modelRequestHash")] string? ModelRequestHash,
    [property: JsonPropertyName("modelResponseHash")] string? ModelResponseHash,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("binderVersion")] string BinderVersion,
    [property: JsonPropertyName("validatorVersion")] string ValidatorVersion,
    [property: JsonPropertyName("retrievalEvidenceIds")] IReadOnlyList<string> RetrievalEvidenceIds,
    [property: JsonPropertyName("visualEvidenceIds")] IReadOnlyList<string> VisualEvidenceIds,
    [property: JsonPropertyName("finalState")] ClaimResolutionState FinalState,
    [property: JsonPropertyName("projectionNames")] IReadOnlyList<string> ProjectionNames)
{
    [JsonPropertyName("layoutEvidenceIds")]
    public IReadOnlyList<string> LayoutEvidenceIds { get; init; } = [];
}

public sealed record V5ReplayBundle(
    [property: JsonPropertyName("taskContractHash")] string TaskContractHash,
    [property: JsonPropertyName("sourceUniverseHash")] string SourceUniverseHash,
    [property: JsonPropertyName("requestHashes")] IReadOnlyList<string> RequestHashes,
    [property: JsonPropertyName("responseHashes")] IReadOnlyList<string> ResponseHashes,
    [property: JsonPropertyName("claims")] IReadOnlyList<ClaimProvenance> Claims)
{
    public string Hash() => Hashing.Sha256(JsonSerializer.Serialize(this, CanonicalJson.Options));
}

public sealed record V5ProviderEnvelope(
    string Model,
    string Provider,
    string Reasoning,
    bool Streaming,
    string ResponseFormat,
    int TimeoutSeconds)
{
    [JsonPropertyName("usageInclude")]
    public bool UsageInclude { get; init; } = true;

    [JsonPropertyName("openRouterResponseCacheDisabled")]
    public bool OpenRouterResponseCacheDisabled { get; init; } = true;

    [JsonPropertyName("explicitCacheControl")]
    public string? ExplicitCacheControl { get; init; }
}

/// <summary>
/// Provider-free execution contract. Building this record never opens a network client or Gold.
/// </summary>
public sealed record V5ProviderPreflight(
    string ProductionSourceSha,
    string SourceUniverseSha,
    string TaskContractHash,
    string ClaimSchemaHash,
    string PromptHash,
    string PackingPolicy,
    int PackCount,
    IReadOnlyList<int> RequestBytes,
    IReadOnlyList<string> RequestHashes,
    V5ProviderEnvelope ProviderEnvelope,
    int PlannedProviderCalls,
    bool GoldRead,
    int ProviderCalls)
{
    [JsonPropertyName("maxBoundedProviderCalls")]
    public int MaxBoundedProviderCalls { get; init; }

    public void Validate()
    {
        if (GoldRead || ProviderCalls != 0) throw new InvalidOperationException("provider-preflight-is-not-provider-free");
        if (PlannedProviderCalls < 0 || PackCount < 0) throw new InvalidOperationException("preflight-count-invalid");
        if (MaxBoundedProviderCalls < 0 || (MaxBoundedProviderCalls > 0 && MaxBoundedProviderCalls < PlannedProviderCalls))
            throw new InvalidOperationException("preflight-bounded-call-count-invalid");
        if (RequestBytes.Count != RequestHashes.Count || RequestBytes.Count != PackCount)
            throw new InvalidOperationException("preflight-request-count-mismatch");
        if (ProviderEnvelope.TimeoutSeconds <= 0) throw new InvalidOperationException("provider-timeout-invalid");
    }

    public string Hash()
    {
        Validate();
        return Hashing.Sha256(JsonSerializer.Serialize(this, CanonicalJson.Options));
    }
}

public static class V5ProviderPreflightBuilder
{
    /// <summary>Builds the v2.1 provider-free preflight while leaving all v1 hashes/artifacts intact.</summary>
    public static V5ProviderPreflight BuildV2_1(
        string productionSourceSha,
        UniversalEvidenceGraph sourceUniverse,
        DocumentTaskContract contract,
        IReadOnlyList<V5ComposedSemanticRequest> requests,
        string packingPolicy,
        V5ProviderEnvelope providerEnvelope)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var preflight = Build(
            productionSourceSha,
            sourceUniverse,
            contract,
            requests,
            packingPolicy,
            providerEnvelope);
        return preflight with { ClaimSchemaHash = SemanticClaimContractV2_1.SchemaHash() };
    }

    public static V5ProviderPreflight Build(
        string productionSourceSha,
        UniversalEvidenceGraph sourceUniverse,
        DocumentTaskContract contract,
        IReadOnlyList<V5ComposedSemanticRequest> requests,
        string packingPolicy,
        V5ProviderEnvelope providerEnvelope) =>
        Build(productionSourceSha, sourceUniverse, contract,
            requests.Count == 0 ? "empty" : requests[0].Prompt,
            packingPolicy, requests.Select(item => item.Prompt).ToArray(), providerEnvelope);

    public static V5ProviderPreflight Build(
        string productionSourceSha,
        UniversalEvidenceGraph sourceUniverse,
        DocumentTaskContract contract,
        string prompt,
        string packingPolicy,
        IReadOnlyList<string> requestPayloads,
        V5ProviderEnvelope providerEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productionSourceSha);
        ArgumentNullException.ThrowIfNull(sourceUniverse);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(packingPolicy);
        ArgumentNullException.ThrowIfNull(requestPayloads);
        ArgumentNullException.ThrowIfNull(providerEnvelope);
        contract.Validate();
        var promptHash = Hashing.Sha256(prompt.ReplaceLineEndings("\n"));
        var requestHashes = requestPayloads.Select(payload => Hashing.Sha256(payload)).ToArray();
        var requestBytes = requestPayloads.Select(payload => System.Text.Encoding.UTF8.GetByteCount(payload)).ToArray();
        var preflight = new V5ProviderPreflight(
            productionSourceSha,
            sourceUniverse.Hash(),
            contract.Hash(),
            SemanticClaimContract.SchemaHash(),
            promptHash,
            packingPolicy,
            requestPayloads.Count,
            requestBytes,
            requestHashes,
            providerEnvelope,
            requestPayloads.Count,
            GoldRead: false,
            ProviderCalls: 0);
        preflight = preflight with
        {
            MaxBoundedProviderCalls = requestPayloads.Count + contract.ExecutionBudget.MaxSemanticModelCalls +
                contract.ExecutionBudget.MaxRetrievalRounds + contract.ExecutionBudget.MaxLayoutCalls +
                contract.ExecutionBudget.MaxVisualCalls,
        };
        preflight.Validate();
        return preflight;
    }
}

public sealed record GenericEvaluationMetrics(
    int GroundedClaims,
    int InvalidClaims,
    int ResolvedClaims,
    int OpenClaims,
    int ConflictedClaims,
    int ExhaustedClaims,
    int RetrievalRounds,
    int VisualCalls,
    int ProviderCalls,
    long TokenCost,
    long LatencyMilliseconds,
    double? ProjectionAccuracy = null);
