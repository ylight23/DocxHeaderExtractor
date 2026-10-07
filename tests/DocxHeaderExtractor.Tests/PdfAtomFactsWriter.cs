using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Writer of the two fact tables a source review reads (<c>atom-layout-facts.tsv</c>, <c>atom-glyph-facts.tsv</c>).
/// Diagnostic only; it changes nothing the pipeline reads. Runs when <c>A99_PDF_ATOM_FACTS</c> is
/// <c>&lt;pdf path relative to the repository&gt;|&lt;output directory&gt;</c>.
/// <para>
/// Layout facts: alias, page, row, segment, line font size, bold ratio, italic ratio, left, right, y, text, leading bold prefix.
/// Glyph facts: alias, page, row, segment, median point size of the glyphs inside the atom's line box,
/// font names with letter counts, left, right, y, text.
/// </para>
/// </summary>
public sealed class PdfAtomFactsWriter
{
    [Fact]
    public void Write_the_atom_fact_tables_when_requested()
    {
        var spec = Environment.GetEnvironmentVariable("A99_PDF_ATOM_FACTS");
        if (string.IsNullOrWhiteSpace(spec)) return;
        var parts = spec.Split('|');
        var pdf = TestRepository.Path(parts[0]);
        Directory.CreateDirectory(parts[1]);

        IReadOnlyList<PdfLine> extracted;
        using (var document = PdfDocument.Open(pdf)) extracted = PdfLineExtraction.ExtractLines(document);
        var atoms = PdfSourceOccurrenceAdapter.Build(extracted, CanonicalSemanticSourceHash.Compute(pdf)).Atoms;
        var lines = extracted.GroupBy(PdfLineIdentity.Of, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        File.WriteAllLines(Path.Combine(parts[1], "atom-layout-facts.tsv"), atoms.Select(a =>
        {
            var l = lines[a.SourceId];
            return $"{a.Alias}\t{a.Page}\t{a.Row}\t{a.Segment}\t{l.FontSize:0.0}\t{l.BoldRatio:0.00}\t{l.ItalicRatio:0.00}\t{l.Left:0}\t{l.Right:0}\t{l.Y:0}\t{a.Text.Replace('\t', ' ')}\t{l.LeadingBoldPrefix.Replace('\t', ' ')}";
        }), new System.Text.UTF8Encoding(false));

        using var reopened = PdfDocument.Open(pdf);
        var mode = PdfFontEmbedding.ModeFor(reopened);
        var letters = reopened.GetPages().ToDictionary(
            p => p.Number,
            p => p.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => (Glyph: PdfGlyph.Of(l, mode), Letter: l)).ToArray());
        File.WriteAllLines(Path.Combine(parts[1], "atom-glyph-facts.tsv"), atoms.Select(a =>
        {
            var l = lines[a.SourceId];
            var bottom = l.Bottom ?? l.Y;
            var top = l.Top ?? l.Y;
            var inside = letters[a.Page].Where(g =>
                g.Glyph.Left >= l.Left - 0.5 && g.Glyph.Right <= l.Right + 0.5 &&
                g.Glyph.Bottom >= bottom - 0.5 && g.Glyph.Top <= top + 0.5).Select(g => g.Letter).ToArray();
            var sizes = inside.Select(g => g.PointSize).Order().ToArray();
            var size = sizes.Length == 0 ? 0 : sizes[sizes.Length / 2];
            var fonts = inside.GroupBy(g => (g.FontName ?? "").Split('+')[^1]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}:{g.Count()}");
            return $"{a.Alias}\t{a.Page}\t{a.Row}\t{a.Segment}\t{size:0.0}\t{string.Join(",", fonts)}\t{l.Left:0}\t{l.Right:0}\t{l.Y:0}\t{a.Text.Replace('\t', ' ')}";
        }), new System.Text.UTF8Encoding(false));
    }
}
