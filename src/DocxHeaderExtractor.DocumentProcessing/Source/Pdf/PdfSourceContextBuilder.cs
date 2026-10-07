using System.Collections.Immutable;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

internal static class PdfSemanticSourceContextBuilder
{
    public static IReadOnlyDictionary<string, PdfSemanticSourceContext> Build(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        int contextWindow = 2)
    {
        var annotationByLine = annotations.ToDictionary(a => LineKey(a.Line));
        var ordered = blocks.OrderBy(b => b.Page).ThenByDescending(b => b.TopY).ThenBy(b => b.Id, StringComparer.Ordinal).ToArray();
        const string regime = "document_body";
        var result = new Dictionary<string, PdfSemanticSourceContext>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Length; index++)
        {
            var block = ordered[index];
            var facts = BuildFacts(block, annotationByLine, regime);
            var window = Math.Clamp(contextWindow, 0, 6);
            var previous = ordered.Take(index).TakeLast(window).Select(b => PromptExcerpt(b.DisplayText)).ToArray();
            var next = ordered.Skip(index + 1).Take(window).Select(b => PromptExcerpt(b.DisplayText)).ToArray();
            var parents = ordered.Take(index).TakeLast(8).Select(b => b.Id).ToArray();
            result[block.Id] = new PdfSemanticSourceContext(facts, previous, next, parents, regime);
        }
        return result;
    }

    private static PdfLineTypography? TypographyOf(IReadOnlyList<PdfLine> lines)
    {
        var typed = lines.Select(line => line.Typography).OfType<PdfLineTypography>().ToArray();
        if (typed.Length == 0) return null;
        var bold = typed.FirstOrDefault(t => t.BoldEvidenceSource != PdfLineTypography.None);
        return new PdfLineTypography(
            typed[0].Version,
            typed.Average(t => t.NominalFontSize),
            typed.Average(t => t.EffectivePointSize),
            typed.Average(t => t.FontBoldFlagRatio),
            typed.Average(t => t.FontNameBoldRatio),
            typed.Average(t => t.DerivedBoldRatio),
            bold?.BoldEvidenceSource ?? PdfLineTypography.None,
            typed.Select(t => t.FontName).FirstOrDefault(n => n.Length > 0) ?? "")
        {
            Glyphs = GlyphsOf(lines),
        };
    }

    /// <summary>
    /// A block's glyph statistics from its lines': the dominant size is the one whose lines carry most characters,
    /// the median the median of the lines' medians, the extremes the extremes, the ratios weighted by characters.
    /// </summary>
    private static PdfGlyphStatistics? GlyphsOf(IReadOnlyList<PdfLine> lines)
    {
        var typed = lines.Where(l => l.Typography?.Glyphs is not null).Select(l => (Line: l, Glyphs: l.Typography!.Glyphs!)).ToArray();
        if (typed.Length == 0) return null;
        if (typed.Length == 1) return typed[0].Glyphs;
        var weight = typed.Select(t => (double)Math.Max(1, t.Line.Text.Length)).ToArray();
        var total = weight.Sum();
        var medians = typed.Select(t => t.Glyphs.MedianPointSize).Order().ToArray();
        return new PdfGlyphStatistics(
            typed.Select((t, i) => (t.Glyphs.DominantPointSize, weight[i])).GroupBy(x => x.DominantPointSize)
                .OrderByDescending(g => g.Sum(x => x.Item2)).ThenBy(g => g.Key).First().Key,
            medians[medians.Length / 2],
            typed.Min(t => t.Glyphs.MinPointSize),
            typed.Max(t => t.Glyphs.MaxPointSize),
            typed.Select((t, i) => (t.Glyphs.DominantFontName, weight[i])).Where(x => x.DominantFontName.Length > 0)
                .GroupBy(x => x.DominantFontName).OrderByDescending(g => g.Sum(x => x.Item2)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).FirstOrDefault() ?? "",
            typed.Select((t, i) => t.Glyphs.BoldGlyphRatio * weight[i]).Sum() / total,
            typed.Select((t, i) => t.Glyphs.ItalicGlyphRatio * weight[i]).Sum() / total);
    }

    private static PdfLineBlockAnnotation? LeastRecurring(IReadOnlyList<PdfLineBlockAnnotation> annotations) =>
        annotations.Count == 0 ? null : annotations.MinBy(a => a.SameNormalizedTextPageCount);

    private static PdfSourceFacts BuildFacts(
        PdfSemanticBlock block,
        IReadOnlyDictionary<string, PdfLineBlockAnnotation> annotationByLine,
        string regime)
    {
        var sourceAnnotations = block.Lines
            .Select(line => annotationByLine.TryGetValue(LineKey(line), out var annotation) ? annotation : null)
            .Where(annotation => annotation is not null)
            .Cast<PdfLineBlockAnnotation>()
            .ToArray();
        var marker = SourceMarkerFactsParser.Parse(block.DisplayText);
        var evidence = new List<string>
        {
            block.LineCount == 1 ? "standalone_line" : "multi_line_cluster",
            marker is null ? "no_marker" : $"marker:{marker.Value.Family}",
        };
        var looseMarker = LooseLabelledMarkerParser.ParseCanonical(block.DisplayText);
        if (looseMarker is not null &&
            PdfTextUtilities.CanonicalForMatch(block.DisplayText).Length <
            PdfTextUtilities.CanonicalForMatch(looseMarker).Length + 6)
            evidence.Add("marker_only_source");
        var facts = new PdfSourceFacts(
            // The canonical projection, the same string the alias catalog and the binder use. These
            // two paths build facts for one occurrence and must agree: if the model is shown the raw
            // concatenation while the binder validates against the projection, every proposal fails
            // as non-verbatim and the failure looks like the model getting the text wrong.
            block.Id, block.VerbatimText, block.Page, block.LineCount, block.Left, block.TopY, block.Right, block.BottomY,
            "document_body", evidence)
        {
            Marker = marker,
            BoldRatio = block.Lines.Count == 0 ? 0 : block.Lines.Average(line => line.BoldRatio),
            ItalicRatio = block.Lines.Count == 0 ? 0 : block.Lines.Average(line => line.ItalicRatio),
            FontSize = block.Lines.Count == 0 ? 0 : block.Lines.Average(line => line.FontSize),
            Typography = TypographyOf(block.Lines),
            LineIds = block.Lines.Select(LineKey).ToArray(),
            VerticalPosition = sourceAnnotations.Length == 0 ? null
                : sourceAnnotations.Max(a => a.VerticalPosition),
            SameNormalizedTextPageCount = LeastRecurring(sourceAnnotations)?.SameNormalizedTextPageCount,
            SameNormalizedTextFirstPage = LeastRecurring(sourceAnnotations)?.SameNormalizedTextFirstPage,
            SameNormalizedTextLastPage = LeastRecurring(sourceAnnotations)?.SameNormalizedTextLastPage,
            EvidenceDetails = evidence.Select(item => new PdfObservedEvidence(item, "true",
                item is "standalone_line" or "multi_line_cluster"
                    ? "layout_parser"
                    : "marker_parser")).ToArray(),
        };
        return facts;
    }

    private static string LineKey(PdfLine line) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{line.Page}|{line.Y:R}|{line.Left:R}|{line.Right:R}|{line.Text}");

    private static string PromptExcerpt(string text) => text.Length <= 180 ? text : text[..180];
}
