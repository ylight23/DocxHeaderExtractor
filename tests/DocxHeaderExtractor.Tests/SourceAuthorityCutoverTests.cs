using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceAuthorityCutoverTests
{
    [Fact]
    public void Normal_authority_context_reads_source_text_when_compatibility_text_differs()
    {
        var source = new SourceDocument
        {
            DocumentId = "source.docx",
            FileName = "source.docx",
            SourcePath = "source.docx",
            SourceKind = "docx",
            Paragraphs = [SourceParagraph("p0", "authoritative source text")],
        };
        var compatibility = new SlimDocument
        {
            FileName = "source.docx",
            SourcePath = "source.docx",
            Paragraphs = [new SlimParagraph
            {
                Index = 0,
                StableId = "p0",
                Text = "compatibility projection text",
                Role = ParagraphRole.HeadingCandidate,
                Score = 0.7,
            }],
        }.Build();

        var authority = DocxAuthorityPipeline.BuildForAudit(source, compatibility, Mode());

        Assert.Equal("authoritative source text", authority.Blocks.Single().DisplayText);
        Assert.Equal("authoritative source text", authority.ModelContexts["p0"].Source.RawText);
    }

    private static SourceParagraph SourceParagraph(string id, string text) => new()
    {
        SourceId = id,
        SourceOrdinal = 0,
        Text = text,
        Style = new SourceStyleFacts { StyleName = "Normal", FontSizePt = 11 },
        Numbering = new SourceNumberingFacts(),
        Layout = new SourceLayoutFacts(),
    };

    private static DocumentModeReport Mode() => new(DocumentMode.SemanticOnly, 1, 0, 0, 0, 0, 0, false);
}
