namespace DocxHeaderExtractor.Core.Models;

/// <summary>Model-visible source authority at the text-inference boundary.</summary>
public sealed record CanonicalSemanticTextInferenceInput(
    IReadOnlyList<CanonicalSemanticSourceEvidence> SourceEvidence,
    IReadOnlyDictionary<string, string>? LayoutBlockBySourceId = null);

/// <summary>Compact parser-owned evidence attached to one canonical source occurrence.</summary>
public sealed record CanonicalSemanticSourceEvidence(
    string SourceAlias,
    string SourceId,
    int SourceOrdinal,
    string ExactSourceText,
    string StructuralScope,
    IReadOnlyList<string> ContainerFacts,
    object StyleFacts,
    object NumberingFacts,
    IReadOnlyList<object> RunFormattingFacts,
    IReadOnlyList<string> ObservedEvidence,
    IReadOnlyList<string> LocalBefore,
    IReadOnlyList<string> LocalAfter)
{
    /// <summary>Observable physical placement facts; never a semantic interpretation.</summary>
    public object? LocationFacts { get; init; }
}

public sealed record CanonicalSemanticInferenceTelemetry(
    string? ActualProvider = null,
    string? FinishReason = null,
    int? InputTokens = null,
    int? ReasoningTokens = null,
    int? OutputTokens = null);

public sealed record CanonicalSemanticTextInferenceResult(
    IReadOnlyList<CanonicalSemanticProposal> Proposals,
    CanonicalSemanticInferenceTelemetry Telemetry)
{
    public IReadOnlyList<SemanticContractIssue> ContractIssues { get; init; } = [];
}

/// <summary>Shared text-model boundary used by production and historical qualification routes.</summary>
public interface ICanonicalSemanticTextModel
{
    Task<CanonicalSemanticTextInferenceResult> InferAsync(
        CanonicalSemanticTextInferenceInput input,
        SemanticContextPacket packedContext,
        string requestId,
        CancellationToken cancellationToken = default);
}
