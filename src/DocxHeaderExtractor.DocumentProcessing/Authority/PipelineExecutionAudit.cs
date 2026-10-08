using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Materialization;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>Auditable losses for a bounded pipeline, especially PDF source/LLM/grounding pipelines.</summary>
public sealed record PipelineExecutionAudit(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("sourceBlocksAvailable")] int SourceBlocksAvailable,
    [property: JsonPropertyName("sourceBlocksSelected")] int SourceBlocksSelected,
    [property: JsonPropertyName("sourcePagesAvailable")] int SourcePagesAvailable,
    [property: JsonPropertyName("sourcePagesSelected")] int SourcePagesSelected,
    [property: JsonPropertyName("sourceBlocks")] IReadOnlyList<SourceBlockAudit> SourceBlocks,
    [property: JsonPropertyName("selectedSourceBlocks")] IReadOnlyList<SourceBlockAudit> SelectedSourceBlocks,
    [property: JsonPropertyName("blockDecisions")] IReadOnlyList<SourceBlockDecisionAudit> BlockDecisions,
    [property: JsonPropertyName("groundedBlockIds")] IReadOnlyList<string> GroundedBlockIds)
{
    /// <summary>Stable pipeline identity for the promoted execution authority.</summary>
    [JsonPropertyName("route")]
    public string? PipelineId { get; init; }

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
    public IReadOnlyList<HeadingSourceStageTrace> SourceStageTraces { get; init; } = [];

    [JsonPropertyName("validatedStructures")]
    public IReadOnlyList<ResolvedHeadingHierarchy> ValidatedStructures { get; init; } = [];

    [JsonPropertyName("hierarchyProposals")]
    public IReadOnlyList<PdfHierarchyProposalAudit> HierarchyProposals { get; init; } = [];

    /// <summary>M8.1 source-only evidence inventory for already validated headings.</summary>
    [JsonPropertyName("hierarchyFacts")]
    public IReadOnlyList<HeadingHierarchyFactAudit> HierarchyFacts { get; init; } = [];

    /// <summary>
    /// Measured semantic disagreement for this run. Reported so the question "does this route need
    /// an adjudication model" is answered from counted conflict, not assumed conflict.
    /// </summary>
    [JsonPropertyName("conflictCensus")]
    public Semantics.Canonical.SemanticConflictCensus ConflictCensus { get; init; } =
        Semantics.Canonical.SemanticConflictCensus.Empty;

    /// <summary>Independent semantic execution outcome. A timeout is partial work, not provider unavailability.</summary>
    [JsonPropertyName("semanticLane")]
    public LaneExecutionAudit? SemanticLane { get; init; }

    /// <summary>Independent visual execution outcome.</summary>
    [JsonPropertyName("visualLane")]
    public LaneExecutionAudit? VisualLane { get; init; }

    /// <summary>
    /// Independent span-resolution outcome. Reported separately from <see cref="SemanticLane"/> on
    /// purpose: a heading needs a resolved span to pass validation, so this lane can fail while the
    /// role lane completes - and folding it into the existing field would change what that field has
    /// always meant.
    /// </summary>
    [JsonPropertyName("spanLane")]
    public LaneExecutionAudit? SpanLane { get; init; }

    [JsonPropertyName("batchTelemetry")]
    public HeadingAuthorityBatchTelemetry? BatchTelemetry { get; init; }
}

public sealed record PdfSelectedSourceIdentity(
    [property: JsonPropertyName("routeBlockIdDiagnostic")] string RouteBlockIdDiagnostic,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("sourceLineIds")] IReadOnlyList<string> SourceLineIds,
    [property: JsonPropertyName("sourceText")] string SourceText,
    [property: JsonPropertyName("sourceSpan")] TextOffsetSpan? SourceSpan = null);

public sealed record LaneExecutionAudit(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("scheduled")] int Scheduled,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("timedOut")] int TimedOut,
    [property: JsonPropertyName("notStarted")] int NotStarted,
    [property: JsonPropertyName("failureClass")] string? FailureClass = null);

public sealed record HeadingAuthorityBatchTelemetry(
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
/// Part of <see cref="PipelineExecutionAudit.HierarchyProposals"/>, so it belongs with the audit
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

public sealed record SourceBlockAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("text")] string Text);

public sealed record SourceBlockDecisionAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("semanticFunction")] string? SemanticFunction,
    [property: JsonPropertyName("reason")] string? Reason = null);
