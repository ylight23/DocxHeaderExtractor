using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using System.Reflection;

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
