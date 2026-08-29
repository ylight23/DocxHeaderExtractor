using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Application.Policy;

/// <summary>Builds deterministic policy state from source facts without a Slim intermediate.</summary>
public static class DocxPolicyStateBuilder
{
    public static DocxPolicyState Build(
        SourceDocument source,
        NumberingStyleFeatures structuralFeatures,
        DerivedDocumentFeatures derivedFeatures,
        ExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(structuralFeatures);
        ArgumentNullException.ThrowIfNull(derivedFeatures);
        ArgumentNullException.ThrowIfNull(options);

        var numberingById = structuralFeatures.Numbering.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var styleById = structuralFeatures.Styles.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var paragraphs = source.Paragraphs.Select(paragraph =>
        {
            if (!numberingById.TryGetValue(paragraph.SourceId, out var numbering) ||
                !styleById.TryGetValue(paragraph.SourceId, out var style))
                throw new InvalidOperationException($"Missing structural features for '{paragraph.SourceId}'.");
            return new DocxPolicyParagraph
            {
                Source = paragraph,
                Numbering = numbering,
                Style = style,
                NumberingStyleHeadingLevel = numbering.NumberingStyleHeadingLevel,
                PrecedesTableOfContents = paragraph.InTableOfContents,
                BodyFontSizePt = derivedFeatures.BodyFontSizePt,
                Corrupt = derivedFeatures.CorruptSourceIds.Contains(paragraph.SourceId),
                TrustedHeadingStyle = false,
                InTableOfContents = paragraph.InTableOfContents,
            };
        }).ToArray();

        var policy = new DocxPolicyState(source, structuralFeatures, derivedFeatures, paragraphs);
        var candidatePolicy = new HeadingCandidatePolicy();
        foreach (var paragraph in paragraphs)
            candidatePolicy.Apply(new CandidatePolicyInput(paragraph, derivedFeatures, options));

        var styleTrust = StyleTrustAudit.Measure(paragraphs.Cast<IPolicyParagraph>().ToArray());
        foreach (var paragraph in paragraphs)
        {
            paragraph.TrustedHeadingStyle = styleTrust.SelectionTrusted &&
                paragraph.Style.BuiltInHeadingStyleLevel is not null;
            if (options.UseStyleTrust && !styleTrust.SelectionTrusted)
                candidatePolicy.Apply(new CandidatePolicyInput(paragraph, derivedFeatures, options, false));
        }

        return new DocxPolicyState(source, structuralFeatures, derivedFeatures, paragraphs, styleTrust);
    }
}
