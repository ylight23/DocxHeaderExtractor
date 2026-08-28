using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Serialization helpers for the diagnostic-only <c>pdf-stage-eval</c> payload. These objects only
/// preserve facts the route already computed; they do not feed candidate generation, ranking,
/// validation, grounding, or product output.
/// </summary>
public static class PdfStageEvalDiagnostics
{
    public static PdfStageEvalLaneDiagnostics BuildLaneDiagnostics(RouteExecutionAudit audit) =>
        new(audit.SemanticLane, audit.SpanLane, audit.VisualLane);

    public static PdfStageEvalProposalResolutionDiagnostics BuildProposalResolutionDiagnostics(
        IReadOnlyList<PdfProposalResolutionAudit> resolutions,
        bool includeItems) =>
        new(
            resolutions.GroupBy(item => item.Resolution)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new PdfStageEvalProposalResolutionDecision(group.Key, group.Count()))
                .ToArray(),
            includeItems ? resolutions : null);
}

public sealed record PdfStageEvalLaneDiagnostics(
    [property: JsonPropertyName("semanticLane")] RouteLaneExecutionAudit? SemanticLane,
    [property: JsonPropertyName("spanLane")] RouteLaneExecutionAudit? SpanLane,
    [property: JsonPropertyName("visualLane")] RouteLaneExecutionAudit? VisualLane)
{
    /// <summary>
    /// Back-compatibility shape for older readers that consumed <c>rows[].lanes.semantic</c>.
    /// </summary>
    [JsonPropertyName("lanes")]
    public PdfStageEvalLegacyLanes Lanes => new(SemanticLane, SpanLane, VisualLane);
}

public sealed record PdfStageEvalLegacyLanes(
    [property: JsonPropertyName("semantic")] RouteLaneExecutionAudit? Semantic,
    [property: JsonPropertyName("span")] RouteLaneExecutionAudit? Span,
    [property: JsonPropertyName("visual")] RouteLaneExecutionAudit? Visual);

public sealed record PdfStageEvalProposalResolutionDiagnostics(
    [property: JsonPropertyName("decisions")] IReadOnlyList<PdfStageEvalProposalResolutionDecision> Decisions,
    [property: JsonPropertyName("items")] IReadOnlyList<PdfProposalResolutionAudit>? Items);

public sealed record PdfStageEvalProposalResolutionDecision(
    [property: JsonPropertyName("resolution")] string Resolution,
    [property: JsonPropertyName("count")] int Count);
