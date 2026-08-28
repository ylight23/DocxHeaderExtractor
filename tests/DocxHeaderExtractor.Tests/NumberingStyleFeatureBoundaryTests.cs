using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class NumberingStyleFeatureBoundaryTests
{
    [Theory]
    [InlineData("Heading 1", "Heading1", 1)]
    [InlineData("Title", "Title", 1)]
    [InlineData("Subtitle", "Subtitle", 2)]
    [InlineData("Custom Heading", "CustomHeading", null)]
    public void Built_in_style_identity_is_pure_style_metadata(
        string styleName, string styleId, int? expectedLevel)
    {
        Assert.Equal(expectedLevel,
            BuiltInHeadingStyleIdentity.LevelFromResolvedStyle(styleName, styleId));
    }

    [Fact]
    public void Features_preserve_source_identity_and_numbering_style_values()
    {
        var source = new SourceDocument
        {
            DocumentId = "doc-sha",
            FileName = "sample.docx",
            SourcePath = "sample.docx",
            SourceKind = "docx",
            Paragraphs = [new SourceParagraph
            {
                SourceId = "p-1",
                SourceOrdinal = 3,
                Text = "1. Heading",
                Style = new SourceStyleFacts { StyleId = "Heading1", StyleName = "Heading 1", BuiltInHeadingStyleLevel = 1, OutlineLevel = 0, Bold = true, FontSizePt = 14 },
                Numbering = new SourceNumberingFacts { NumberingId = 4, NumberingLevel = 1, NumberLabel = "1.", NumberingFormat = "decimal" },
                Layout = new SourceLayoutFacts(),
            }]
        };

        var features = NumberingStyleFeatures.FromSourceDocument(source);

        var numbering = Assert.Single(features.Numbering);
        var style = Assert.Single(features.Styles);
        Assert.Equal("p-1", numbering.SourceId);
        Assert.Equal(4, numbering.NumberingId);
        Assert.Equal("1.", numbering.NumberLabel);
        Assert.Equal("p-1", style.SourceId);
        Assert.Equal("Heading1", style.StyleId);
        Assert.Equal(1, style.BuiltInHeadingStyleLevel);
        Assert.True(style.Bold);
    }

    [Fact]
    public void Features_do_not_expose_policy_or_demotion_fields()
    {
        var fields = typeof(ParagraphNumberingFeatures).GetProperties()
            .Concat(typeof(ParagraphStyleFeatures).GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(fields, field => field is "Role" or "Score" or "IsCandidate" or "GuessedLevel");
        Assert.DoesNotContain(fields, field => field.Contains("Demot", StringComparison.OrdinalIgnoreCase));
    }
}
