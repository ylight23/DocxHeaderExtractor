namespace DocxHeaderExtractor.Sources.Pdf;

/// <summary>
/// A line under construction, and the rule for what belongs to it.
/// <para>
/// A glyph's vertical midpoint is a function of its own shape, not of the line it sits on. A period
/// and a capital resting on one baseline have midpoints roughly a third of the cap height apart, so
/// a rule that measures midpoint distance splits them once the text is large enough - and the
/// period leaves the line it belongs to, becoming a line, a block and finally a source occurrence
/// of its own. The two signals here are properties of the line rather than of the glyph:
/// </para>
/// <para>
/// <b>Baseline</b> decides membership. Glyphs set on one line share a baseline by construction;
/// superscripts and subscripts are shifted from it by a fraction of an em, while the next line is a
/// full leading away. The tolerance is half the line's scale, which separates those two cases by
/// typography rather than by measurement of any particular document: a shift of more than half an
/// em is no longer a raised or lowered glyph, and a leading of less than half an em is not a line.
/// </para>
/// <para>
/// <b>Vertical overlap</b> guards against a baseline that is reported oddly - rotated text, a glyph
/// box that does not sit on its own baseline. The denominator is the <em>smaller</em> of the two
/// heights, so the test is symmetric and a small glyph is judged against its own size: a period
/// lying wholly inside the band is fully overlapped whatever the line's height, while a descender
/// reaching a sliver into the line below is not.
/// </para>
/// </summary>
internal sealed class PdfVisualLineBucket
{
    /// <summary>A glyph shifted by more than this fraction of the line's scale is a different line.</summary>
    public const double BaselineTolerance = 0.5;

    /// <summary>How much of the smaller box must lie inside the other's vertical band.</summary>
    public const double OverlapRatio = 0.5;

    private bool _open;
    private double _baseline;
    private double _scale;
    private double _top;
    private double _bottom;

    public int Count { get; private set; }

    /// <summary>
    /// The geometry of one glyph, without the parser type that carries it, so the rule can be
    /// exercised on cases a real document may not happen to contain.
    /// </summary>
    public readonly record struct Glyph(double Baseline, double Top, double Bottom, double FontSize)
    {
        public double Height => Top - Bottom;

        /// <summary>
        /// How large this glyph says its line is. A period is a small box on an 11pt line, and
        /// taking the declared point size as well as the drawn box keeps the line's tolerance from
        /// collapsing to the size of its smallest mark.
        /// </summary>
        public double Scale => Math.Max(FontSize, Height);
    }

    public bool Accepts(Glyph glyph)
    {
        if (!_open) return true;

        // Either signal is enough, and they are not interchangeable. Baseline is what a typesetter
        // actually aligned, so it admits a subscript whose small box barely reaches into the line's
        // band. Overlap is the rescue for glyphs whose baseline is reported oddly - rotated runs, a
        // box that does not sit on its own baseline - and for a mark large enough to span the line
        // whatever its baseline says. Requiring both would reject the subscript; requiring neither
        // in particular is what the incumbent midpoint rule effectively does.
        return AlignedBaseline(glyph) || Overlaps(glyph);
    }

    private bool AlignedBaseline(Glyph glyph) =>
        Math.Abs(glyph.Baseline - _baseline) <= Math.Max(1.0, _scale * BaselineTolerance);

    private bool Overlaps(Glyph glyph)
    {
        var overlap = Math.Min(glyph.Top, _top) - Math.Max(glyph.Bottom, _bottom);
        var smaller = Math.Min(glyph.Height, _top - _bottom);
        return smaller <= 0 ? overlap >= 0 : overlap >= smaller * OverlapRatio;
    }

    public void Add(Glyph glyph)
    {
        // The line's baseline and scale are taken from its largest glyph so far, never from a
        // running mean. A mean drifts towards whichever glyphs happen to be numerous, and a line
        // opened by a superscript would keep that raised baseline as its own.
        if (!_open || glyph.Scale > _scale)
        {
            _baseline = glyph.Baseline;
            _scale = glyph.Scale;
        }

        _top = _open ? Math.Max(_top, glyph.Top) : glyph.Top;
        _bottom = _open ? Math.Min(_bottom, glyph.Bottom) : glyph.Bottom;
        _open = true;
        Count++;
    }

    public static Glyph Of(PdfGlyph glyph) => new(
        glyph.Baseline,
        glyph.Top,
        glyph.Bottom,
        glyph.FontSize);

    /// <summary>
    /// Splits a page's glyphs into visual lines, in the order given. The caller supplies the order
    /// because it is part of the contract: glyphs arrive sorted by baseline, so one open line is
    /// enough and the grouping cannot depend on how far back it is allowed to look.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<T>> Split<T>(
        IReadOnlyList<T> ordered, Func<T, Glyph> geometry)
    {
        var lines = new List<IReadOnlyList<T>>();
        var open = new PdfVisualLineBucket();
        List<T>? current = null;

        foreach (var item in ordered)
        {
            var glyph = geometry(item);
            if (current is null || !open.Accepts(glyph))
            {
                open = new PdfVisualLineBucket();
                current = [];
                lines.Add(current);
            }

            open.Add(glyph);
            current.Add(item);
        }

        return lines;
    }
}
