using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class DocxAuthorityPipelineTests
{
    [Fact]
    public void BuildCreatesDocxSourceFactsAndKeepsScopeOutOfActiveStack()
    {
        var document = new SlimDocument
        {
            FileName = "sample.docx",
            SourcePath = "sample.docx",
            Paragraphs =
            [
                Paragraph(1, "s1", "Chapter 1 Introduction"),
                Paragraph(2, "s2", "1.1 Scope"),
                Paragraph(3, "toc", "1. Introduction ........ 3", toc: true),
                Paragraph(4, "table", "Revenue 2025", tableDepth: 1),
                Paragraph(5, "code", "cache-control = 1#cache-directive"),
            ],
        }.Build();

        var source = DocxAuthorityPipeline.BuildForAudit(document, Mode());

        Assert.Equal(5, source.Blocks.Count);
        Assert.Equal("table_of_contents", source.Contexts["toc"].Scope);
        Assert.Equal("table", source.Contexts["table"].Scope);
        Assert.Equal("code_or_grammar", source.Contexts["code"].Scope);
        Assert.Contains("s1", source.ModelContexts["s2"].ActiveHeadingStack.Single());
        Assert.DoesNotContain(source.ModelContexts["code"].ActiveHeadingStack, item => item.StartsWith("toc:", StringComparison.Ordinal));
        Assert.Equal("Arabic", source.ModelContexts["s2"].Source.Marker?.Signature.Split(':')[0]);
        Assert.Equal("docx_parser", source.ModelContexts["s1"].Source.EvidenceDetails.First().Origin);
    }

    [Fact]
    public void BuildAppliesLegalOntologyWithoutDependingOnDocumentName()
    {
        var document = new SlimDocument
        {
            FileName = "anything.docx",
            SourcePath = "anything.docx",
            Paragraphs =
            [
                Paragraph(1, "part", "PHAN I QUY DINH CHUNG"),
                Paragraph(2, "article", "DIEU 1. Pham vi dieu chinh"),
                Paragraph(3, "note", "Khoan 3 Dieu 3 duoc sua doi, bo sung boi van ban khac"),
            ],
        }.Build();

        var source = DocxAuthorityPipeline.BuildForAudit(document,
            new DocumentModeReport(DocumentMode.VietnameseLegal, 3, 0, 0, 0, 0, 0, false));

        Assert.Equal(PdfDomainRole.LegalPart, source.ModelContexts["part"].Source.DomainRole);
        Assert.Equal(PdfDomainRole.LegalArticle, source.ModelContexts["article"].Source.DomainRole);
        Assert.Equal(PdfDomainRole.AmendmentAnnotation, source.ModelContexts["note"].Source.DomainRole);
    }

    private static SlimParagraph Paragraph(int index, string id, string text, bool toc = false, int tableDepth = 0) => new()
    {
        Index = index,
        StableId = id,
        Text = text,
        TableDepth = tableDepth,
        Role = ParagraphRole.Normal,
        InTableOfContents = toc,
        FontSizePt = 12,
    };

    private static DocumentModeReport Mode() => new(DocumentMode.SemanticOnly, 5, 0, 0, 0, 0, 0, false);
}
