using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

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
    /// <summary>Source identities selected before any provider execution; candidate id is diagnostic only.</summary>
    [JsonPropertyName("selectedSourceIdentities")]
    public IReadOnlyList<PdfSelectedSourceIdentity> SelectedSourceIdentities { get; init; } = [];

    /// <summary>Raw analyst completions, populated only by explicit diagnostic routes.</summary>
    [JsonPropertyName("rawAnalystResponses")]
    public IReadOnlyList<string> RawAnalystResponses { get; init; } = [];

    [JsonPropertyName("modelInputContracts")]
    public IReadOnlyList<string> ModelInputContracts { get; init; } = [];

    /// <summary>Per-candidate source/model/validation trace for PDF-first audit routes.</summary>
    [JsonPropertyName("candidateStageTraces")]
    public IReadOnlyList<PdfCandidateStageTrace> CandidateStageTraces { get; init; } = [];

    [JsonPropertyName("validatedStructures")]
    public IReadOnlyList<PdfValidatedStructure> ValidatedStructures { get; init; } = [];

    [JsonPropertyName("visualEvidence")]
    public IReadOnlyList<RouteVisualEvidenceAudit> VisualEvidence { get; init; } = [];

    /// <summary>Immutable per-region facts sufficient to replay evaluation without a VLM call.</summary>
    [JsonPropertyName("visualRecoveries")]
    public IReadOnlyList<PdfVisualRecoveryTrace> VisualRecoveries { get; init; } = [];

    [JsonPropertyName("proposalResolutions")]
    public IReadOnlyList<PdfProposalResolutionAudit> ProposalResolutions { get; init; } = [];

    [JsonPropertyName("hierarchyProposals")]
    public IReadOnlyList<PdfHierarchyProposalAudit> HierarchyProposals { get; init; } = [];

    /// <summary>M8.1 source-only evidence inventory for already validated headings.</summary>
    [JsonPropertyName("hierarchyFacts")]
    public IReadOnlyList<PdfHierarchyFactAudit> HierarchyFacts { get; init; } = [];

    [JsonPropertyName("textLayerRecoveries")]
    public IReadOnlyList<PdfTextLayerRecoveryAudit> TextLayerRecoveries { get; init; } = [];

    [JsonPropertyName("rankedCandidates")]
    public IReadOnlyList<RankedCandidate> RankedCandidates { get; init; } = [];

    /// <summary>Independent semantic execution outcome. A timeout is partial work, not provider unavailability.</summary>
    [JsonPropertyName("semanticLane")]
    public RouteLaneExecutionAudit? SemanticLane { get; init; }

    /// <summary>Independent visual execution outcome.</summary>
    [JsonPropertyName("visualLane")]
    public RouteLaneExecutionAudit? VisualLane { get; init; }

    /// <summary>
    /// Independent span-resolution outcome. Reported separately from <see cref="SemanticLane"/> on
    /// purpose: a heading needs a resolved span to pass validation, so this lane can fail while the
    /// role lane completes - and folding it into the existing field would change what that field has
    /// always meant.
    /// </summary>
    [JsonPropertyName("spanLane")]
    public RouteLaneExecutionAudit? SpanLane { get; init; }
}

public sealed record PdfSelectedSourceIdentity(
    [property: JsonPropertyName("candidateIdDiagnostic")] string CandidateIdDiagnostic,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("sourceLineIds")] IReadOnlyList<string> SourceLineIds,
    [property: JsonPropertyName("sourceText")] string SourceText,
    [property: JsonPropertyName("sourceSpan")] TextOffsetSpan? SourceSpan = null);

public sealed record RouteLaneExecutionAudit(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("scheduled")] int Scheduled,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("timedOut")] int TimedOut,
    [property: JsonPropertyName("notStarted")] int NotStarted,
    [property: JsonPropertyName("failureClass")] string? FailureClass = null);

public sealed record RouteBlockAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("text")] string Text);

public sealed record RouteBlockDecisionAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record RouteBlockRejectionAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record RouteVisualEvidenceAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("contextLinesAbove")] int ContextLinesAbove = 0,
    [property: JsonPropertyName("contextLinesBelow")] int ContextLinesBelow = 0);

public sealed record PdfVisualRecoveryTrace(
    [property: JsonPropertyName("regionId")] string RegionId,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("observedText")] string ObservedText,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("mappedText")] string? MappedText = null,
    [property: JsonPropertyName("mappedStableId")] string? MappedStableId = null,
    [property: JsonPropertyName("mappedSpanStart")] int? MappedSpanStart = null,
    [property: JsonPropertyName("mappedSpanEnd")] int? MappedSpanEnd = null,
    [property: JsonPropertyName("validatorReason")] string? ValidatorReason = null,
    [property: JsonPropertyName("attempts")] IReadOnlyList<DocxHeaderExtractor.DocumentProcessing.Vision.PdfVisualAttemptOutcome>? Attempts = null);
