using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Auditable losses for a bounded route, especially PDF candidate/LLM/grounding pipelines.</summary>
public sealed record RouteExecutionAudit(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("candidatesAvailable")] int CandidatesAvailable,
    [property: JsonPropertyName("candidatesSelected")] int CandidatesSelected,
    [property: JsonPropertyName("candidatePagesAvailable")] int CandidatePagesAvailable,
    [property: JsonPropertyName("candidatePagesSelected")] int CandidatePagesSelected,
    [property: JsonPropertyName("candidateBlocks")] IReadOnlyList<RouteBlockAudit> CandidateBlocks,
    [property: JsonPropertyName("selectedCandidateBlocks")] IReadOnlyList<RouteBlockAudit> SelectedCandidateBlocks,
    [property: JsonPropertyName("budgetExcluded")] IReadOnlyList<RouteBlockAudit> BudgetExcluded,
    [property: JsonPropertyName("blockDecisions")] IReadOnlyList<RouteBlockDecisionAudit> BlockDecisions,
    [property: JsonPropertyName("groundedBlockIds")] IReadOnlyList<string> GroundedBlockIds,
    [property: JsonPropertyName("groundingRejections")] IReadOnlyList<RouteBlockRejectionAudit> GroundingRejections,
    [property: JsonPropertyName("alignedBlockIds")] IReadOnlyList<string> AlignedBlockIds)
{
    /// <summary>Raw analyst completions, populated only by explicit diagnostic routes.</summary>
    [JsonPropertyName("rawAnalystResponses")]
    public IReadOnlyList<string> RawAnalystResponses { get; init; } = [];

    /// <summary>Visual confirmations for the explicitly requested PDF audit lane.</summary>
    [JsonPropertyName("visualBlockDecisions")]
    public IReadOnlyList<RouteVisualBlockDecisionAudit> VisualBlockDecisions { get; init; } = [];

    /// <summary>Text-only semantic triage before visual adjudication.</summary>
    [JsonPropertyName("semanticBlockDecisions")]
    public IReadOnlyList<RouteBlockDecisionAudit> SemanticBlockDecisions { get; init; } = [];
}

public sealed record RouteVisualBlockDecisionAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("visualEvidenceTags")] IReadOnlyList<string>? VisualEvidenceTags = null,
    [property: JsonPropertyName("sourceGrounded")] bool? SourceGrounded = null,
    [property: JsonPropertyName("spanValid")] bool? SpanValid = null,
    [property: JsonPropertyName("evidenceValid")] bool? EvidenceValid = null);

public sealed record RouteBlockAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("text")] string Text);

public sealed record RouteBlockDecisionAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence);

public sealed record RouteBlockRejectionAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason);
