using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class WritebackMappingBoundaryTests
{
    [Fact]
    public void Duplicate_text_keeps_distinct_source_identity_and_locator()
    {
        var source = new SourceDocument
        {
            DocumentId = "doc-sha",
            FileName = "sample.docx",
            SourcePath = "sample.docx",
            SourceKind = "docx",
            Paragraphs = [Paragraph("p-1", 4), Paragraph("p-2", 9)],
        };

        var mappings = WritebackMappingSet.FromSourceDocument(source);

        Assert.Equal(2, mappings.Count);
        Assert.NotEqual(mappings["p-1"].Identity, mappings["p-2"].Identity);
        Assert.NotEqual(mappings["p-1"].Locator.ParagraphIndex, mappings["p-2"].Locator.ParagraphIndex);
        Assert.All(mappings.Values, mapping => Assert.Equal("same heading", mapping.Locator.SourceText));
    }

    [Fact]
    public void Mapping_contains_no_policy_or_demotion_state()
    {
        var properties = typeof(WritebackMapping).GetProperties().Select(property => property.Name).ToArray();

        Assert.Equal(["Identity", "Locator"], properties);
        Assert.DoesNotContain(properties, property => property is "Role" or "Score" or "IsCandidate" or "GuessedLevel");
    }

    [Fact]
    public void Source_id_is_the_only_mapping_key()
    {
        var source = new SourceDocument
        {
            DocumentId = "doc-sha",
            FileName = "sample.docx",
            SourcePath = "sample.docx",
            SourceKind = "docx",
            Paragraphs = [Paragraph("same-id", 1)],
        };

        var mapping = Assert.Single(WritebackMappingSet.FromSourceDocument(source));

        Assert.Equal("same-id", mapping.Key);
        Assert.Equal("same-id", mapping.Value.Identity.SourceId);
    }

    private static SourceParagraph Paragraph(string id, int ordinal) => new()
    {
        SourceId = id,
        SourceOrdinal = ordinal,
        Text = "same heading",
        Style = new SourceStyleFacts(),
        Numbering = new SourceNumberingFacts(),
        Layout = new SourceLayoutFacts(),
    };
}
