using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Policy;

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

    [Fact]
    public async Task Docx_authority_produces_generic_structure_before_compatibility_projection()
    {
        var source = new SourceDocument
        {
            DocumentId = "source.docx",
            FileName = "source.docx",
            SourcePath = "source.docx",
            SourceKind = "docx",
            Paragraphs = [SourceParagraph("p0", "1. Authoritative heading", builtInLevel: 1)],
        };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var policy = DocxPolicyStateBuilder.Build(source, features,
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());

        var result = await DocxAuthorityPipeline.RunAsync(
            policy, Mode(), analyst: null);
        var element = Assert.Single(result.Structure.Elements);
        var projected = Assert.Single(HeadingOutlineProjection.Project(result.Structure));

        Assert.NotEqual(element.Id, Assert.Single(element.Sources).SourceId);
        Assert.Equal("p0", element.Sources[0].SourceId);
        Assert.Equal(new StructuralSpan(0, source.Paragraphs[0].Text.Length), element.Sources[0].Span);
        Assert.Equal("1. Authoritative heading", projected.Text);
        Assert.Null(projected.Level);
        Assert.Equal("p0", projected.SourceId);
    }

    private static SourceParagraph SourceParagraph(string id, string text, int? builtInLevel = null) => new()
    {
        SourceId = id,
        SourceOrdinal = 0,
        Text = text,
        Style = new SourceStyleFacts { StyleName = "Normal", FontSizePt = 11, BuiltInHeadingStyleLevel = builtInLevel },
        Numbering = new SourceNumberingFacts(),
        Layout = new SourceLayoutFacts(),
    };

    private static DocumentModeReport Mode() => new(DocumentMode.SemanticOnly, 1, 0, 0, 0, 0, 0, false);
}
