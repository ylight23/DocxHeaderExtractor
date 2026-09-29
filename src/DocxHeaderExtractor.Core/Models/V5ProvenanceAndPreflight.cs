using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.V5;

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
