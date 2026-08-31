using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfTocDictionaryOutlineTests
{
    [Fact]
    public void DirectPdfTocProducerPreservesIbrdInformationStatement054()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "03_tai_chinh_ke_toan",
            "054_IBRD_Information_Statement_FY25.docx");
        Assert.True(File.Exists(docx), $"Missing fixture: {docx}");

        var source = new OpenXmlDocumentSource().Read(docx);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var policy = DocxPolicyStateBuilder.Build(source, features,
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
        var paragraphs = policy.Paragraphs.Cast<IPolicyParagraph>().ToArray();
        var mode = DocumentModeClassifier.Measure(paragraphs);
        var result = PdfTocDictionaryOutline.TryBuild(docx, paragraphs, mode);

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(24, result.Probe.Entries);
        Assert.Equal(24, result.Probe.RelaxedPageAnchors);
        Assert.Equal(24, result.Headings.Count);
        Assert.All(result.Headings, h =>
        {
            Assert.Equal(1, h.Level);
            Assert.Equal(PdfTocDictionaryOutline.Basis, h.ConfidenceBasis);
        });
        Assert.Contains(result.Headings, h => h.Text == "Availability of Information");
        Assert.Contains(result.Headings, h => h.Text == "Summary Information");
        Assert.Contains(result.Headings, h => h.Text == "Overview");
        Assert.Contains(result.Headings, h => h.Text == "Financial Results");
        Assert.Contains(result.Headings, h => h.Text == "Index to Financial Statements and Internal Control Reports");
        Assert.DoesNotContain(result.Headings, h => h.Text.Contains("Lending Highlights", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
