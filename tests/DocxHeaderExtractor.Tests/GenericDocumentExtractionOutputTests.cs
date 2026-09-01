using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class GenericDocumentExtractionOutputTests
{
    [Fact]
    public async Task Authority_pipeline_exposes_generic_result_before_heading_projection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-generic-output-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
            var generic = await pipeline.RunDocumentAsync(path);
            var legacy = await pipeline.RunAsync(path);

            Assert.NotEmpty(generic.SourceCatalog.Units);
            Assert.NotEmpty(generic.Structure.Elements);
            Assert.NotEmpty(generic.Sections);
            Assert.NotEmpty(generic.Chunks);
            Assert.Equal(
                legacy.Headings.Select(heading => (heading.Index, heading.Text, heading.Level)),
                HeadingOutlineProjection.Project(generic.Structure)
                    .Select(heading => (heading.Index, heading.Text, heading.Level)));
            Assert.Equal(0, generic.Provenance.ProviderCalls);
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public void Generic_output_preserves_structure_relations_sections_and_source_backed_chunks()
    {
        var source = BuildSource();
        var elements = new[]
        {
            Element("h1", "Architecture", 0, StructuralElementType.Heading, ProposedRole.HeadingTopic),
            Element("h2", "Runtime", 1, StructuralElementType.Heading, ProposedRole.HeadingTopic),
            Element("list", "One source of truth", 2, StructuralElementType.ListItem, ProposedRole.ListItemTopic),
            Element("figure", "Figure 1", 3, StructuralElementType.Figure, ProposedRole.StructuralContainer),
            Element("figure-title", "Figure 1", 3, StructuralElementType.FigureTitle, ProposedRole.FigureTitle),
            Element("caption", "Architecture overview", 4, StructuralElementType.Caption, ProposedRole.Caption),
            Element("table", "Table 1", 5, StructuralElementType.Table, ProposedRole.StructuralContainer),
            Element("table-title", "Table 1", 5, StructuralElementType.TableTitle, ProposedRole.Caption),
        };
        var relations = new[]
        {
            new StructuralRelationProposal("h1", "h2", StructuralRelationType.ParentChild),
            new StructuralRelationProposal("h2", "list", StructuralRelationType.ParentChild),
            new StructuralRelationProposal("h2", "figure", StructuralRelationType.ParentChild),
            new StructuralRelationProposal("figure-title", "figure", StructuralRelationType.Labels),
            new StructuralRelationProposal("caption", "figure", StructuralRelationType.CaptionOf),
            new StructuralRelationProposal("table-title", "table", StructuralRelationType.Labels),
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
        Assert.Contains(result.Structure.Elements, element => element.Type == StructuralElementType.ListItem);
        Assert.Contains(result.Structure.Elements, element => element.Type == StructuralElementType.Figure);
        Assert.Contains(result.Structure.Elements, element => element.Type == StructuralElementType.FigureTitle);
        Assert.Contains(result.Structure.Elements, element => element.Type == StructuralElementType.Caption);
        Assert.Contains(result.Structure.Elements, element => element.Type == StructuralElementType.Table);
        Assert.Contains(result.Structure.Elements, element => element.Type == StructuralElementType.TableTitle);
        Assert.Contains(result.Structure.Relations, relation =>
            relation.Type == StructuralRelationType.CaptionOf && relation.ToId == "figure");
        Assert.Contains(result.Structure.Relations, relation =>
            relation.Type == StructuralRelationType.Labels && relation.ToId == "table");

        var firstChunk = result.Chunks.First();
        Assert.Contains("Architecture", firstChunk.Text);
        Assert.Contains("Architecture overview", firstChunk.Text);
        Assert.Contains("caption", firstChunk.StructuralElementIds);
        Assert.Contains("figure", firstChunk.StructuralElementIds);
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

        var candidate = new StructuralCandidate
        {
            CandidateId = "caption-1",
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
            candidate,
            new StructuralProposal
            {
                CandidateId = "caption-1",
                Type = StructuralElementType.Caption,
                Role = ProposedRole.Caption,
                ProposedSources = [new ProposedSourceReference("pdf-block-1", new StructuralSpan(7, 23))],
            },
            "structural:caption:1",
            new StructuralDecision("test", "accepted", 1, "parser-fact"));

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
        var candidate = new StructuralCandidate { CandidateId = "multi", ObservedSourceFacts = facts };
        var element = StructuralProposalValidator.Materialize(
            candidate,
            new StructuralProposal
            {
                CandidateId = "multi",
                Type = StructuralElementType.Caption,
                Role = ProposedRole.Caption,
                ProposedSources =
                [
                    new ProposedSourceReference("pdf-a", new StructuralSpan(0, 5)),
                    new ProposedSourceReference("pdf-b", new StructuralSpan(0, 6)),
                ],
            },
            "structural:multi",
            new StructuralDecision("test", "accepted", 1, "parser-facts"));

        Assert.NotNull(element);
        Assert.Equal(new[] { "pdf-a", "pdf-b" }, element!.Sources.Select(source => source.SourceId));
        Assert.Equal(new[] { 1, 2 }, element.Sources.Select(source => source.SourceOrdinal));
    }

    [Fact]
    public void Pdf_materializer_joins_parser_catalog_and_preserves_narrow_structural_span()
    {
        const string raw = "prefix Figure 3 caption suffix";
        var fact = new PdfHierarchyFactAudit(
            "b1", 0, 1, "document_body", "document_body", null, null, false, null,
            null, null, 1, "relationship_unresolved", [])
        {
            FactId = "p1:b1:s7-23",
            SourceBlockText = raw,
            HeadingSpan = new TextOffsetSpan(7, 23),
            HeadingText = "Figure 3 caption",
        };
        var final = PdfFinalStructureProjection.Project(
            "sha",
            [new PdfValidatedStructure("b1", 1, null, "unresolved", "requires_review")],
            [fact],
            [new PdfCanonicalGrounding(
                "b1", 0, "docx-p1", new DocxTextSpan(7, 23), raw)]);
        var catalog = DocumentSourceCatalogBuilder.FromSourceFacts([
            new SourceFacts
            {
                SourceId = "b1",
                RawText = raw,
                RawSpan = new SourceTextSpan(0, raw.Length),
                Source = new SourceAnchor { SourceType = "pdf", ParagraphIndex = 0, RenderBlockId = "b1" },
            },
        ]);

        var materialized = StructuralAuthorityMaterializer.Materialize(
            final, PdfOutputDecisionPolicy.Decide(final), catalog);
        var source = Assert.Single(materialized.Structure.Elements).Sources.Single();

        Assert.Equal("b1", source.SourceId);
        Assert.Equal(0, source.SourceOrdinal);
        Assert.Equal(new StructuralSpan(7, 23), source.Span);
        Assert.Equal(raw, catalog.Units.Single().Text);
        Assert.Equal(new StructuralSpan(0, raw.Length), catalog.Units.Single().SourceSpan);
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
            Layout = new SourceLayoutFacts(),
        }).ToArray(),
    };

    private static ValidatedStructuralElement Element(
        string id,
        string text,
        int ordinal,
        StructuralElementType type,
        ProposedRole role,
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
        var candidate = new StructuralCandidate
        {
            CandidateId = id,
            ObservedSourceFacts = [facts],
        };
        return StructuralProposalValidator.Materialize(
            candidate,
            new StructuralProposal
            {
                CandidateId = id,
                Type = type,
                Role = role,
                ProposedSources = [new ProposedSourceReference(facts.SourceId, new StructuralSpan(0, text.Length))],
            },
            id,
            new StructuralDecision("test", "accepted", 1, "test-source"))!;
    }
}
