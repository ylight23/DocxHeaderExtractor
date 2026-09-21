using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The visual line a PDF actually has, reconstructed independently of the grouper so it can be
/// used as evidence against it.
/// <para>
/// A source occurrence is supposed to be the harness's reconstruction of a structural boundary.
/// To say whether that reconstruction is right, something other than the reconstruction has to
/// define the boundary. That is what this is: parser lines regrouped by vertical overlap, which is
/// the one thing a reader can see and the grouper does not use.
/// </para>
/// <para>
/// <see cref="PdfLineExtraction"/> buckets glyphs by their vertical <em>midpoint</em> with a
/// tolerance scaled to glyph height. A full-height letter and a period sitting on the same baseline
/// have midpoints several points apart, so the period leaves the line it belongs to and becomes a
/// line - then an occurrence - of its own. Overlap does not have that failure: a period's extent
/// lies wholly inside the extent of the text it sits with, whatever their midpoints do.
/// </para>
/// <para>
/// This is measurement only. Nothing here is wired into extraction, and it deliberately does not
/// propose the replacement rule - the census it feeds exists to decide what that rule should be.
/// </para>
/// </summary>
internal sealed record PdfVisualLine(
    int Index,
    int Page,
    double Top,
    double Bottom,
    IReadOnlyList<int> OccurrenceIndexes,
    string Text)
{
    /// <summary>More than one occurrence on one visual line: the line was split by the grouper.</summary>
    public bool SplitAcrossOccurrences => OccurrenceIndexes.Count > 1;
}

internal static class PdfSourceOccurrenceBoundary
{
    /// <summary>
    /// Groups the parser lines behind <paramref name="occurrences"/> into visual lines.
    /// <para>
    /// Two parser lines are the same visual line when one's vertical centre falls inside the
    /// other's extent. Containment rather than a distance threshold, so the rule does not need a
    /// tuned constant and cannot chain two body lines together through a run of near misses.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PdfVisualLine> VisualLines(IReadOnlyList<PdfSemanticBlock> occurrences)
    {
        var members = occurrences
            .SelectMany((block, index) => block.Lines.Select(line => (Block: index, Line: line)))
            .Where(item => item.Line.Top is not null && item.Line.Bottom is not null)
            .GroupBy(item => item.Line.Page)
            .OrderBy(group => group.Key);

        var lines = new List<PdfVisualLine>();
        foreach (var page in members)
        {
            // Descending Top, so the tallest glyph run on a baseline anchors its line. Punctuation
            // peeled off a line is shorter than the text it belongs to, so it always arrives second
            // and joins rather than anchoring.
            var ordered = page.OrderByDescending(item => item.Line.Top!.Value)
                .ThenBy(item => item.Line.Left)
                .ToArray();

            var open = new List<(int Block, PdfLine Line)>();
            double anchorTop = 0, anchorBottom = 0;

            void Close()
            {
                if (open.Count == 0) return;
                lines.Add(new PdfVisualLine(
                    lines.Count,
                    open[0].Line.Page,
                    open.Max(item => item.Line.Top!.Value),
                    open.Min(item => item.Line.Bottom!.Value),
                    open.Select(item => item.Block).Distinct().Order().ToArray(),
                    string.Join(" ", open.OrderBy(item => item.Line.Left).Select(item => item.Line.Text))));
                open.Clear();
            }

            foreach (var item in ordered)
            {
                var centre = (item.Line.Top!.Value + item.Line.Bottom!.Value) / 2;
                if (open.Count > 0 && centre <= anchorTop && centre >= anchorBottom)
                {
                    open.Add(item);
                    continue;
                }

                Close();
                open.Add(item);
                anchorTop = item.Line.Top!.Value;
                anchorBottom = item.Line.Bottom!.Value;
            }

            Close();
        }

        return lines;
    }

    /// <summary>
    /// The parser lines of <paramref name="occurrence"/> that carry <paramref name="goldText"/>.
    /// <para>
    /// Gold addresses a heading by alias plus, for a partial span, its exact text. Which lines that
    /// text lands on is a harness fact, recovered here rather than asserted: a claim is attributed
    /// only to the lines it actually occupies, so a body line fused into the same occurrence cannot
    /// lend its defects to a heading that has none.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> LinesCarrying(PdfSemanticBlock occurrence, string? goldText)
    {
        var all = Enumerable.Range(0, occurrence.LineCount).ToArray();
        if (goldText is null) return all;

        var start = occurrence.VerbatimText.IndexOf(goldText, StringComparison.Ordinal);
        if (start < 0) return all;
        var end = start + goldText.Length;

        var carried = new List<int>();
        var offset = 0;
        for (var i = 0; i < occurrence.LineCount; i++)
        {
            var text = occurrence.Lines[i].Projection.VerbatimText;
            var lineStart = offset;
            var lineEnd = offset + text.Length;
            if (lineStart < end && lineEnd > start) carried.Add(i);
            offset = lineEnd + Separator.Length;
        }

        return carried.Count > 0 ? carried : all;
    }

    /// <summary>
    /// How <see cref="PdfSemanticBlock.Projection"/> joins its lines. Asserted against the real
    /// occurrence text before the offsets above are trusted, rather than assumed.
    /// </summary>
    public const string Separator = " ";

    /// <summary>Whitespace-insensitive, case-insensitive identity. Punctuation is kept.</summary>
    public static string Key(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character)).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The same identity with punctuation removed, to separate it from a real difference.</summary>
    public static string KeyWithoutPunctuation(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character))
            .Select(char.ToLowerInvariant).ToArray());
}
