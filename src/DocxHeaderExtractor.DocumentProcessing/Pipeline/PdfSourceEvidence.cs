using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The model-visible evidence of one PDF source atom: its text, the facts the parser observed and
/// where it sits. Facts only - nothing here decides what an atom means.
/// </summary>
internal static class PdfSourceEvidence
{
    internal static CanonicalSemanticSourceEvidence EvidenceOf(
        PdfSemanticSourceContext context,
        string alias,
        int ordinal,
        double bodyFontSize)
    {
        var source = context.Source;
        return new CanonicalSemanticSourceEvidence(
            alias,
            source.SourceId,
            ordinal,
            source.RawText,
            source.StructuralScope,
            ["pdf-parser-source", $"page:{source.Page}"],
            StyleFactsOf(source, bodyFontSize),
            new { },
            [],
            source.ObservedEvidence,
            context.PreviousBlocks,
            context.NextBlocks)
        {
            // What the V2 request shows in place of the scope label: where the occurrence sits and how
            // often its text recurs, so the model - not the harness - decides it is page furniture or
            // a contents entry.
            LocationFacts = new
            {
                page = source.Page,
                verticalPosition = source.VerticalPosition,
                sameNormalizedTextPageCount = source.SameNormalizedTextPageCount,
                sameNormalizedTextFirstPage = source.SameNormalizedTextFirstPage,
                sameNormalizedTextLastPage = source.SameNormalizedTextLastPage,
            },
        };
    }

    /// <summary>
    /// The style measurements an occurrence declares. These are raw parser observations and numeric
    /// ratios only: no "larger-than-body", no bold/italic threshold, no pre-semantic salience label.
    /// </summary>
    private static object StyleFactsOf(PdfSourceFacts source, double bodyFontSize)
    {
        var typography = source.Typography;
        var baseFacts = new
        {
            fontSize = Round(source.FontSize, 2),
            bodyFontSize = Round(bodyFontSize, 2),
            fontSizeToBodyRatio = Ratio(source.FontSize, bodyFontSize),
            boldRatio = Round(source.BoldRatio, 3),
            italicRatio = Round(source.ItalicRatio, 3),
            lineCount = source.LineCount,
        };
        if (typography?.Glyphs is not { } glyphs)
        {
            if (typography is null)
                return baseFacts;

            return new
            {
                baseFacts.fontSize,
                baseFacts.bodyFontSize,
                baseFacts.fontSizeToBodyRatio,
                baseFacts.boldRatio,
                baseFacts.italicRatio,
                baseFacts.lineCount,
                Typography = new
                {
                    sourceFacts = PdfSourceFactsVersions.Id(typography.Version),
                    effectivePointSize = Round(typography.EffectivePointSize, 2),
                    fontName = typography.FontName,
                    fontBoldFlagRatio = Round(typography.FontBoldFlagRatio, 3),
                    fontNameBoldRatio = Round(typography.FontNameBoldRatio, 3),
                    derivedBoldRatio = Round(typography.DerivedBoldRatio, 3),
                    boldEvidenceSource = typography.BoldEvidenceSource,
                },
            };
        }

        return new
        {
            baseFacts.fontSize,
            baseFacts.bodyFontSize,
            baseFacts.fontSizeToBodyRatio,
            baseFacts.boldRatio,
            baseFacts.italicRatio,
            baseFacts.lineCount,
            Typography = new
            {
                sourceFacts = PdfSourceFactsVersions.Id(typography.Version),
                dominantPointSize = Round(glyphs.DominantPointSize, 2),
                medianPointSize = Round(glyphs.MedianPointSize, 2),
                minPointSize = Round(glyphs.MinPointSize, 2),
                maxPointSize = Round(glyphs.MaxPointSize, 2),
                dominantFontName = glyphs.DominantFontName,
                fontBoldFlagRatio = Round(typography.FontBoldFlagRatio, 3),
                fontNameBoldRatio = Round(typography.FontNameBoldRatio, 3),
                derivedBoldRatio = Round(typography.DerivedBoldRatio, 3),
                boldEvidenceSource = typography.BoldEvidenceSource,
                boldGlyphRatio = Round(glyphs.BoldGlyphRatio, 3),
                italicGlyphRatio = Round(glyphs.ItalicGlyphRatio, 3),
            },
        };
    }

    private static double? Ratio(double size, double body) =>
        size <= 0 || body <= 0 ? null : Round(size / body, 3);

    private static double Round(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);

    internal static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(value => value > 0).OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
    }
}
