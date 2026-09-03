using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

internal static class NativePolicyStateFactory
{
    public static DocxPolicyState Create(
        IEnumerable<(int Index, string Text, int? NumberingId, int? StyleLevel)> items,
        StyleTrust? styleTrust = null) =>
        Create(items.Select(item => (item.Index, item.Text, item.NumberingId, item.StyleLevel, (string?)null)), styleTrust);

    public static DocxPolicyState Create(
        IEnumerable<(int Index, string Text, int? NumberingId, int? StyleLevel, string? NumberLabel)> items,
        StyleTrust? styleTrust = null)
    {
        var specs = items.ToArray();
        var source = new SourceDocument
        {
            DocumentId = "native-test.docx",
            FileName = "native-test.docx",
            SourcePath = "native-test.docx",
            SourceKind = "docx",
            Paragraphs = specs.Select(item => new SourceParagraph
            {
                SourceId = $"p[{item.Index}]",
                SourceOrdinal = item.Index,
                Text = item.Text,
                Style = new SourceStyleFacts
                {
                    BuiltInHeadingStyleLevel = item.StyleLevel,
                    FontSizePt = 12,
                },
                Numbering = new SourceNumberingFacts
                {
                    NumberingId = item.NumberingId,
                    NumberLabel = item.NumberLabel,
                },
                Layout = new SourceLayoutFacts(),
            }).ToArray(),
        };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var built = DocxPolicyStateBuilder.Build(source, features, derived, new ExtractionOptions());
        foreach (var spec in specs)
        {
            var paragraph = built.Paragraphs.Single(item => item.Index == spec.Index);
            paragraph.TrustedHeadingStyle = spec.StyleLevel is not null;
            paragraph.GuessedLevel = spec.StyleLevel;
        }
        return styleTrust is null
            ? built
            : new DocxPolicyState(source, features, derived, built.Paragraphs, styleTrust);
    }
}
