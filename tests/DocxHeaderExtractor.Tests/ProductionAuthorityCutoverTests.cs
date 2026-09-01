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

        var final = PdfFinalStructureProjection.Project("sha", audit.ValidatedStructures, audit.HierarchyFacts,
            PdfCanonicalGrounding.FromGroundedHeadings([heading]));
        var decisions = PdfOutputDecisionPolicy.Decide(final);
        var materialized = StructuralAuthorityMaterializer.Materialize(final, decisions);
        var authority = new StructuralAuthorityResult(materialized.Structure, audit, "pdf",
            materialized.EmittedElementIds);

        var without = AuthorityExtractionPipeline.ApplyStructuralQuarantine(authority, null);
        var with = AuthorityExtractionPipeline.ApplyStructuralQuarantine(authority, new HashSet<int> { 7 });

        Assert.Single(HeadingOutlineProjection.Project(without.Structure, without.EmittedElementIds));
        Assert.Empty(HeadingOutlineProjection.Project(with.Structure, with.EmittedElementIds));
        Assert.Empty(with.Structure.Relations);
    }

    [Fact]
    public void Structural_quarantine_removes_dangling_parent_relations_and_emissions()
    {
        var source = (string id, int ordinal) => new SourceReference(
            id, ordinal, new StructuralSpan(0, 1));
        var valid = new StructuralValidation(true, true, true, true, 1, true, true, true, null);
        var decision = new StructuralDecision("model", "RequiresReview", 1, "test");
        var parent = new ValidatedStructuralElement
        {
            Id = "parent",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [source("p0", 0)],
            Text = "P",
            Level = 1,
            Validation = valid,
            Decision = decision,
        };
        var child = parent with
        {
            Id = "child",
            Sources = [source("p1", 1)],
            Text = "C",
            Level = 2,
            ParentId = "parent",
        };
        var authority = new StructuralAuthorityResult(
            ValidatedStructure.FromElements([parent, child]),
            null,
            "test",
            new HashSet<string>(["parent", "child"], StringComparer.Ordinal));

        var filtered = AuthorityExtractionPipeline.ApplyStructuralQuarantine(
            authority, new HashSet<int> { 0 });

        var surviving = Assert.Single(filtered.Structure.Elements);
        Assert.Equal("child", surviving.Id);
        Assert.Null(surviving.ParentId);
        Assert.Empty(filtered.Structure.Relations);
        Assert.Equal(["child"], filtered.EmittedElementIds);
    }

    [Fact]
    public void Pdf_quarantine_keeps_final_structure_and_filters_only_product_decisions()
    {
        var final = PdfFinalStructureProjection.Project(
            "sha",
            [
                new PdfValidatedStructure("pdf:block:17", 1, null, "unresolved", "requires_review"),
                new PdfValidatedStructure("pdf:block:18", 2, "pdf:block:17", "resolved", "requires_review"),
            ],
            [
                Fact("pdf:block:17", 0, "First heading", 1),
                Fact("pdf:block:18", 1, "Second heading", 2),
            ],
            [
                new PdfCanonicalGrounding("pdf:block:17", 42, "@body[1]/p[42]", new DocxTextSpan(0, 13), "First heading"),
                new PdfCanonicalGrounding("pdf:block:18", 43, "@body[1]/p[43]", new DocxTextSpan(0, 14), "Second heading"),
            ]);
        var originalDecisions = PdfOutputDecisionPolicy.Decide(final);
        var materialized = StructuralAuthorityMaterializer.Materialize(final, originalDecisions);
        var authority = new StructuralAuthorityResult(materialized.Structure, null, "pdf", materialized.EmittedElementIds);
        var quarantined = AuthorityExtractionPipeline.ApplyStructuralQuarantine(
            authority, new HashSet<int> { 42 });

        var filteredDecisions = AuthorityExtractionPipeline.FilterPdfOutputDecisions(
            originalDecisions, quarantined.Structure);
        var product = PdfProductOutputSerializer.Serialize(final, filteredDecisions);
        var survivingFinalHeading = final.Headings.Single(heading => heading.SourceAnchor!.ParagraphIndex == 43);
        var expected = PdfProductOutputSerializer.Serialize(
            final, originalDecisions.Where(decision => decision.HeadingId == survivingFinalHeading.Id).ToArray());
        var survivingProductHeading = Assert.Single(product.Headings);

        Assert.Equal(1, product.Headings.Count);
        Assert.Equal(survivingFinalHeading.SourceAnchor!.ParagraphIndex, survivingProductHeading.ParagraphIndex);
        Assert.Equal(survivingFinalHeading.SourceAnchor.StableId, survivingProductHeading.StableId);
        Assert.Equal(survivingFinalHeading.SourceAnchor.Span, survivingProductHeading.Span);
        Assert.Equal(survivingFinalHeading.Text, survivingProductHeading.Text);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(product));
        Assert.Empty(quarantined.Structure.Relations);
        Assert.NotNull(quarantined.EmittedElementIds);
        Assert.Single(quarantined.EmittedElementIds!, id => id.Contains("p[43]", StringComparison.Ordinal));
    }

    private static PdfHierarchyFactAudit Fact(string id, int order, string text, int level) =>
        new(id, order, 1, "document_body", "semantic", null, null, false, null,
            null, null, level, "relationship_unresolved", [])
        {
            FactId = $"fact:{id}",
            SourceBlockText = text,
            HeadingSpan = new TextOffsetSpan(0, text.Length),
            HeadingText = text,
        };
}
