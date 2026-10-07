using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Source;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingDocumentExtractionOutputTests
{
    [Fact]
    public async Task Authority_pipeline_without_a_model_exposes_the_source_catalog_and_claims_no_structure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-generic-output-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            using var pipeline = new DocxExtractionPipeline(new PipelineOptions { DisableLlm = true });
            var generic = (await pipeline.RunDocumentExecutionAsync(path)).Result;
            var legacy = await pipeline.RunAsync(path);

            Assert.NotEmpty(generic.SourceCatalog.Units);
            // No model, no semantic claims: the harness does not declare headings on its own.
            Assert.Empty(generic.Structure.Elements);
            Assert.Equal(
                legacy.Headings.Select(heading => (heading.Index, heading.Text, heading.Level)),
                HeadingOutlineProjection.Project(generic.Structure)
                    .Select(heading => (heading.Index, heading.Text, heading.Level)));
            Assert.Equal(0, generic.Provenance.ProviderCalls);
        }
        finally
        {
            OfficeDocumentConverter.TryDelete(path);
        }
    }

    [Fact]
    public void Heading_output_preserves_hierarchy_sections_and_unclaimed_body_chunks()
    {
        var source = BuildSource();
        var elements = new[]
        {
            Element("h1", "Architecture", 0),
            Element("h2", "Runtime", 1),
        };
        var relations = new[]
        {
            new StructuralRelationProposal("h1", "h2", StructuralRelationType.ParentChild),
        };
        var structure = ValidatedStructure.FromElements(elements, relations);
        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(source);
        var sections = StructuralSectionProjection.Project(structure, catalog);
        var chunks = SectionChunkProjection.Project(
            sections, catalog, structure, new DocumentChunkingPolicy(1000));
        var result = new DocumentExtractionResult(
            new DocumentIdentity("doc", "doc.docx", "docx", "doc.docx"),
            catalog,
            structure,
            sections,
            chunks,
            new DocumentExtractionProvenance("docx-authority-v1", "docx-source-document", 0));

        Assert.NotEmpty(result.Sections);
        Assert.NotEmpty(result.Chunks);
        Assert.Equal(2, result.Structure.Elements.Count);
        Assert.All(result.Structure.Elements, element =>
        {
            Assert.Equal(StructuralElementType.Heading, element.Type);
            Assert.Equal(ProposedRole.HeadingTopic, element.Role);
        });
        var parent = Assert.Single(result.Structure.Relations);
        Assert.Equal(new StructuralRelation("h1", "h2", StructuralRelationType.ParentChild), parent);
        Assert.Equal("h1", result.Structure.Elements.Single(element => element.Id == "h2").ParentId);

        var firstChunk = result.Chunks.First();
        Assert.Contains("Architecture", firstChunk.Text);
        Assert.Contains("Architecture overview", firstChunk.Text);
        Assert.Equal(new[] { "h1", "h2" }, firstChunk.StructuralElementIds);
        Assert.Contains("p4", firstChunk.SourceIds); // body content is not a structural element
        Assert.Equal(firstChunk.Text, string.Join('\n', firstChunk.SourceIds.Select(id =>
            catalog.Units.Single(unit => unit.SourceId == id).Text)));
    }

    [Fact]
    public void Parser_owned_facts_build_catalog_without_inventing_text()
    {
        const string raw = "prefix Figure 3 caption suffix";
        var catalog = DocumentSourceCatalogBuilder.FromSourceFacts([
            new SourceFacts
            {
                SourceId = "pdf-block-1",
                RawText = raw,
                RawSpan = new SourceTextSpan(7, 23),
                Source = new SourceAnchor
                {
                    SourceType = "pdf",
                    RenderBlockId = "pdf-block-1",
                    ParagraphIndex = 20,
                },
            },
        ]);

        var unit = Assert.Single(catalog.Units);
        Assert.Equal(raw, unit.Text);
        Assert.Equal(new StructuralSpan(0, raw.Length), unit.SourceSpan);

        var sourceOccurrence = new StructuralSourceOccurrence
        {
            SourceOccurrenceId = "heading-1",
            ObservedSourceFacts =
            [
                new SourceFacts
                {
                    SourceId = "pdf-block-1",
                    RawText = raw,
                    RawSpan = new SourceTextSpan(0, raw.Length),
                    Source = new SourceAnchor { SourceType = "pdf", ParagraphIndex = 20 },
                },
            ],
        };
        var element = StructuralProposalValidator.Materialize(
            sourceOccurrence,
            new StructuralProposal
            {
                SourceOccurrenceId = "heading-1",
                Type = StructuralElementType.Heading,
                Role = ProposedRole.HeadingTopic,
                ProposedSources = [new ProposedSourceReference("pdf-block-1", new StructuralSpan(7, 23))],
            },
            "structural:heading:1",
            new StructuralDecision(StructuralDecisionOrigin.Model, nameof(HeadingDecisionStatus.RequiresReview), "parser-fact"));

        Assert.NotNull(element);
        Assert.Equal(new StructuralSpan(7, 23), Assert.Single(element!.Sources).Span);
        Assert.Equal("Figure 3 caption", element.Text);
    }

    [Fact]
    public void Multi_source_structural_element_keeps_each_parser_span()
    {
        var facts = new[]
        {
            new SourceFacts
            {
                SourceId = "pdf-a", RawText = "first", RawSpan = new SourceTextSpan(0, 5),
                Source = new SourceAnchor { SourceType = "pdf", ParagraphIndex = 1 },
            },
            new SourceFacts
            {
                SourceId = "pdf-b", RawText = "second", RawSpan = new SourceTextSpan(0, 6),
                Source = new SourceAnchor { SourceType = "pdf", ParagraphIndex = 2 },
            },
        };
        var sourceOccurrence = new StructuralSourceOccurrence { SourceOccurrenceId = "multi", ObservedSourceFacts = facts };
        var element = StructuralProposalValidator.Materialize(
            sourceOccurrence,
            new StructuralProposal
            {
                SourceOccurrenceId = "multi",
                Type = StructuralElementType.Heading,
                Role = ProposedRole.HeadingTopic,
                ProposedSources =
                [
                    new ProposedSourceReference("pdf-a", new StructuralSpan(0, 5)),
                    new ProposedSourceReference("pdf-b", new StructuralSpan(0, 6)),
                ],
            },
            "structural:multi",
            new StructuralDecision(StructuralDecisionOrigin.Model, nameof(HeadingDecisionStatus.RequiresReview), "parser-facts"));

        Assert.NotNull(element);
        Assert.Equal(new[] { "pdf-a", "pdf-b" }, element!.Sources.Select(source => source.SourceId));
        Assert.Equal(new[] { 1, 2 }, element.Sources.Select(source => source.SourceOrdinal));
    }

    private static SourceDocument BuildSource() => new()
    {
        DocumentId = "doc",
        FileName = "doc.docx",
        SourcePath = "doc.docx",
        SourceKind = "docx",
        Paragraphs = Enumerable.Range(0, 6).Select(index => new SourceParagraph
        {
            SourceId = $"p{index}",
            SourceOrdinal = index,
            Text = index switch
            {
                0 => "Architecture",
                1 => "Runtime",
                2 => "One source of truth",
                3 => "Figure 1",
                4 => "Architecture overview",
                _ => "Table 1",
            },
            Style = new SourceStyleFacts(),
            Numbering = new SourceNumberingFacts(),
        }).ToArray(),
    };

    private static ValidatedStructuralElement Element(
        string id,
        string text,
        int ordinal,
        string? sourceId = null)
    {
        sourceId ??= $"p{ordinal}";
        var facts = new SourceFacts
        {
            SourceId = sourceId,
            RawText = text,
            Source = new SourceAnchor
            {
                SourceType = "test",
                ParagraphId = sourceId,
                ParagraphIndex = ordinal,
            },
            RawSpan = new SourceTextSpan(0, text.Length),
        };
        var sourceOccurrence = new StructuralSourceOccurrence
        {
            SourceOccurrenceId = id,
            ObservedSourceFacts = [facts],
        };
        return StructuralProposalValidator.Materialize(
            sourceOccurrence,
            new StructuralProposal
            {
                SourceOccurrenceId = id,
                Type = StructuralElementType.Heading,
                Role = ProposedRole.HeadingTopic,
                ProposedSources = [new ProposedSourceReference(facts.SourceId, new StructuralSpan(0, text.Length))],
            },
            id,
            new StructuralDecision(StructuralDecisionOrigin.Model, nameof(HeadingDecisionStatus.RequiresReview), "test-source"))!;
    }
}
