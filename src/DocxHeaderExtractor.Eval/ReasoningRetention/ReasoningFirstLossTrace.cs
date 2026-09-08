using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public static class ReasoningTraceStage
{
    public const string SourceVisible = "SOURCE_VISIBLE";
    public const string ModelRequestOwner = "MODEL_REQUEST_OWNER";
    public const string ModelProposed = "MODEL_PROPOSED";
    public const string BoundToSource = "BOUND_TO_SOURCE";
    public const string Deduped = "DEDUPED";
    public const string ConflictStatus = "CONFLICT_STATUS";
    public const string HardValidated = "HARD_VALIDATED";
    public const string HierarchyEdgeStatus = "HIERARCHY_EDGE_STATUS";
    public const string LevelDerived = "LEVEL_DERIVED";
    public const string ProjectionStatus = "PROJECTION_STATUS";
    public const string FinalIncluded = "FINAL_INCLUDED";
}

public static class ReasoningTraceLoss
{
    public const string ModelOmission = "MODEL_OMISSION";
    public const string ModelSpanError = "MODEL_SPAN_ERROR";
    public const string ModelFalsePositive = "MODEL_FALSE_POSITIVE";
    public const string SystemSegmentationLoss = "SYSTEM_SEGMENTATION_LOSS";
    public const string SystemBindingLoss = "SYSTEM_BINDING_LOSS";
    public const string SystemDedupeLoss = "SYSTEM_DEDUPE_LOSS";
    public const string SystemValidatorLoss = "SYSTEM_VALIDATOR_LOSS";
    public const string SystemHierarchyLoss = "SYSTEM_HIERARCHY_LOSS";
    public const string SystemProjectionLoss = "SYSTEM_PROJECTION_LOSS";
}

public sealed record ReasoningFirstLossTraceRecord
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("proposalId")] public required string ProposalId { get; init; }
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("span")] public required StructuralSpan Span { get; init; }
    [JsonPropertyName("stages")] public required IReadOnlyDictionary<string, bool> Stages { get; init; }
    [JsonPropertyName("firstLoss")] public string? FirstLoss { get; init; }
    [JsonPropertyName("lossReason")] public string? LossReason { get; init; }
}

public static class ReasoningFirstLossTraceBuilder
{
    public static ReasoningFirstLossTraceRecord Build(
        string documentId,
        ReasoningHeadingProposal proposal,
        bool sourceVisible,
        bool modelRequestOwner,
        bool modelProposed,
        bool boundToSource,
        bool deduped,
        bool hardValidated,
        bool hierarchyEdgeValid,
        bool levelDerived,
        ReasoningProjectionDecision projection,
        bool finalIncluded)
    {
        var stages = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [ReasoningTraceStage.SourceVisible] = sourceVisible,
            [ReasoningTraceStage.ModelRequestOwner] = modelRequestOwner,
            [ReasoningTraceStage.ModelProposed] = modelProposed,
            [ReasoningTraceStage.BoundToSource] = boundToSource,
            [ReasoningTraceStage.Deduped] = deduped,
            [ReasoningTraceStage.ConflictStatus] = true,
            [ReasoningTraceStage.HardValidated] = hardValidated,
            [ReasoningTraceStage.HierarchyEdgeStatus] = hierarchyEdgeValid,
            [ReasoningTraceStage.LevelDerived] = levelDerived,
            [ReasoningTraceStage.ProjectionStatus] = true,
            [ReasoningTraceStage.FinalIncluded] = finalIncluded,
        };
        var firstLoss = FirstLoss(stages, projection);
        return new ReasoningFirstLossTraceRecord
        {
            DocumentId = documentId,
            ProposalId = ReasoningGlobalHierarchyPass.ProposalId(documentId, proposal.SourceId, proposal.HeadingSpan),
            SourceId = proposal.SourceId,
            Span = proposal.HeadingSpan,
            Stages = stages,
            FirstLoss = firstLoss.Stage,
            LossReason = firstLoss.Reason,
        };
    }

    private static (string? Stage, string? Reason) FirstLoss(IReadOnlyDictionary<string, bool> stages, ReasoningProjectionDecision projection)
    {
        if (!stages[ReasoningTraceStage.SourceVisible]) return (ReasoningTraceStage.SourceVisible, ReasoningTraceLoss.SystemSegmentationLoss);
        if (!stages[ReasoningTraceStage.ModelRequestOwner]) return (ReasoningTraceStage.ModelRequestOwner, ReasoningTraceLoss.SystemBindingLoss);
        if (!stages[ReasoningTraceStage.ModelProposed]) return (ReasoningTraceStage.ModelProposed, ReasoningTraceLoss.ModelOmission);
        if (!stages[ReasoningTraceStage.BoundToSource]) return (ReasoningTraceStage.BoundToSource, ReasoningTraceLoss.ModelSpanError);
        if (!stages[ReasoningTraceStage.Deduped]) return (ReasoningTraceStage.Deduped, ReasoningTraceLoss.SystemDedupeLoss);
        if (!stages[ReasoningTraceStage.HardValidated]) return (ReasoningTraceStage.HardValidated, ReasoningTraceLoss.SystemValidatorLoss);
        if (!stages[ReasoningTraceStage.HierarchyEdgeStatus]) return (ReasoningTraceStage.HierarchyEdgeStatus, ReasoningTraceLoss.SystemHierarchyLoss);
        if (!stages[ReasoningTraceStage.FinalIncluded] && projection.Status == ReasoningTaskProjection.Excluded)
            return (ReasoningTraceStage.ProjectionStatus, ReasoningTraceLoss.SystemProjectionLoss);
        return (null, null);
    }
}
