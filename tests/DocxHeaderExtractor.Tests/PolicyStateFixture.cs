using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

internal static class PolicyStateFixture
{
    public static DocxPolicyState FromSlim(SlimDocument document)
    {
        var source = DocxSourceFactsBuilder.Build(document.SourcePath, document.Paragraphs,
            document.PageHeaders, document.PageFooters);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var built = DocxPolicyStateBuilder.Build(source, features,
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
        return new DocxPolicyState(source, features, built.DerivedFeatures, built.Paragraphs,
            document.StyleTrust, document.Mode);
    }
}
