using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class RfcTocDictionaryOutlineTests
{
    [Fact]
    public void Dung_toc_dictionary_giu_so_muc_va_khop_nav_092()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "07_system_generated",
            "092_RFC9111_HTTP_Caching.docx");
        Assert.True(File.Exists(docx), $"Missing fixture: {docx}");

        var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(docx);
        var result = RfcTocDictionaryOutline.Analyze(slim);
        var headings = result.Headings;

        Assert.True(result.Accepted);
        Assert.True(result.Diagnostics.TocParagraphs > 0);
        Assert.True(result.Diagnostics.DictionaryEntries >= 67);
        Assert.Equal(0, result.Diagnostics.TocOnlyEntries);
        Assert.True(result.Diagnostics.BodyAnchorRatio >= 0.90);
        Assert.Contains(headings, h => h.Text == "1. Introduction" && h.Index == 8);
        Assert.Contains(headings, h => h.Text == "1.1. Requirements Notation");
        Assert.DoesNotContain(headings, h => h.Text == "Requirements Notation");
        Assert.Contains(headings, h => h.Text == "9. References");
        Assert.Contains(headings, h => h.Text == "9.1. Normative References");
        Assert.Contains(headings, h => h.Text == "9.2. Informative References");
        Assert.Contains(headings, h => h.Text == "Appendix A Collected ABNF");
        Assert.Contains(headings, h => h.Text == "Appendix B Changes from RFC 7234");
        Assert.True(headings.Count >= 67);
    }

    [Fact]
    public async Task Pipeline_no_llm_khong_cat_lai_tieu_de_rfc_da_lay_tu_toc()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "07_system_generated",
            "092_RFC9111_HTTP_Caching.docx");
        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var outline = await pipeline.RunAsync(docx);

        Assert.Equal("auto:rfc-toc-dictionary", outline.DeterministicRoute);
        Assert.True(outline.Headings.Count >= 67);
        Assert.Contains(outline.Headings, h => h.Text == "3.1. Storing Header and Trailer Fields");
        Assert.DoesNotContain(outline.Headings, h => h.Text.StartsWith("3.1. Storing Header and Trailer Fields Caches MUST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pipeline_khong_bam_nguong_cu_khi_cum_toc_thap_hon_van_khop_than_bai()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "07_system_generated",
            "093_RFC9112_HTTP_1_1.docx");

        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var outline = await pipeline.RunAsync(docx);

        Assert.Equal("auto:rfc-toc-dictionary", outline.DeterministicRoute);
        Assert.Contains(outline.Headings, h => h.Text == "12. IANA Considerations");
        Assert.Contains(outline.Headings, h => h.Text == "13. References");
        Assert.Contains(outline.Headings, h => h.Text == "Appendix A Collected ABNF");
        Assert.Contains(outline.Headings, h => h.Text == "Appendix B Differences between HTTP and MIME");
        Assert.Contains(outline.Headings, h => h.Text == "B.1. MIME-Version");
        Assert.Contains(outline.Headings, h => h.Text == "B.6. MHTML and Line Length Limitations");
        Assert.Contains(outline.Headings, h => h.Text == "Appendix C Changes from Previous RFCs");
        Assert.Contains(outline.Headings, h => h.Text == "C.2.1. Multihomed Web Servers");
        Assert.Contains(outline.Headings, h => h.Text == "C.3. Changes from RFC 7230");
    }

    [Fact]
    public async Task Pipeline_bat_duoc_than_bai_sau_toc_duoi_acknowledgments_contributors_authors()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "07_system_generated",
            "094_RFC9113_HTTP_2.docx");

        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var outline = await pipeline.RunAsync(docx);

        Assert.Equal("auto:rfc-toc-dictionary", outline.DeterministicRoute);
        Assert.True(outline.Headings.Count >= 97);
        Assert.Contains(outline.Headings, h => h.Text == "1. Introduction" && h.Index == 10);
        Assert.Contains(outline.Headings, h => h.Text == "12. References");
        Assert.Contains(outline.Headings, h => h.Text == "12.1. Normative References");
        Assert.Contains(outline.Headings, h => h.Text == "12.2. Informative References");
        Assert.Contains(outline.Headings, h => h.Text == "Appendix A Prohibited TLS 1.2 Cipher Suites");
        Assert.Contains(outline.Headings, h => h.Text == "Appendix B Changes from RFC 7540");
    }

    [Fact]
    public void Toc_dictionary_tu_loai_khi_chi_co_than_bai_nhieu_so_muc()
    {
        var paragraphs = Enumerable.Range(0, 36)
            .Select(i => new SlimParagraph
            {
                Index = i,
                StableId = $"p[{i}]",
                Text = $"{i + 1}. Heading {i + 1} This paragraph references 1. Alpha 2. Beta 3. Gamma in prose.",
                FontSizePt = 12,
            })
            .ToList();
        var slim = new SlimDocument
        {
            FileName = "no-toc.docx",
            SourcePath = "no-toc.docx",
            Paragraphs = paragraphs,
        }.Build();

        var result = RfcTocDictionaryOutline.Analyze(slim);

        Assert.False(result.Accepted);
        Assert.Empty(result.Headings);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
