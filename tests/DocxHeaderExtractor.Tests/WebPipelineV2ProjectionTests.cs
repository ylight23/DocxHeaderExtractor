extern alias WebApp;

using System.Text.Json;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Application.Tasks;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Validation;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using WebApp::DocxHeaderExtractor.Web;

namespace DocxHeaderExtractor.Tests;

public sealed class WebPipelineV2ProjectionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Relations_only_define_parentage_and_filtered_parents_remain_unresolved()
    {
        var graph = ValidatedStructureFactory.Create(
            [Element("a", "s1", 1), Element("b", "s2", 2), Element("c", "s3", 3),
             Element("root", "s4", 2), Element("unknown", "s5", null)],
            [new("a", "b", StructuralRelationType.ParentChild), new("b", "c", StructuralRelationType.ParentChild)]);
        var run = Run(graph);
        var dto = WebPipelineProjection.Project(run, "docx");
        Assert.Equal(["a", "root", "unknown"], dto.Roots);
        Assert.Equal("a", dto.Headings.Single(h => h.ElementId == "b").ParentId);
        Assert.Equal("b", dto.Headings.Single(h => h.ElementId == "c").ParentId);
        Assert.Null(dto.Headings.Single(h => h.ElementId == "root").ParentId); // level 2 is NOT parent evidence
        Assert.Equal(1, dto.Summary.UnknownLevel);
        Assert.Equal(2, dto.Relations.Count);
        var filtered = run.Execution! with { HeadingPipeline = run.Execution.HeadingPipeline! with
            { EmittedElementIds = new HashSet<string> { "b", "c", "root" } } };
        dto = WebPipelineProjection.Project(run with { Execution = filtered }, "docx");
        Assert.Equal(["b"], dto.Unresolved);
        Assert.Equal("parent-not-emitted", dto.Headings.Single(h => h.ElementId == "b").ParentStatus);
        Assert.Equal(["c"], dto.Headings.Single(h => h.ElementId == "b").Children);
        Assert.Equal(2, dto.Summary.FilteredValidated);
    }

    [Fact]
    public void Multipart_duplicate_text_and_ordinal_keep_exact_source_identity_and_spans()
    {
        var first = Element("a", "s1", null) with { Sources =
            [new("s1", 7, new(1, 4)), new("s2", 7, new(0, 3))], Text = "Duplicate Duplicate" };
        var second = Element("b", "s3", null);
        var graph = ValidatedStructureFactory.Create([first, second]);
        var catalog = new DocumentSourceCatalog([
            Unit("s1", 7, "xABCbody"), Unit("s2", 7, "DEF"), Unit("s3", 7, "<img src=x onerror=alert(1)>")]);
        var dto = WebPipelineProjection.Project(Run(graph, catalog), "pdf");
        var heading = dto.Headings[0];
        Assert.Equal(2, heading.Sources.Count);
        Assert.Equal("ABC", heading.Sources[0].SelectedText);
        Assert.Equal("DEF", heading.Sources[1].SelectedText);
        Assert.Equal(new StructuralSpan(1, 4), heading.Sources[0].Span);
        Assert.Equal(["s1", "s2"], heading.Sources.Select(s => s.SourceId));
        Assert.Equal(7, heading.Sources[0].SourceOrdinal);
        Assert.Equal(7, heading.Sources[1].SourceOrdinal);
        Assert.Equal("s3", dto.Headings[1].Sources[0].SourceId);
        Assert.Equal("<img src=x onerror=alert(1)>", dto.Headings[1].Sources[0].SourceText);
        Assert.Empty(dto.Relations);
    }

    [Fact]
    public void Inspector_retains_projection_evidence_without_promoting_it_to_graph_authority()
    {
        var graph = ValidatedStructureFactory.Create([Element("a", "s1", null)]);
        var run = Run(graph);
        var context = new HeadingProjectionContext(new Dictionary<string, HeadingProjectionMetadata>
        { ["a"] = new() { OutlineStableId = "stable-a", HierarchyResolution = "unresolved",
            InlineBody = "body", InlineBodySpan = new(3, 7), BoundarySource = "producer-boundary" } });
        var execution = run.Execution! with { HeadingPipeline = run.Execution.HeadingPipeline! with { ProjectionContext = context } };
        var before = JsonSerializer.Serialize(graph);
        var dto = WebPipelineProjection.Project(run with { Execution = execution }, "docx");
        var h = Assert.Single(dto.Headings);
        Assert.Equal("stable-a", h.StableId);
        Assert.Equal("body", h.InlineBody);
        Assert.Equal("producer-boundary", h.BoundaryEvidence);
        Assert.Equal(["a"], dto.Unresolved);
        Assert.Empty(dto.Roots);
        Assert.Equal(before, JsonSerializer.Serialize(graph));
        Assert.Equal(1, dto.Summary.RequiresReview);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("pdf")]
    public void Empty_execution_has_honest_stage_observations_and_no_fake_rejected_count(string kind)
    {
        var dto = WebPipelineProjection.Project(Run(ValidatedStructureFactory.Create([])), kind);
        Assert.Empty(dto.Headings);
        Assert.Null(dto.Summary.Rejected);
        Assert.Equal("not-recorded", dto.Summary.RejectedAvailability);
        Assert.Equal("source-detection", dto.Stages[0].Id);
        Assert.Equal("source-parsing", dto.Stages[1].Id);
        Assert.Equal("final-result", dto.Stages[^1].Id);
        Assert.Equal("not-recorded", dto.Stages.Single(s => s.Id == "exact-binding-validation").Status);
        Assert.All(dto.Stages.Where(s => s.Id.StartsWith("F1") || s.Id.StartsWith("G2A") || s.Id.StartsWith("H2-C")),
            s => Assert.Equal("not-recorded", s.Status));
        Assert.Equal(kind == "pdf" ? 3 : 0, dto.Stages.Count(s => s.Id.StartsWith("F1") || s.Id.StartsWith("G2A") || s.Id.StartsWith("H2-C")));
    }

    [Fact]
    public void Unavailable_or_stale_execution_cannot_supply_graph_evidence()
    {
        var run = Run(ValidatedStructureFactory.Create([Element("a", "s1", 1)]));
        Assert.Equal("unavailable", WebPipelineProjection.Project(run with { Execution = null }, "docx").Availability);
        var stale = run.Execution! with { Outline = new DocumentOutline { File = "stale", Headings = [] } };
        var dto = WebPipelineProjection.Project(run with { Execution = stale }, "docx");
        Assert.Equal("unavailable", dto.Availability);
        Assert.Empty(dto.Headings);
    }

    [Theory]
    [InlineData("placement-transport-failed", "failed")]
    [InlineData("placement-invalid-response", "failed")]
    [InlineData("placement-unresolved", "unresolved")]
    [InlineData("placement-accepted", "partial")]
    [InlineData("placement-not-required", "skipped")]
    [InlineData("placement-not-requested", "not-recorded")]
    public void Placement_stage_uses_actual_producer_observation_and_does_not_hide_or_reclassify_headings(string status, string expected)
    {
        var run = Run(ValidatedStructureFactory.Create([Element("a", "s1", null)]));
        var audit = new PipelineExecutionAudit("test", 1, 1, 1, 1, [], [], [], [])
        { PlacementExecution = new(status, 1, 0, 0, 0, 1, "safe-failure-class", [new("s1", "O1", "unresolved")]),
          RawAnalystResponses = ["PRIVATE completion"] };
        var execution = run.Execution! with { HeadingPipeline = run.Execution.HeadingPipeline! with { Audit = audit } };
        var dto = WebPipelineProjection.Project(run with { Execution = execution }, "pdf");
        Assert.Equal(expected, dto.Stages.Single(s => s.Id == "hierarchy-placement").Status);
        Assert.Contains(status, dto.Stages.Single(s => s.Id == "hierarchy-placement").Evidence);
        Assert.Null(Assert.Single(dto.Headings).Level);
        Assert.Single(dto.Headings);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(dto, Json));
        Assert.Contains("placementExecution", JsonSerializer.Serialize(dto, Json));
    }

    [Fact]
    public void Web_audit_is_whitelisted_and_runtime_sidecars_do_not_change_compatibility_JSON()
    {
        var run = Run(ValidatedStructureFactory.Create([Element("a", "s1", 1)]));
        var audit = new PipelineExecutionAudit("secret", 1, 1, 1, 1, [], [], [], [])
        { RawAnalystResponses = ["PRIVATE-RAW-SSE APIKEY"], ModelInputContracts = ["F1"] };
        var outline = new DocumentOutline { File = "test.docx", Headings = run.Outline.Headings, RouteAudit = audit };
        var execution = run.Execution! with { Outline = outline, HeadingPipeline = run.Execution.HeadingPipeline! with { Audit = audit } };
        var dto = WebPipelineProjection.Project(run with { Outline = outline, Execution = execution,
            TaskResult = run.TaskResult with { Projection = run.TaskResult.Projection with { Value = outline } } }, "pdf");
        var json = JsonSerializer.Serialize(dto, Json);
        Assert.DoesNotContain("PRIVATE", json);
        Assert.DoesNotContain("rawAnalystResponses", json);
        Assert.DoesNotContain("usage", json);
        var safe = WebPipelineProjection.SafeOutline(outline, Json).ToJsonString();
        Assert.DoesNotContain("PRIVATE", safe);
        Assert.DoesNotContain("rawAnalystResponses", safe);
        Assert.Equal(JsonSerializer.Serialize(run.Execution! with { HeadingPipeline = null }), JsonSerializer.Serialize(run.Execution));
        Assert.Equal(JsonSerializer.Serialize(run with { Execution = null }), JsonSerializer.Serialize(run));
        Assert.Equal(JsonSerializer.Serialize(run.Execution!.HeadingPipeline! with { CheckpointObservations = [] }),
            JsonSerializer.Serialize(run.Execution.HeadingPipeline! with { CheckpointObservations = ["source-selection:completed"] }));
    }

    [Fact]
    public void Explicit_NONE_can_complete_placement_without_a_level_or_a_guessed_root_relation()
    {
        var run = Run(ValidatedStructureFactory.Create([Element("a", "s1", null)]));
        var audit = new PipelineExecutionAudit("test", 1, 1, 1, 1, [], [], [], [])
        { PlacementExecution = new("placement-accepted", 1, 1, 0, 1, 0, null,
            [new("s1", "O1", "model-out-of-hierarchy")]) };
        var dto = WebPipelineProjection.Project(run with { Execution = run.Execution! with
            { HeadingPipeline = run.Execution.HeadingPipeline! with { Audit = audit } } }, "pdf");
        Assert.Equal("completed", dto.Stages.Single(s => s.Id == "hierarchy-placement").Status);
        Assert.Contains("outside-tree=1", dto.Stages.Single(s => s.Id == "hierarchy-placement").Evidence);
        Assert.Null(Assert.Single(dto.Headings).Level);
        Assert.Empty(dto.Relations);
    }

    private static ValidatedStructuralElement Element(string id, string source, int? level) => new()
    {
        Id = id, Type = StructuralElementType.Heading, Role = ProposedRole.HeadingTopic, Text = "Duplicate",
        Level = level, Sources = [new(source, 7, new(0, 3))],
        Decision = new("model", "RequiresReview", "source-bound"),
        Validation = new(true, true, true, true, 1, true, true, true, null)
    };
    private static DocumentSourceUnit Unit(string id, int ordinal, string text) =>
        new(id, ordinal, text, new SourceAnchor { SourceType = "test", Page = 1 }, new(0, text.Length));
    private static DocumentAgentRunResult Run(ValidatedStructure graph, DocumentSourceCatalog? catalog = null)
    {
        var outline = new DocumentOutline { File = "test.docx", Headings = HeadingOutlineProjection.Project(graph) };
        var execution = new DocumentExtractionExecutionResult(new(new("doc", "test.docx", "docx", "private-path"),
            catalog ?? new DocumentSourceCatalog(graph.Elements.SelectMany(e => e.Sources).Select(s => s.SourceId)
                .Distinct().Select(s => Unit(s, 7, "Duplicate"))), graph, [], [], new("test-pipeline", "source", 0)), outline)
            { HeadingPipeline = new(graph, null, "test") };
        return new(Guid.NewGuid(), AgentRunOutcome.Completed, outline, 1, [])
        { Execution = execution, Skill = new("test", "test", "1", "digest", "path", new(), []),
            TaskResult = new(Guid.NewGuid(), "plan", "Completed", new(outline, "validated", []), [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) };
    }
}
