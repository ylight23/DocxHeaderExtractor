using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Web;

public sealed record WebHeadingSource(string SourceId, int SourceOrdinal, StructuralSpan Span,
    string Availability, string? SourceText, string? SelectedText, SourceAnchor? Coordinates);
public sealed record WebHeadingNode(string ElementId, string? StableId, string SourceId, string Text,
    int? Level, string? ParentId, string ParentStatus, IReadOnlyList<string> Children,
    string? HierarchyResolution, StructuralDecision Decision, StructuralValidation Validation,
    string? BoundaryEvidence, string? InlineBody, TextOffsetSpan? InlineBodySpan,
    IReadOnlyList<WebHeadingSource> Sources);
public sealed record WebPipelineStage(string Id, string Status, string Evidence);
public sealed record WebPipelineSummary(int Accepted, int UnknownLevel, int RequiresReview,
    int? Rejected, string RejectedAvailability, int FilteredValidated);
public sealed record WebPipelineResponse(string SchemaVersion, Guid ExecutionId, string Availability,
    string Outcome, string SourceKind, IReadOnlyList<WebHeadingNode> Headings,
    IReadOnlyList<StructuralRelation> Relations, IReadOnlyList<string> Roots, IReadOnlyList<string> Unresolved,
    WebPipelineSummary Summary, IReadOnlyList<WebPipelineStage> Stages,
    IReadOnlyList<string> Checkpoints, object? Provenance, object? Audit);

/// <summary>Read-only Web projection of one retained execution. No parsing, inference or authority reconstruction.</summary>
public static class WebPipelineProjection
{
    public static WebPipelineResponse Project(DocumentAgentRunResult run, string sourceKind)
    {
        var execution = run.Execution;
        if (execution is null || !ReferenceEquals(execution.Outline, run.TaskResult.Value))
            return new("web-pipeline-v2", run.RunId, "unavailable", run.Outcome.ToString(), sourceKind,
                [], [], [], [], new(0, 0, 0, null, "not-recorded", 0),
                [new("execution", "not-recorded", "Same-execution envelope was not supplied")], [], null, null);

        var graph = execution.Result.Structure;
        var pipeline = execution.HeadingPipeline;
        var ids = pipeline?.EmittedElementIds ?? graph.Elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var elements = graph.Elements.Where(e => ids.Contains(e.Id)).ToArray();
        var visible = elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        // Parent authority comes ONLY from the admitted graph, never ParentId compatibility views or levels.
        var allRelations = graph.Relations.Where(r => r.Type == StructuralRelationType.ParentChild).ToArray();
        var relations = allRelations.Where(r => visible.Contains(r.FromId) && visible.Contains(r.ToId)).ToArray();
        var parent = allRelations.ToDictionary(r => r.ToId, r => r.FromId, StringComparer.Ordinal);
        var sources = execution.Result.SourceCatalog.Units.ToDictionary(u => u.SourceId, StringComparer.Ordinal);
        var context = pipeline?.ProjectionContext ?? HeadingProjectionContext.Empty;
        var records = HeadingOutlineProjection.Project(graph, ids, context);
        var headings = elements.Select((element, index) =>
        {
            var record = records[index];
            var parentId = parent.GetValueOrDefault(element.Id);
            var status = parentId is not null ? visible.Contains(parentId) ? "validated-parent" : "parent-not-emitted"
                : record.HierarchyResolution?.Contains("unresolved", StringComparison.OrdinalIgnoreCase) == true
                    ? "unresolved" : "no-validated-parent";
            return new WebHeadingNode(element.Id, record.StableId, record.SourceId ?? element.Sources[0].SourceId,
                record.Text, record.Level, parentId, status,
                relations.Where(r => r.FromId == element.Id).Select(r => r.ToId).ToArray(),
                record.HierarchyResolution, element.Decision, element.Validation, record.BoundarySource,
                record.InlineBody, record.InlineBodySpan, element.Sources.Select(s =>
                {
                    var unit = sources.GetValueOrDefault(s.SourceId);
                    var valid = unit is not null && s.Span.IsValidFor(unit.Text);
                    return new WebHeadingSource(s.SourceId, s.SourceOrdinal, s.Span,
                        valid ? "source-catalog" : "unavailable", unit?.Text,
                        valid ? unit!.Text[s.Span.Start..s.Span.End] : null, unit?.SourceAnchor);
                }).ToArray());
        }).ToArray();
        var audit = pipeline?.Audit ?? execution.Outline.RouteAudit;
        var stages = new List<WebPipelineStage> { new("source-detection", "completed", "Web uploaded-byte detector"),
            new("source-parsing", "completed", "Production execution source catalog") };
        var lane = audit?.SemanticLane;
        stages.Add(new("semantic-execution", lane?.Status switch
        { "complete" => "completed", "not-run" => "skipped", "failed" => "failed", "timed-out" => "failed", _ => "not-recorded" },
            lane is null ? "No lane observation" : $"Producer lane status: {lane.Status}; failure: {lane.FailureClass ?? "none"}"));
        if (sourceKind == "pdf")
            foreach (var stage in new[] { "F1-semantic-function", "G2A-anchor-existence", "H2-C-boundary-extent" })
                stages.Add(lane?.Status == "not-run"
                    ? new(stage, "skipped", $"Producer semantic lane explicitly not-run: {lane.FailureClass ?? "not recorded"}")
                    : new(stage, "not-recorded", "Declared protocols are not per-stage execution observations"));
        stages.Add(new("exact-binding-validation", elements.Length > 0 ? "completed" : "not-recorded",
            elements.Length > 0 ? "Validated structural elements" : "No binding-stage observation"));
        stages.Add(new("hierarchy-placement", audit?.ValidatedStructures.Count > 0 ? "completed" : "not-recorded",
            "Producer hierarchy records; no hierarchy is inferred by Web"));
        stages.Add(new("projection", "completed", "Returned production outline"));
        stages.Add(new("final-result", "completed", "Validated harness task result"));
        return new("web-pipeline-v2", run.RunId, "same-execution", run.Outcome.ToString(), sourceKind, headings,
            relations, headings.Where(h => h.ParentStatus == "no-validated-parent").Select(h => h.ElementId).ToArray(),
            headings.Where(h => h.ParentStatus is "unresolved" or "parent-not-emitted").Select(h => h.ElementId).ToArray(),
            new(headings.Length, headings.Count(h => h.Level is null),
                records.Count(h => h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed), null, "not-recorded",
                graph.Elements.Count - elements.Length), stages, pipeline?.CheckpointObservations ?? [],
            new { pipeline = execution.Result.Provenance.Route, executionContract = execution.Result.Provenance.ExecutionContract,
                sourceCatalogKind = execution.Result.Provenance.SourceCatalogKind,
                reportedProviderCalls = execution.Result.Provenance.ProviderCalls,
                providerCallBasis = "producer-provenance; not an independent transport-attempt counter",
                model = execution.Outline.Model, sentDataExternally = execution.Outline.Provenance?.SentDataExternally },
            SafeAudit(audit));
    }

    public static object? SafeAudit(PipelineExecutionAudit? audit) => audit is null ? null : new
    { audit.PipelineId, audit.SourceBlocksAvailable, audit.SourceBlocksSelected, audit.SourcePagesAvailable,
        audit.SourcePagesSelected, audit.SemanticLane, audit.SpanLane,
        groundedSourceIds = audit.GroundedBlockIds, decisionCount = audit.BlockDecisions.Count };

    /// <summary>Keep outline compatibility fields while withholding transport completions/payloads from the Web.</summary>
    public static JsonNode SafeOutline(DocumentOutline outline, JsonSerializerOptions options)
    {
        var node = JsonSerializer.SerializeToNode(outline, options)!;
        if (outline.RouteAudit is not null)
            node["routeAudit"] = JsonSerializer.SerializeToNode(SafeAudit(outline.RouteAudit), options);
        return node;
    }
}
