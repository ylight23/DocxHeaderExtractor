using System.Text.Json;
using System.Text.Json.Nodes;
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
    public static WebPipelineResponse Project(Guid runId, string outcome, DocumentOutline outline,
        DocumentExtractionExecutionResult? execution, string sourceKind)
    {
        if (execution is null || !ReferenceEquals(execution.Outline, outline))
            return new("web-pipeline-v2", runId, "unavailable", outcome, sourceKind,
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
        { "complete" => "completed", "partial" => "partial", "not-run" => "skipped", "failed" => "failed", "timed-out" => "failed", _ => "not-recorded" },
            lane is null ? "No lane observation" : $"Producer lane status: {lane.Status}; failure: {lane.FailureClass ?? "none"}"));
        // The PDF lane's model steps since the V6 promotion: line selection per layout segment, then grouping of adjacent selected lines.
        if (sourceKind is "pdf" or "scan" or "docx")
        {
            var grouping = audit?.Grouping;
            stages.Add(lane?.Status switch
            {
                "not-run" => new("V6-line-selection", "skipped", $"Producer semantic lane explicitly not-run: {lane.FailureClass ?? "not recorded"}"),
                "complete" => new("V6-line-selection", "completed", $"Segments decided: {lane.Completed}/{lane.Scheduled}"),
                "partial" => new("V6-line-selection", "partial", $"Segments decided: {lane.Completed}/{lane.Scheduled}"),
                _ => new("V6-line-selection", "not-recorded", "No lane observation"),
            });
            stages.Add(lane?.Status is "complete" or "partial"
                ? new("V6-line-grouping", grouping is null ? "not-recorded" : grouping.UnresolvedAliases > 0 || grouping.FallbackRuns > 0 ? "partial" : "completed",
                    grouping is null ? "Grouping execution counters not recorded" :
                    $"Model divisions={grouping.ModelDividedRuns}; actual fallback runs={grouping.FallbackRuns}; disputes={grouping.DisputedRuns}; verification checks={grouping.VerificationChecks}; verified={grouping.VerifiedRuns}; membership disputes={grouping.MembershipDisputes}; boundary disputes={grouping.BoundaryDisputes}; unresolved aliases={grouping.UnresolvedAliases}; ignored review changes={grouping.IgnoredReviewChanges}")
                : new("V6-line-grouping", lane?.Status == "not-run" ? "skipped" : "not-recorded", "No grouping observation"));
            stages.Add(grouping is null
                ? new("V6-group-verification", lane?.Status == "not-run" ? "skipped" : "not-recorded", "No verification counters recorded")
                : new("V6-group-verification", grouping.UnresolvedAliases > 0 ? "partial" : grouping.VerificationChecks > 0 ? "completed" : "skipped",
                    $"Checks={grouping.VerificationChecks}; accepted={grouping.VerifiedRuns}; membership disputes={grouping.MembershipDisputes}; boundary disputes={grouping.BoundaryDisputes}; unresolved aliases={grouping.UnresolvedAliases}"));
        }
        stages.Add(new("exact-binding-validation", elements.Length > 0 ? "completed" : "not-recorded",
            elements.Length > 0 ? "Validated structural elements" : "No binding-stage observation"));
        var placement = audit?.PlacementExecution;
        stages.Add(placement is null
            ? new("hierarchy-placement", "not-recorded", "No placement execution observation; hierarchy records alone do not prove a placement call")
            : new("hierarchy-placement", placement.Status switch
                {
                    "placement-accepted" when placement.UnresolvedCount > 0 => "partial",
                    "placement-accepted" => "completed",
                    "placement-unresolved" => "unresolved",
                    "placement-invalid-response" or "placement-transport-failed" => "failed",
                    "placement-not-required" => "skipped",
                    _ => "not-recorded",
                },
                $"Producer {placement.Status}; requested={placement.RequestedCount}; placed={placement.PlacedCount}; outside-tree={placement.OutOfHierarchyCount}; unresolved={placement.UnresolvedCount}; failure={placement.FailureClass ?? "none"}"));
        stages.Add(new("projection", "completed", "Returned production outline"));
        stages.Add(new("final-result", outcome == "Partial" ? "partial" : "completed", "Validated pipeline task result"));
        return new("web-pipeline-v2", runId, "same-execution", outcome, sourceKind, headings,
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
        groundedSourceIds = audit.GroundedBlockIds, decisionCount = audit.BlockDecisions.Count,
        placementExecution = audit.PlacementExecution, audit.Scan, audit.Grouping };

    /// <summary>Keep outline compatibility fields while withholding transport completions/payloads from the Web.</summary>
    public static JsonNode SafeOutline(DocumentOutline outline, JsonSerializerOptions options)
    {
        var node = JsonSerializer.SerializeToNode(outline, options)!;
        if (outline.RouteAudit is not null)
            node["routeAudit"] = JsonSerializer.SerializeToNode(SafeAudit(outline.RouteAudit), options);
        return node;
    }
}
