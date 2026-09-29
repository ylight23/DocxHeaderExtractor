namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Where one visual row stops being one region.
/// <para>
/// Grouping glyphs by baseline reconstructs a row that runs the full width of the page. That is
/// right for a line of prose and wrong for everything set side by side: a two-sided masthead, a
/// column pair, a table row. Those are separate pieces of text that happen to share a baseline, and
/// a coordinate atom spanning both cannot be bound to either.
/// </para>
/// <para>
/// A wide gap on its own does not say which of the two a row is. Tab-aligned metadata, a page
/// header with its folio, a justified last line and a signature line all leave large whitespace
/// inside a single region, and a corpus census counts 33,871 rows with a jump of more than three
/// line-heights - far more than the number of genuinely divided rows. What distinguishes them is
/// not the size of the gap but whether the whitespace <em>persists</em>: two regions set beside
/// each other leave a corridor that the rows around them leave open too, while a tab stop is a
/// hole in one row with text either side of it above and below.
/// </para>
/// <para>
/// So a cut needs two things: a gap much wider than the page's ordinary word space, and at least
/// one neighbouring row that keeps the same corridor open while carrying text on both sides of it.
/// Geometry only - no text, no font, no label, and nothing derived from what a heading is.
/// </para>
/// </summary>
internal static class PdfVisualRegion
{
    /// <summary>How many ordinary word spaces wide a gap must be before it is considered for a cut.</summary>
    public const double MinimumCutGapFactor = 3.0;

    /// <summary>How far above and below to look for a row that keeps the same corridor open.</summary>
    public const int NeighbourWindow = 2;

    /// <summary>How much of the proposed corridor a neighbour must also leave clear.</summary>
    public const double CorridorSupportRatio = 0.5;

    /// <summary>Neighbouring rows that must agree, besides the row holding the gap.</summary>
    public const int SupportingRowsRequired = 1;

    /// <summary>The horizontal extent of one glyph, which is all this rule reads.</summary>
    public readonly record struct Box(double Left, double Right);

    /// <summary>
    /// For each row, the indexes after which it should be cut. A row of <c>n</c> glyphs with cuts
    /// <c>[3]</c> becomes two segments: glyphs 0..3 and 4..n-1.
    /// </summary>
    /// <param name="rows">Rows in page order, each glyph ordered left to right.</param>
    /// <param name="wordGap">The page's ordinary space between words, in points.</param>
    public static IReadOnlyList<IReadOnlyList<int>> Cuts(
        IReadOnlyList<IReadOnlyList<Box>> rows, double wordGap)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var none = rows.Select(_ => (IReadOnlyList<int>)[]).ToArray();
        if (wordGap <= 0) return none;

        var cuts = new List<int>[rows.Count];
        for (var row = 0; row < rows.Count; row++)
        {
            cuts[row] = [];
            var glyphs = rows[row];
            for (var index = 0; index + 1 < glyphs.Count; index++)
            {
                var from = glyphs[index].Right;
                var to = glyphs[index + 1].Left;
                var width = to - from;
                if (width < MinimumCutGapFactor * wordGap) continue;

                var middle = (from + to) / 2;
                var supporting = 0;
                for (var offset = -NeighbourWindow; offset <= NeighbourWindow; offset++)
                {
                    var neighbour = row + offset;
                    if (offset == 0 || neighbour < 0 || neighbour >= rows.Count) continue;
                    if (ClearWidthAround(rows[neighbour], middle) >= width * CorridorSupportRatio)
                        supporting++;
                }

                if (supporting >= SupportingRowsRequired) cuts[row].Add(index);
            }
        }

        return cuts;
    }

    /// <summary>
    /// How wide the whitespace around <paramref name="middle"/> is in this row, counted only when
    /// the row has text on both sides of it. A row that simply ends before this point has no
    /// corridor there - it has a margin, and a margin is not evidence of a second region.
    /// </summary>
    public static double ClearWidthAround(IReadOnlyList<Box> row, double middle)
    {
        double left = double.NegativeInfinity, right = double.PositiveInfinity;
        var textLeft = false;
        var textRight = false;

        foreach (var box in row)
        {
            if (box.Left <= middle && box.Right >= middle) return 0;
            if (box.Right < middle)
            {
                textLeft = true;
                if (box.Right > left) left = box.Right;
            }
            else
            {
                textRight = true;
                if (box.Left < right) right = box.Left;
            }
        }

        return textLeft && textRight ? right - left : 0;
    }

    /// <summary>
    /// The page's ordinary space between words: the median of the gaps wide enough to be a space
    /// at all. Taken from the page rather than from a constant, because a gap only means something
    /// next to the spacing the document itself uses.
    /// </summary>
    public static double WordGap(IReadOnlyList<double> observedGaps)
    {
        ArgumentNullException.ThrowIfNull(observedGaps);
        if (observedGaps.Count == 0) return 0;

        var sorted = observedGaps.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
