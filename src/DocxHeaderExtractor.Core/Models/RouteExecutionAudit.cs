using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Pipeline;

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

    /// <summary>Observability-only first-loss detail; never consulted by extraction decisions.</summary>
    [JsonPropertyName("lossInstrumentation")]
    public PdfLossInstrumentation? LossInstrumentation { get; init; }

    /// <summary>Observability-only request facts for each pointer-span provider call.</summary>
    [JsonPropertyName("spanRequestInstrumentation")]
    public IReadOnlyList<PdfSpanRequestInstrumentation> SpanRequestInstrumentation { get; init; } = [];
}

public sealed record PdfSpanRequestInstrumentation(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("batchIndex")] int BatchIndex,
    [property: JsonPropertyName("sourceIds")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("sourceOrdinals")] IReadOnlyList<int?> SourceOrdinals,
    [property: JsonPropertyName("semanticRoles")] IReadOnlyList<string> SemanticRoles,
    [property: JsonPropertyName("sourceCount")] int SourceCount,
    [property: JsonPropertyName("promptChars")] int PromptChars,
    [property: JsonPropertyName("promptUtf8Bytes")] int PromptUtf8Bytes,
    [property: JsonPropertyName("allowedSpanCountTotal")] int AllowedSpanCountTotal,
    [property: JsonPropertyName("allowedSpanCountPerSource")] IReadOnlyDictionary<string, int> AllowedSpanCountPerSource,
    [property: JsonPropertyName("sourceSliceCharsTotal")] int SourceSliceCharsTotal,
    [property: JsonPropertyName("startedUtc")] DateTimeOffset StartedUtc,
    [property: JsonPropertyName("completedUtc")] DateTimeOffset CompletedUtc,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("configuredRequestTimeoutMs")] long? ConfiguredRequestTimeoutMs,
    [property: JsonPropertyName("remainingBudgetMsAtStart")] long? RemainingBudgetMsAtStart,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("exceptionType")] string? ExceptionType,
    [property: JsonPropertyName("sanitizedExceptionMessage")] string? SanitizedExceptionMessage,
    [property: JsonPropertyName("httpStatus")] int? HttpStatus,
    [property: JsonPropertyName("cancellationRequestedBefore")] bool CancellationRequestedBefore,
    [property: JsonPropertyName("cancellationRequestedAfter")] bool CancellationRequestedAfter,
    [property: JsonPropertyName("responseReceived")] bool ResponseReceived,
    [property: JsonPropertyName("responseBytes")] int? ResponseBytes,
    [property: JsonPropertyName("returnedIds")] IReadOnlyList<string> ReturnedIds,
    [property: JsonPropertyName("nullSpanIds")] IReadOnlyList<string> NullSpanIds,
    [property: JsonPropertyName("malformedIds")] IReadOnlyList<string> MalformedIds,
    [property: JsonPropertyName("invalidBoundaryIds")] IReadOnlyList<string> InvalidBoundaryIds,
    [property: JsonPropertyName("invalidPairIds")] IReadOnlyList<string> InvalidPairIds);

public sealed record PdfLossInstrumentation(
    [property: JsonPropertyName("roleProposalsTotal")] int RoleProposalsTotal,
    [property: JsonPropertyName("unknownRoleCount")] int UnknownRoleCount,
    [property: JsonPropertyName("aliasNormalizedCount")] int AliasNormalizedCount,
    [property: JsonPropertyName("spanRequested")] int SpanRequested,
    [property: JsonPropertyName("spanResponseNull")] int SpanResponseNull,
    [property: JsonPropertyName("spanMalformed")] int SpanMalformed,
    [property: JsonPropertyName("spanInvalidBoundary")] int SpanInvalidBoundary,
    [property: JsonPropertyName("spanInvalidPair")] int SpanInvalidPair,
    [property: JsonPropertyName("spanValidBoundary")] int SpanValidBoundary,
    [property: JsonPropertyName("validatorAccepted")] int ValidatorAccepted,
    [property: JsonPropertyName("validatorRejected")] int ValidatorRejected,
    [property: JsonPropertyName("validatorRejectedByReason")] IReadOnlyDictionary<string, int> ValidatorRejectedByReason,
    [property: JsonPropertyName("hierarchyProposals")] int HierarchyProposals,
    [property: JsonPropertyName("hierarchyAccepted")] int HierarchyAccepted,
    [property: JsonPropertyName("items")] IReadOnlyList<PdfLossObservation> Items);

public sealed record PdfLossObservation(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int? SourceOrdinal,
    [property: JsonPropertyName("rawRole")] string? RawRole,
    [property: JsonPropertyName("canonicalRole")] string CanonicalRole,
    [property: JsonPropertyName("aliasNormalized")] bool AliasNormalized,
    [property: JsonPropertyName("proposedSpan")] TextOffsetSpan? ProposedSpan,
    [property: JsonPropertyName("spanStatus")] string SpanStatus,
    [property: JsonPropertyName("allowedBoundary")] bool? AllowedBoundary,
    [property: JsonPropertyName("validatorStatus")] string ValidatorStatus,
    [property: JsonPropertyName("validatorReason")] string? ValidatorReason,
    [property: JsonPropertyName("hierarchyStatus")] string? HierarchyStatus,
    [property: JsonPropertyName("firstLoss")] string? FirstLoss);

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
    [property: JsonPropertyName("attempts")] IReadOnlyList<DocxHeaderExtractor.Core.Vision.PdfVisualAttemptOutcome>? Attempts = null);
