using DocxHeaderExtractor.Core.OpenXmlLayer;
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
    public async Task PipelineReportsBookTocDictionaryRoute()
    {
        var docx = Path.Combine(
            RepositoryRoot(), "todo10_8", "heading_corpus_95_word", "04_giao_trinh",
            "063_Advanced_Linear_Algebra.docx");

        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions
        {
            DisableLlm = true,
        });

        var outline = await pipeline.RunAsync(docx);

        Assert.Equal("auto:book-toc-dictionary", outline.DeterministicRoute);
        Assert.Equal(102, outline.Headings.Count);
        Assert.Equal(0, outline.DisputedCount);
        Assert.All(outline.Headings, h => Assert.Equal(BookTocDictionaryOutline.Basis, h.ConfidenceBasis));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
