using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class TocStructuralFeatureDeriverTests
{
    [Fact]
    public void Derives_preceding_relation_by_source_identity()
    {
        var source = Source("toc.docx",
        [
            Paragraph("p0", "Contents heading"),
            Paragraph("p1", "Chapter 1 1"),
            Paragraph("p2", "Body"),
        ]);

        var features = new TocStructuralFeatureDeriver().Derive(source, new HashSet<string> { "p1" });

        Assert.True(features.PrecedesTableOfContents("p0"));
        Assert.False(features.PrecedesTableOfContents("p2"));
    }

    [Fact]
    public void Duplicate_text_does_not_share_toc_relation()
    {
        var source = Source("duplicate.docx",
        [
            Paragraph("p0", "Repeated"),
            Paragraph("p1", "Repeated"),
            Paragraph("p2", "Entry 1"),
        ]);

        var features = new TocStructuralFeatureDeriver().Derive(source, new HashSet<string> { "p2" });

        Assert.False(features.PrecedesTableOfContents("p0"));
        Assert.True(features.PrecedesTableOfContents("p1"));
    }

    [Fact]
    public void Deriver_does_not_depend_on_candidate_or_model_policy()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "DocxHeaderExtractor.Core",
            "Application", "Features", "TocStructuralFeatureDeriver.cs");
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("HeadingCandidatePolicy", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HeadingHeuristics", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelProposal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ValidatedHeading", text, StringComparison.Ordinal);
    }

    private static SourceDocument Source(string path, IReadOnlyList<SourceParagraph> paragraphs) => new()
    {
        DocumentId = path,
        FileName = path,
        SourcePath = path,
        SourceKind = "docx",
        Paragraphs = paragraphs,
    };

    private static SourceParagraph Paragraph(string id, string text) => new()
    {
        SourceId = id,
        SourceOrdinal = int.Parse(id[1..]),
        Text = text,
        Style = new SourceStyleFacts(),
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
