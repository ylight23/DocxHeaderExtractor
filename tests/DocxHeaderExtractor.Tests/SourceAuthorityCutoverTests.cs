using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceAuthorityCutoverTests
{
    [Fact]
    public void Native_authority_context_reads_source_text()
    {
        var source = new SourceDocument
        {
            DocumentId = "source.docx",
            FileName = "source.docx",
            SourcePath = "source.docx",
            SourceKind = "docx",
            Paragraphs = [SourceParagraph("p0", "authoritative source text")],
        };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var policy = DocxPolicyStateBuilder.Build(source, features,
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
        var authority = DocxAuthorityPipeline.BuildForAudit(policy, Mode());

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
