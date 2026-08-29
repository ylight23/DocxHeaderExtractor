using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class DocumentFeatureDeriverTests
{
    [Fact]
    public void Derives_body_font_using_existing_character_weight_rule()
    {
        var source = SourceDocument("features.docx",
        [
            Paragraph("p1", "short", 14),
            Paragraph("p2", new string('x', 20), 11),
            Paragraph("p3", new string('y', 20), 11),
        ]);

        var features = new DocumentFeatureDeriver().Derive(source);

        Assert.Equal(11, features.BodyFontSizePt);
        Assert.Equal(5, features.FontSizeCharacterWeights[14]);
        Assert.Equal(40, features.FontSizeCharacterWeights[11]);
    }

    [Fact]
    public void Empty_source_has_no_body_font_feature()
    {
        var features = new DocumentFeatureDeriver().Derive(SourceDocument("empty.docx", []));

        Assert.Null(features.BodyFontSizePt);
        Assert.Empty(features.FontSizeCharacterWeights);
    }

    [Fact]
    public void Derivation_does_not_mutate_source()
    {
        var source = SourceDocument("immutable.docx", [Paragraph("p1", "heading", 12)]);
        var before = source.Paragraphs[0];

        _ = new DocumentFeatureDeriver().Derive(source);

        Assert.Equal(before, source.Paragraphs[0]);
    }

    [Fact]
    public void Derives_corruption_from_source_text_without_policy_fields()
    {
        var source = SourceDocument("corrupt.docx",
            [Paragraph("p1", "HHììnnhh 11.1 22", 11), Paragraph("p2", "Normal source", 11)]);

        var features = new DocumentFeatureDeriver().Derive(source);

        Assert.Contains("p1", features.CorruptSourceIds);
        Assert.DoesNotContain("p2", features.CorruptSourceIds);
    }

    [Fact]
    public void Feature_deriver_has_no_policy_dependency()
    {
        var sourcePath = Path.Combine(FindRepositoryRoot(),
            "src", "DocxHeaderExtractor.Core", "Application", "Features", "DocumentFeatureDeriver.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("HeadingHeuristics", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelProposal", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ValidatedHeading", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthorityRoutePolicy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfFirstValidatedFallback", source, StringComparison.Ordinal);
    }

    private static SourceDocument SourceDocument(string path, IReadOnlyList<SourceParagraph> paragraphs) => new()
    {
        DocumentId = path,
        FileName = path,
        SourcePath = path,
        SourceKind = "docx",
        Paragraphs = paragraphs,
    };

    private static SourceParagraph Paragraph(string id, string text, double size) => new()
    {
        SourceId = id,
        SourceOrdinal = int.Parse(id[1..]),
        Text = text,
        Style = new SourceStyleFacts { FontSizePt = size },
        Numbering = new SourceNumberingFacts(),
        Layout = new SourceLayoutFacts(),
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
