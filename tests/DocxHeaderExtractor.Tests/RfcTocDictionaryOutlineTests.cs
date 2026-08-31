using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;

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

        var result = AnalyzeNative(docx);
        var headings = result.Headings;

        Assert.True(result.Accepted);
        Assert.True(result.Diagnostics.TocParagraphs > 0);
        Assert.True(result.Diagnostics.DictionaryEntries >= 67);
        Assert.Equal(0, result.Diagnostics.TocOnlyEntries);
        Assert.True(result.Diagnostics.BodyAnchorRatio >= 0.90);
        var introduction = Assert.Single(headings.Where(h => h.Text == "1. Introduction"));
        Assert.Equal("body[1]/tbl[13]/tr[1]/tc[1]/p[1]", introduction.StableId);
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
    public void Toc_dictionary_tu_loai_khi_chi_co_than_bai_nhieu_so_muc()
    {
        var state = NativePolicyStateFactory.Create(
            Enumerable.Range(0, 36).Select(i =>
                (i,
                 $"{i + 1}. Heading {i + 1} This paragraph references 1. Alpha 2. Beta 3. Gamma in prose.",
                 (int?)null,
                 (int?)null)));

        var result = RfcTocDictionaryOutline.Analyze(
            state.Paragraphs.Cast<IPolicyParagraph>().ToArray());

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

    private static RfcTocDictionaryResult AnalyzeNative(string path)
    {
        var source = new OpenXmlDocumentSource().Read(path);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var policy = DocxPolicyStateBuilder.Build(source, features,
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
        return RfcTocDictionaryOutline.Analyze(policy.Paragraphs.Cast<IPolicyParagraph>().ToArray());
    }
}
