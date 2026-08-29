using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class RfcTocDirectAnalyzerSemanticTests
{
    [Fact]
    public void Rfc_093_direct_analyzer_preserves_toc_semantics()
    {
        var result = RfcTocDictionaryOutline.Analyze(Extract("093_RFC9112_HTTP_1_1.docx"));

        Assert.True(result.Accepted);
        Assert.Equal(73, result.Diagnostics.DictionaryEntries);
        Assert.Equal(73, result.Diagnostics.BodyAnchors);
        Assert.Equal(0, result.Diagnostics.TocOnlyEntries);
        Assert.Equal(1.0, result.Diagnostics.BodyAnchorRatio);
        Assert.Contains(result.Headings, h => h.Text == "12. IANA Considerations");
        Assert.Contains(result.Headings, h => h.Text == "13. References");
        Assert.Contains(result.Headings, h => h.Text == "Appendix A Collected ABNF");
        Assert.Contains(result.Headings, h => h.Text == "Appendix B Differences between HTTP and MIME");
        Assert.Contains(result.Headings, h => h.Text == "B.1. MIME-Version");
        Assert.Contains(result.Headings, h => h.Text == "B.6. MHTML and Line Length Limitations");
        Assert.Contains(result.Headings, h => h.Text == "Appendix C Changes from Previous RFCs");
        Assert.Contains(result.Headings, h => h.Text == "C.2.1. Multihomed Web Servers");
        Assert.Contains(result.Headings, h => h.Text == "C.3. Changes from RFC 7230");
    }

    [Fact]
    public void Rfc_093_native_analyzer_matches_legacy_output()
    {
        var slim = Extract("093_RFC9112_HTTP_1_1.docx");
        var native = PolicyStateFixture.FromSlim(slim).Paragraphs.Cast<IPolicyParagraph>().ToArray();
        var legacy = RfcTocDictionaryOutline.Analyze(slim);
        var current = RfcTocDictionaryOutline.Analyze(native);

        Assert.Equal(legacy.Accepted, current.Accepted);
        Assert.Equal(legacy.Diagnostics, current.Diagnostics);
        Assert.Equal(legacy.Headings.Select(Project), current.Headings.Select(Project));
    }

    private static SlimDocument Extract(string fileName)
    {
        var root = RepositoryRoot();
        return new DocxSlimExtractor(new ExtractionOptions()).Extract(Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "07_system_generated", fileName));
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
