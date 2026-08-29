using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public class BookTocDictionaryOutlineTests
{
    [Fact]
    public void AdvancedLinearAlgebraUsesTocTitlesAndBodyAnchors()
    {
        var docx = Path.Combine(
            RepositoryRoot(), "todo10_8", "heading_corpus_95_word", "04_giao_trinh",
            "063_Advanced_Linear_Algebra.docx");

        var slim = new DocxSlimExtractor().Extract(docx);

        var result = BookTocDictionaryOutline.Analyze(slim);

        Assert.True(result.Accepted, result.Diagnostics.Reason);
        Assert.Equal(102, result.Headings.Count);
        Assert.True(result.Diagnostics.BodyAnchorRatio > 0.95);
        Assert.All(result.Headings, h => Assert.Equal(BookTocDictionaryOutline.Basis, h.ConfidenceBasis));
        Assert.Contains(result.Headings, h =>
            h.Level == 1 &&
            h.Text == "Part I. Linear algebra");
        Assert.Contains(result.Headings, h =>
            h.Level == 1 &&
            h.Text == "Part II. Advanced results");
        Assert.Contains(result.Headings, h =>
            h.Level == 1 &&
            h.Text == "Part III. Positive matrices");
        Assert.Contains(result.Headings, h =>
            h.Level == 1 &&
            h.Text == "Part IV. Matrix groups");
        Assert.Contains(result.Headings, h =>
            h.Level == 2 &&
            h.Text == "Chapter 1. Linear maps");
        Assert.Contains(result.Headings, h =>
            h.Level == 3 &&
            h.Text == "1b. Linear maps");
        Assert.DoesNotContain(result.Headings, h =>
            h.Text.Contains("As you can see", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Native_analyzer_matches_legacy_output()
    {
        var docx = Path.Combine(
            RepositoryRoot(), "todo10_8", "heading_corpus_95_word", "04_giao_trinh",
            "063_Advanced_Linear_Algebra.docx");
        var slim = new DocxSlimExtractor().Extract(docx);
        var native = PolicyStateFixture.FromSlim(slim).Paragraphs.Cast<IPolicyParagraph>().ToArray();

        var legacy = BookTocDictionaryOutline.Analyze(slim);
        var current = BookTocDictionaryOutline.Analyze(native);

        Assert.Equal(legacy.Accepted, current.Accepted);
        Assert.Equal(legacy.Diagnostics, current.Diagnostics);
        Assert.Equal(legacy.Headings.Select(Project), current.Headings.Select(Project));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }

    private static object Project(HeadingRecord heading) => new
    {
        heading.Index,
        heading.StableId,
        heading.SourceId,
        heading.Text,
        heading.Level,
        heading.HeadingSpan,
        heading.BoundarySource,
        heading.DecisionStatus,
        heading.ConfidenceBasis,
    };
}
