using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfTocDictionaryOutlineTests
{
    [Fact]
    public async Task PipelineUsesPdfTocDictionaryForIbrdInformationStatement054()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "03_tai_chinh_ke_toan",
            "054_IBRD_Information_Statement_FY25.docx");
        var pdf = Path.Combine(
            root, "todo10_8", "heading_corpus_100", "03_tai_chinh_ke_toan",
            "054_IBRD_Information_Statement_FY25.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var outline = await pipeline.RunAsync(docx);

        Assert.Equal("auto:pdf-toc-dictionary", outline.DeterministicRoute);
        Assert.Equal(24, outline.Headings.Count);
        Assert.All(outline.Headings, h =>
        {
            Assert.Equal(1, h.Level);
            Assert.Equal(PdfTocDictionaryOutline.Basis, h.ConfidenceBasis);
        });
        Assert.Contains(outline.Headings, h => h.Text == "Availability of Information");
        Assert.Contains(outline.Headings, h => h.Text == "Summary Information");
        Assert.Contains(outline.Headings, h => h.Text == "Overview");
        Assert.Contains(outline.Headings, h => h.Text == "Financial Results");
        Assert.Contains(outline.Headings, h => h.Text == "Index to Financial Statements and Internal Control Reports");
        Assert.DoesNotContain(outline.Headings, h => h.Text.Contains("Lending Highlights", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
