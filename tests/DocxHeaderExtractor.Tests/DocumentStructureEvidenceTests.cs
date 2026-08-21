using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class DocumentStructureEvidenceTests
{
    [Fact]
    public void TextLayoutDocxWithoutNativeStructureMustPreferPdfEvidence()
    {
        var document = new SlimDocument
        {
            FileName = "converted.docx",
            SourcePath = "converted.docx",
            Paragraphs = [new SlimParagraph { Index = 0, StableId = "p1", Text = "ContentsSection I: Overview" }],
        };

        Assert.False(DocumentStructureEvidence.HasNativeSemanticStructure(document));
    }

    [Fact]
    public void NativeOoxmlStructureRemainsAuthoritative()
    {
        var document = new SlimDocument
        {
            FileName = "native.docx",
            SourcePath = "native.docx",
            Paragraphs = [new SlimParagraph { Index = 0, StableId = "p1", Text = "Introduction", OutlineLevel = 0 }],
        };

        Assert.True(DocumentStructureEvidence.HasNativeSemanticStructure(document));
    }

    [Fact]
    public void AuthoritativeTextTocOutranksVisualPdfCluster()
    {
        IReadOnlyCollection<HeadingRecord> headings =
        [
            new() { Index = 1, StableId = "p2", Level = 1, Text = "Section I", ConfidenceBasis = FinancialStatementsTocOutline.Basis },
        ];

        Assert.True(DocumentStructureEvidence.HasAuthoritativeInternalOutline(headings));
    }
}
