using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>Auditable losses for a bounded route, especially PDF source/LLM/grounding pipelines.</summary>
public sealed record RouteExecutionAudit(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("sourceBlocksAvailable")] int SourceBlocksAvailable,
    [property: JsonPropertyName("sourceBlocksSelected")] int SourceBlocksSelected,
    [property: JsonPropertyName("sourcePagesAvailable")] int SourcePagesAvailable,
    [property: JsonPropertyName("sourcePagesSelected")] int SourcePagesSelected,
    [property: JsonPropertyName("sourceBlocks")] IReadOnlyList<RouteBlockAudit> SourceBlocks,
    [property: JsonPropertyName("selectedSourceBlocks")] IReadOnlyList<RouteBlockAudit> SelectedSourceBlocks,
    [property: JsonPropertyName("blockDecisions")] IReadOnlyList<RouteBlockDecisionAudit> BlockDecisions,
    [property: JsonPropertyName("groundedBlockIds")] IReadOnlyList<string> GroundedBlockIds)
{
    /// <summary>Stable route identity for the promoted execution authority.</summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    /// <summary>Explicit source-to-representation lineage captured at the route boundary.</summary>
    [JsonIgnore]
    public IReadOnlyList<RouteSourceRepresentation> SourceRepresentations { get; init; } = [];

    /// <summary>Provider request membership without retaining prompts or raw completions.</summary>
    [JsonIgnore]
    public IReadOnlyList<RouteModelRequestAudit> ModelRequests { get; init; } = [];

    /// <summary>One row per parser-owned source occurrence, including unknown stages.</summary>
    [JsonIgnore]
    public IReadOnlyList<RouteOccurrenceTrace> OccurrenceTraces { get; init; } = [];

    /// <summary>Source identities selected before any provider execution; route-local id is diagnostic only.</summary>
    [JsonPropertyName("selectedSourceIdentities")]
    public IReadOnlyList<PdfSelectedSourceIdentity> SelectedSourceIdentities { get; init; } = [];

    /// <summary>Raw analyst completions, populated only by explicit diagnostic routes.</summary>
    [JsonPropertyName("rawAnalystResponses")]
    public IReadOnlyList<string> RawAnalystResponses { get; init; } = [];

    [JsonPropertyName("modelInputContracts")]
    public IReadOnlyList<string> ModelInputContracts { get; init; } = [];

    /// <summary>Per-source source/model/validation trace for PDF-first audit routes.</summary>
    [JsonPropertyName("sourceStageTraces")]
    public IReadOnlyList<PdfSemanticSourceStageTrace> SourceStageTraces { get; init; } = [];

    [JsonPropertyName("validatedStructures")]
    public IReadOnlyList<PdfValidatedStructure> ValidatedStructures { get; init; } = [];

    [JsonPropertyName("hierarchyProposals")]
    public IReadOnlyList<PdfHierarchyProposalAudit> HierarchyProposals { get; init; } = [];

    /// <summary>M8.1 source-only evidence inventory for already validated headings.</summary>
    [JsonPropertyName("hierarchyFacts")]
    public IReadOnlyList<PdfHierarchyFactAudit> HierarchyFacts { get; init; } = [];

    /// <summary>
    /// Measured semantic disagreement for this run. Reported so the question "does this route need
    /// an adjudication model" is answered from counted conflict, not assumed conflict.
    /// </summary>
    [JsonPropertyName("conflictCensus")]
    public Pipeline.SemanticConflictCensus ConflictCensus { get; init; } =
        Pipeline.SemanticConflictCensus.Empty;

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

    [JsonPropertyName("batchTelemetry")]
    public PdfPipelineBatchTelemetry? BatchTelemetry { get; init; }
}

public sealed record PdfSelectedSourceIdentity(
    [property: JsonPropertyName("routeBlockIdDiagnostic")] string RouteBlockIdDiagnostic,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("sourceLineIds")] IReadOnlyList<string> SourceLineIds,
    [property: JsonPropertyName("sourceText")] string SourceText,
    [property: JsonPropertyName("sourceSpan")] TextOffsetSpan? SourceSpan = null);

public sealed record RouteSourceRepresentation(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("representationId")] string RepresentationId,
    [property: JsonPropertyName("representationKind")] string RepresentationKind,
    [property: JsonPropertyName("routeBlockId")] string? RouteBlockId,
    [property: JsonPropertyName("lineageMethod")] string LineageMethod);

public sealed record RouteModelRequestAudit(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("routeBlockIds")] IReadOnlyList<string> RouteBlockIds,
    [property: JsonPropertyName("providerCallAttempted")] bool ProviderCallAttempted,
    [property: JsonPropertyName("responseObserved")] bool ResponseObserved,
    [property: JsonPropertyName("status")] string Status);

public sealed record RouteOccurrenceTrace
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public required string DocumentGroupId { get; init; }
    [JsonPropertyName("sourceSha256")] public required string SourceSha256 { get; init; }
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("stableId")] public string? StableId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public required int SourceOrdinal { get; init; }
    [JsonPropertyName("sourceSpan")] public required TextOffsetSpan SourceSpan { get; init; }
    [JsonPropertyName("representationId")] public string? RepresentationId { get; init; }
    [JsonPropertyName("representationKind")] public string? RepresentationKind { get; init; }
    [JsonPropertyName("routeBlockId")] public string? RouteBlockId { get; init; }
    [JsonPropertyName("routeOwner")] public required string RouteOwner { get; init; }
    [JsonPropertyName("routeBlockConstructed")] public bool? RouteBlockConstructed { get; init; }
    [JsonPropertyName("routeBlockSelected")] public bool? RouteBlockSelected { get; init; }
    [JsonPropertyName("modelRequestIds")] public IReadOnlyList<string> ModelRequestIds { get; init; } = [];
    [JsonPropertyName("modelRequestMembership")] public required string ModelRequestMembership { get; init; }
    [JsonPropertyName("modelProposalPresent")] public bool? ModelProposalPresent { get; init; }
    [JsonPropertyName("modelSemanticFunction")] public string? ModelSemanticFunction { get; init; }
    [JsonPropertyName("modelLevel")] public int? ModelLevel { get; init; }
    [JsonPropertyName("modelParent")] public string? ModelParent { get; init; }
    [JsonPropertyName("modelSpan")] public TextOffsetSpan? ModelSpan { get; init; }
    [JsonPropertyName("validationStatus")] public string? ValidationStatus { get; init; }
    [JsonPropertyName("validationIssues")] public IReadOnlyList<string> ValidationIssues { get; init; } = [];
    [JsonPropertyName("markerBefore")] public string? MarkerBefore { get; init; }
    [JsonPropertyName("markerAfter")] public string? MarkerAfter { get; init; }
    [JsonPropertyName("markerReason")] public string? MarkerReason { get; init; }
    [JsonPropertyName("structuralBefore")] public string? StructuralBefore { get; init; }
    [JsonPropertyName("structuralAfter")] public string? StructuralAfter { get; init; }
    [JsonPropertyName("structuralReason")] public string? StructuralReason { get; init; }
    [JsonPropertyName("finalIncluded")] public bool FinalIncluded { get; init; }
    [JsonPropertyName("finalRole")] public string? FinalRole { get; init; }
    [JsonPropertyName("finalLevel")] public int? FinalLevel { get; init; }
    [JsonPropertyName("finalParent")] public string? FinalParent { get; init; }
    [JsonPropertyName("finalSpan")] public TextOffsetSpan? FinalSpan { get; init; }
}

public sealed record RouteLaneExecutionAudit(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("scheduled")] int Scheduled,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("timedOut")] int TimedOut,
    [property: JsonPropertyName("notStarted")] int NotStarted,
    [property: JsonPropertyName("failureClass")] string? FailureClass = null);

public sealed record PdfPipelineBatchTelemetry(
    [property: JsonPropertyName("sourceParagraphCount")] int SourceParagraphCount,
    [property: JsonPropertyName("roleInputBlockCount")] int RoleInputBlockCount,
    [property: JsonPropertyName("roleBatchCount")] int RoleBatchCount,
    [property: JsonPropertyName("roleProviderCalls")] int RoleProviderCalls,
    [property: JsonPropertyName("roleInputTokensTotal")] int RoleInputTokensTotal,
    [property: JsonPropertyName("roleLargestBatchBlocks")] int RoleLargestBatchBlocks,
    [property: JsonPropertyName("roleLargestBatchTokens")] int RoleLargestBatchTokens,
    [property: JsonPropertyName("headingLikeAfterRole")] int HeadingLikeAfterRole,
    [property: JsonPropertyName("spanBatchCount")] int SpanBatchCount,
    [property: JsonPropertyName("spanProviderCalls")] int SpanProviderCalls,
    [property: JsonPropertyName("spanInputTokensTotal")] int SpanInputTokensTotal,
    [property: JsonPropertyName("hierarchyInputCount")] int HierarchyInputCount,
    [property: JsonPropertyName("hierarchyProviderCalls")] int HierarchyProviderCalls,
    [property: JsonPropertyName("totalProviderCalls")] int TotalProviderCalls,
    [property: JsonPropertyName("totalResponses")] int TotalResponses,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs);

/// <summary>
/// One model-proposed parent link and what deterministic validation did with it.
/// <para>
/// Part of <see cref="RouteExecutionAudit.HierarchyProposals"/>, so it belongs with the audit
/// contract. It used to live inside <c>PdfSemanticHierarchyFallback</c> - the model stage that
/// produced it - which meant deleting that dead stage would have taken a live production type with
/// it.
/// </para>
/// </summary>
public sealed record PdfHierarchyProposalAudit(
    string Id,
    string? ProposedParentId,
    string? ResolvedParentId,
    string Resolution);

public sealed record RouteBlockAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("text")] string Text);

public sealed record RouteBlockDecisionAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("semanticFunction")] string? SemanticFunction,
    [property: JsonPropertyName("reason")] string? Reason = null)
{
    [JsonIgnore] public string? ProposedParentId { get; init; }
    [JsonIgnore] public TextOffsetSpan? ProposedSourceSpan { get; init; }
}
