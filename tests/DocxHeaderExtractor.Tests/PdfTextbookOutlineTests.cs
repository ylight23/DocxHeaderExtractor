using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public class PdfTextbookOutlineTests
{
    [Fact]
    public void OpenStax056UsesPdfLayoutWhenDocxTextLayoutLostBoundaries()
    {
        var docx = Path.Combine(
            "todo10_8", "heading_corpus_95_word", "04_giao_trinh",
            "056_OpenStax_Business_Law_I_Essentials.docx");
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "04_giao_trinh",
            "056_OpenStax_Business_Law_I_Essentials.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor(new ExtractionOptions { SplitMergedParagraphs = true }).Extract(docx);
        var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);

        var result = PdfTextbookOutline.TryBuild(docx, slim, mode);

        Assert.Contains("aligned=46/52", result.Reason);
        Assert.Equal(46, result.Headings.Count);
        Assert.Equal(HeadingDecisionStatus.AutoAcceptedEvidence, result.Headings[0].DecisionStatus);
        Assert.All(result.Headings, h => Assert.Equal("pdf_textbook_layout", h.ConfidenceBasis));
        Assert.Contains(result.Headings, h =>
            h.Index == 50 &&
            h.Level == 2 &&
            h.Text == "2.1 Negotiation");
        Assert.Contains(result.Headings, h =>
            h.Index == 298 &&
            h.Level == 2 &&
            h.Text == "14.2 The Framework of Securities Regulation");
        Assert.DoesNotContain(result.Headings, h => h.Text.Contains('•'));
    }

    [Fact]
    public void NumberingAuditParsesPdfLayoutHeadingTextNotTheWholeParagraph()
    {
        var document = new SlimDocument
        {
            FileName = "synthetic.docx",
            SourcePath = "synthetic.docx",
            Paragraphs =
            [
                new SlimParagraph
                {
                    Index = 50,
                    StableId = "p51",
                    Text = "2.1 • Negotiation 15 prone to zero-sum thinking...",
                },
                new SlimParagraph
                {
                    Index = 52,
                    StableId = "p53",
                    Text = "16 2 • Disputes and Dispute Settlement Negotiation Styles in Practice ...",
                },
            ],
        };
        var headings = new List<HeadingRecord>
        {
            new()
            {
                Index = 50,
                StableId = "p51",
                Level = 2,
                Text = "2.1 Negotiation",
                ConfidenceBasis = "pdf_textbook_layout",
                Source = HeadingSource.Structure,
            },
            new()
            {
                Index = 52,
                StableId = "p53",
                Level = 2,
                Text = "2.2 Mediation",
                ConfidenceBasis = "pdf_textbook_layout",
                Source = HeadingSource.Structure,
            },
        };

        var warnings = NumberingAudit.Run(headings, document);

        Assert.Empty(warnings);
    }
}
