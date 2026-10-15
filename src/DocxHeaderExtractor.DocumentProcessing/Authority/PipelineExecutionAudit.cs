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

    /// <summary>Raw model completions, populated only by explicit diagnostic routes.</summary>
    [JsonPropertyName("rawModelResponses")]
    public IReadOnlyList<string> RawModelResponses { get; init; } = [];

    [JsonPropertyName("modelInputContracts")]
    public IReadOnlyList<string> ModelInputContracts { get; init; } = [];

    [JsonPropertyName("validatedStructures")]
    public IReadOnlyList<ResolvedHeadingHierarchy> ValidatedStructures { get; init; } = [];

    /// <summary>M8.1 source-only evidence inventory for already validated headings.</summary>
    [JsonPropertyName("hierarchyFacts")]
    public IReadOnlyList<HeadingHierarchyFactAudit> HierarchyFacts { get; init; } = [];

    /// <summary>Independent semantic execution outcome. A timeout is partial work, not provider unavailability.</summary>
    [JsonPropertyName("semanticLane")]
    public LaneExecutionAudit? SemanticLane { get; init; }

    /// <summary>
    /// Independent span-resolution outcome. Reported separately from <see cref="SemanticLane"/> on
    /// purpose: a heading needs a resolved span to pass validation, so this lane can fail while the
    /// role lane completes - and folding it into the existing field would change what that field has
    /// always meant.
    /// </summary>
    [JsonPropertyName("spanLane")]
    public LaneExecutionAudit? SpanLane { get; init; }

    /// <summary>Runtime-only placement observation; frozen audit JSON remains unchanged.</summary>
    [JsonIgnore]
    public HeadingPlacementExecutionObservation? PlacementExecution { get; init; }
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

public sealed record SourceBlockAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("text")] string Text);

public sealed record SourceBlockDecisionAudit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("semanticFunction")] string? SemanticFunction,
    [property: JsonPropertyName("reason")] string? Reason = null);
