using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class DocumentConsumerProjectionTests
{
    [Fact]
    public void Ie_projection_preserves_generic_structure()
    {
        var result = BuildResult();

        var ie = Assert.Single(IEContextProjection.Project(result));

        Assert.Equal(result.Chunks[0].Text, ie.SourceText);
        Assert.Equal("doc-1", ie.DocumentId);
        Assert.Equal(result.Chunks[0].Id, ie.ChunkId);
        Assert.Equal(result.Chunks[0].SourceIds, ie.SourceUnits.Select(unit => unit.SourceId));
        Assert.Equal(result.Chunks[0].StructuralElementIds, ie.StructuralElementIds);
        Assert.Contains(ie.FigureTableContext, item => item.Type == StructuralElementType.Figure);
        Assert.Contains(ie.FigureTableContext, item => item.Type == StructuralElementType.Table);
        Assert.Contains(ie.FigureTableContext, item => item.Type == StructuralElementType.Caption);
    }

    private static DocumentExtractionResult BuildResult()
    {
        var texts = new[]
        {
            "Architecture", "One source of truth", "Figure 1", "Figure 1 title",
            "Figure 1 caption", "Table 1", "Table 1 title",
        };
        var catalog = new DocumentSourceCatalog(texts.Select((text, index) => new DocumentSourceUnit(
            $"p{index}", index, text,
            new SourceAnchor { SourceType = "docx", ParagraphId = $"p{index}", ParagraphIndex = index },
            new StructuralSpan(0, text.Length))));
        var elements = new[]
        {
            Element("h1", "Architecture", 0, StructuralElementType.Heading, ProposedRole.HeadingTopic),
            Element("list", "One source of truth", 1, StructuralElementType.ListItem, ProposedRole.ListItemTopic),
            Element("figure", "Figure 1", 2, StructuralElementType.Figure, ProposedRole.StructuralContainer),
            Element("figure-title", "Figure 1 title", 3, StructuralElementType.FigureTitle, ProposedRole.FigureTitle),
            Element("caption", "Figure 1 caption", 4, StructuralElementType.Caption, ProposedRole.Caption),
            Element("table", "Table 1", 5, StructuralElementType.Table, ProposedRole.StructuralContainer),
            Element("table-title", "Table 1 title", 6, StructuralElementType.TableTitle, ProposedRole.Caption),
        };
        var relations = new[]
        {
            new StructuralRelationProposal("h1", "list", StructuralRelationType.ParentChild),
            new StructuralRelationProposal("h1", "figure", StructuralRelationType.ParentChild),
            new StructuralRelationProposal("figure-title", "figure", StructuralRelationType.Labels),
            new StructuralRelationProposal("caption", "figure", StructuralRelationType.CaptionOf),
            new StructuralRelationProposal("table-title", "table", StructuralRelationType.Labels),
        };
        var structure = ValidatedStructure.FromElements(elements, relations);
        var section = new StructuralSection(
            "section:h1", "h1", null, ["h1"],
            catalog.Units.Select(unit => unit.SourceId).ToArray(),
            structure.Elements.Select(element => element.Id).ToArray());
        var text = string.Join('\n', catalog.Units.Select(unit => unit.Text));
        var chunk = new DocumentChunk(
            "section:h1:chunk:1", section.Id, section.SourceIds,
            section.StructuralElementIds, text, Math.Max(1, text.Length / 2));
        return new DocumentExtractionResult(
            new DocumentIdentity("doc-1", "doc.docx", "docx", "doc.docx"),
            catalog, structure, [section], [chunk],
            new DocumentExtractionProvenance("docx-authority-v1", "docx-source-document", 0));
    }

    private static ValidatedStructuralElement Element(
        string id, string text, int ordinal, StructuralElementType type, ProposedRole role) => new()
    {
        Id = id,
        Type = type,
        Role = role,
        Sources = [new SourceReference($"p{ordinal}", ordinal, new StructuralSpan(0, text.Length))],
        Text = text,
        Level = type == StructuralElementType.Heading ? 1 : null,
        Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
        Decision = new StructuralDecision("test", "accepted", 1, "parser-facts"),
    };
}
