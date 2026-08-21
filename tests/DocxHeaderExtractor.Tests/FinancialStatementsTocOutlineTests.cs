using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class FinancialStatementsTocOutlineTests
{
    [Fact]
    public async Task WbgFinancialStatement041UsesContentsDictionary()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "03_tai_chinh_ke_toan",
            "041_IBRD_Financial_Statements_June_2025.docx");

        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var outline = await pipeline.RunAsync(docx);

        Assert.Equal("auto:financial-statement-toc", outline.DeterministicRoute);
        Assert.True(outline.Headings.Count >= 30);
        Assert.Contains(outline.Headings, h => h.Level == 1 && h.Text == "Section I: Overview");
        Assert.Contains(outline.Headings, h => h.Level == 2 && h.Text == "Introduction");
        Assert.Contains(outline.Headings, h => h.Level == 2 && h.Text == "Financial Business Model");
        Assert.Contains(outline.Headings, h => h.Level == 1 && h.Text.StartsWith("Section XIV:", StringComparison.Ordinal));
        Assert.Contains(outline.Headings, h => h.Level == 1 && h.Text == "Appendix");
        Assert.All(outline.Headings, h => Assert.Equal(FinancialStatementsTocOutline.Basis, h.ConfidenceBasis));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
