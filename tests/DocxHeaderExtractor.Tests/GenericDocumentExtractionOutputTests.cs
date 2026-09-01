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
    public void Parser_owned_structural_sources_can_join_a_catalog_without_inventing_text()
    {
        var source = BuildSource();
        var baseCatalog = DocumentSourceCatalogBuilder.FromSourceDocument(source);
        var element = Element("pdf-element-1", "Figure 2", 20, StructuralElementType.Figure,
            ProposedRole.StructuralContainer, "pdf-block-1");
        var merged = DocumentSourceCatalogBuilder.MergeStructuralSources(
            baseCatalog, ValidatedStructure.FromElements([element]));

        var unit = Assert.Single(merged.Units, item => item.SourceId == "pdf-block-1");
        Assert.Equal("Figure 2", unit.Text);
        Assert.Equal(20, unit.SourceOrdinal);
        Assert.Equal("parser-fact", unit.SourceAnchor.SourceType);
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
