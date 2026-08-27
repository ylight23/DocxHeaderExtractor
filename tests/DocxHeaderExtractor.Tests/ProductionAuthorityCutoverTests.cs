using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

public sealed class ProductionAuthorityCutoverTests
{
    [Fact]
    public void Legacy_flag_does_not_select_the_normal_orchestrator()
    {
        var options = new PipelineOptions { PdfFirstValidatedFallback = false };
        using var tool = new DocxHeaderExtractor.AgentHarness.PipelineDocumentExtractionTool(options);

        var pipeline = typeof(DocxHeaderExtractor.AgentHarness.PipelineDocumentExtractionTool)
            .GetField("_pipeline", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(tool);
        Assert.IsType<AuthorityExtractionPipeline>(pipeline);
    }

    [Fact]
    public void Final_structure_source_text_is_additive_under_schema_v2()
    {
        var heading = new PdfProductHeading("id", 1, "stable", new DocxTextSpan(7, 14),
            "HEADING", "Heading", 1, null, true, [], "prefix HEADING body");
        var json = System.Text.Json.JsonSerializer.Serialize(heading);
        Assert.Contains("sourceText", json, StringComparison.Ordinal);

        var old = System.Text.Json.JsonSerializer.Deserialize<PdfProductHeading>(
            "{\"id\":\"id\",\"paragraphIndex\":1,\"stableId\":null,\"span\":{\"start\":0,\"end\":4},\"text\":\"Title\",\"role\":\"Heading\",\"level\":1,\"parentId\":null,\"requiresReview\":true,\"reasons\":[]}");
        Assert.NotNull(old);
        Assert.Null(old!.SourceText);
    }

    [Fact]
    public void Old_final_structure_schema_v2_without_source_text_round_trips()
    {
        var fact = new PdfHierarchyFactAudit("b1", 0, 1, "document_body", "semantic", null, null,
            false, null, null, null, null, "relationship_unresolved", ["validated_source_span"])
        {
            FactId = "p1:b1:s7-14", SourceBlockText = "prefix HEADING body",
            HeadingSpan = new TextOffsetSpan(7, 14), HeadingText = "HEADING",
        };
        var structure = new PdfValidatedStructure("b1", 1, null, "unresolved", "requires_review");
        var grounding = new PdfCanonicalGrounding("b1", 4, "stable", new DocxTextSpan(7, 14), "prefix HEADING body");
        var current = PdfFinalStructureProjection.Project("sha", [structure], [fact], [grounding]);
        var oldNode = JsonNode.Parse(JsonSerializer.Serialize(current))!.AsObject();
        oldNode["headings"]![0]!.AsObject().Remove("sourceText");

        var old = oldNode.Deserialize<PdfFinalStructure>();

        Assert.NotNull(old);
        Assert.Equal(2, old!.SchemaVersion);
        var oldHeading = Assert.Single(old.Headings);
        Assert.Equal(current.Headings[0].Id, oldHeading.Id);
        Assert.Equal(current.Headings[0].Text, oldHeading.Text);
        Assert.Equal(current.Headings[0].Level, oldHeading.Level);
        Assert.Equal(current.Headings[0].SourceAnchor, oldHeading.SourceAnchor);
        Assert.Equal(new DocxTextSpan(7, 14), oldHeading.SourceAnchor!.Span);

        var currentJson = JsonNode.Parse(JsonSerializer.Serialize(current))!.AsObject();
        Assert.Equal(2, currentJson["schemaVersion"]!.GetValue<int>());
        Assert.Equal("prefix HEADING body", currentJson["headings"]![0]! ["sourceText"]!.GetValue<string>());
    }

    [Fact]
    public void Provenance_records_only_executed_lanes_and_externality()
    {
        var noLanes = Provenance(null, false);
        Assert.DoesNotContain(noLanes.Passes, pass => pass.Name is "semantic-role" or "heading-span" or "semantic-hierarchy");

        var audit = Audit() with
        {
            SemanticLane = new RouteLaneExecutionAudit("complete", 3, 3, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("not_run", 0, 0, 0, 0),
            HierarchyProposals = [],
        };
        var local = Provenance(audit, false);
        Assert.Contains(local.Passes, pass => pass.Name == "semantic-role" && !pass.SentDataExternally);
        Assert.DoesNotContain(local.Passes, pass => pass.Name == "heading-span");

        var remote = Provenance(audit, true);
        Assert.Contains(remote.Passes, pass => pass.Name == "semantic-role" && pass.SentDataExternally);
    }

    private static OutlineRunProvenance Provenance(RouteExecutionAudit? audit, bool external) =>
        (OutlineRunProvenance)typeof(AuthorityExtractionPipeline)
            .GetMethod("BuildProvenance", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [audit, external])!;

    private static RouteExecutionAudit Audit() =>
        new("authority", 3, 3, 0, 0, [], [], [], [], [], [], []);

    [Fact]
    public async Task NoLlmBuiltInStylesBecomeGroundedProductHeadings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-authority-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
            var outline = await pipeline.RunAsync(path);
            Assert.Equal(4, outline.Headings.Count);
            Assert.NotNull(outline.ProductOutput);
            Assert.Equal(4, outline.ProductOutput!.Headings.Count);
            Assert.DoesNotContain(outline.Headings, heading => heading.Text.StartsWith("2.1 ", StringComparison.Ordinal));
            Assert.All(outline.Provenance.Passes, pass => Assert.False(pass.SentDataExternally));
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public async Task Quarantine_is_applied_before_deterministic_proposal_and_output()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-authority-quarantine-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
            var outline = await pipeline.RunAsync(path, new HashSet<int> { 0 });
            Assert.Equal(3, outline.Headings.Count);
            Assert.DoesNotContain(outline.Headings, heading => heading.Index == 0);
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public void Pdf_backed_quarantine_removes_grounded_occurrence_before_product_projection()
    {
        var heading = new HeadingRecord
        {
            Index = 7, StableId = "@body[1]/p[7]", SourceId = "pdf-block-7", Text = "HEADING",
            OriginalText = "prefix HEADING body", HeadingSpan = new TextOffsetSpan(7, 14), Level = 1,
        };
        var fact = new PdfHierarchyFactAudit("pdf-block-7", 0, 1, "document_body", "financial",
            null, null, false, null, null, null, 1, "relationship_unresolved", ["validated_source_span"])
        {
            FactId = "p1:pdf-block-7:s7-14", SourceBlockText = "HEADING", HeadingSpan = new TextOffsetSpan(7, 14),
            HeadingText = "HEADING",
        };
        var structure = new PdfValidatedStructure("pdf-block-7", 1, null, "unresolved", "requires_review");
        var audit = new RouteExecutionAudit("pdf", 1, 1, 1, 1, [], [], [], [], ["pdf-block-7"], [], ["pdf-block-7"])
        {
            ValidatedStructures = [structure], HierarchyFacts = [fact],
        };

        IReadOnlyList<HeadingRecord> remaining;
        var without = AuthorityExtractionPipeline.ApplyQuarantine(audit, [heading], null, out remaining)!;
        var with = AuthorityExtractionPipeline.ApplyQuarantine(audit, [heading], new HashSet<int> { 7 }, out remaining)!;
        var output = (RouteExecutionAudit source) =>
            PdfProductOutputSerializer.Serialize(
                PdfFinalStructureProjection.Project("sha", source.ValidatedStructures, source.HierarchyFacts,
                    PdfCanonicalGrounding.FromGroundedHeadings(source == without ? [heading] : remaining)),
                PdfOutputDecisionPolicy.Decide(PdfFinalStructureProjection.Project("sha", source.ValidatedStructures,
                    source.HierarchyFacts, PdfCanonicalGrounding.FromGroundedHeadings(source == without ? [heading] : remaining))));

        Assert.Single(output(without).Headings); // The unquarantined grounded occurrence is emitted.
        Assert.Empty(output(with).Headings); // The authority audit is filtered before ProductOutput.
    }
}
