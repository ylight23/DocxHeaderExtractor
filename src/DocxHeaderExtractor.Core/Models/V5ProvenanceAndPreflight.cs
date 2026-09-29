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
    [property: JsonPropertyName("projectionNames")] IReadOnlyList<string> ProjectionNames);

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
    int TimeoutSeconds);

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
    public void Validate()
    {
        if (GoldRead || ProviderCalls != 0) throw new InvalidOperationException("provider-preflight-is-not-provider-free");
        if (PlannedProviderCalls < 0 || PackCount < 0) throw new InvalidOperationException("preflight-count-invalid");
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
