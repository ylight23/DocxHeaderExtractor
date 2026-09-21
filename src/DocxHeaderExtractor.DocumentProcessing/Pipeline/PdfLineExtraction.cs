using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Dòng PDF dựng lại từ letter theo toạ độ Y (không phải dòng logic OOXML). <see cref="BoldRatio"/>
/// và <see cref="LeadingBoldPrefix"/> chỉ có ý nghĩa khi bộ gọi cần tín hiệu bold — hai bộ dựng
/// heading khác nhau (font-size cho textbook, bold-run-in cho biên bản/minutes) đọc chung một lượt
/// quét letter vì cùng một thuật toán bucket-theo-Y/khoảng-cách, chỉ khác tín hiệu dùng để quyết heading.
/// </summary>
internal sealed record PdfLine(
    int Page, double Y, double FontSize, string Text, double BoldRatio, string LeadingBoldPrefix,
    double ItalicRatio, double Left, double Right, string FontName, string FillColorKey,
    string? CanonicalMatchText = null, string? MatchText = null,
    double? Bottom = null, double? Top = null)
{
    private readonly PdfSourceTextProjection? _projection;

    /// <summary>
    /// The canonical text of this line and the way back to its glyphs. <see cref="Text"/> stays the
    /// raw parser concatenation for audit; the projection is what the model and the binder use.
    /// <para>
    /// A line built from a string rather than from glyphs has no reconstruction to declare, so it
    /// falls back to itself. That keeps the text intact instead of blanking it, and the empty span
    /// map says plainly that no glyph provenance is available.
    /// </para>
    /// </summary>
    public PdfSourceTextProjection Projection
    {
        get => _projection ?? PdfSourceTextProjection.Identity(Text);
        init => _projection = value;
    }
}

/// <summary>How glyphs are gathered into a line.</summary>
internal enum PdfLineGrouping
{
    /// <summary>
    /// Proximity of vertical midpoints. The behaviour every frozen source universe was built with,
    /// and the active default until a migration says otherwise.
    /// </summary>
    MidpointV1,

    /// <summary>
    /// Baseline compatibility plus vertical overlap. See <see cref="PdfVisualLineBucket"/>.
    /// </summary>
    VisualLineV2,

    /// <summary>
    /// <see cref="VisualLineV2"/>'s rows, then cut where a vertical corridor divides one row into
    /// separate regions. See <see cref="PdfVisualRegion"/>.
    /// </summary>
    VisualLineSegmentV3,
}

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

    public static Glyph Of(Letter letter) => new(
        letter.StartBaseLine.Y,
        letter.BoundingBox.Top,
        letter.BoundingBox.Bottom,
        letter.FontSize);

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

internal static class PdfLineExtraction
{
    public static IReadOnlyList<PdfLine> ExtractLines(
        PdfDocument doc, PdfLineGrouping grouping = PdfLineGrouping.MidpointV1)
    {
        var lines = new List<PdfLine>();
        foreach (var page in doc.GetPages())
        {
            var visible = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value));

            // V2 sorts by baseline, so every glyph of one line arrives together and a single open
            // bucket is enough; its last tie-break is the glyph itself, so the order is total and
            // one document cannot produce two universes. V1 keeps the midpoint order its tolerance
            // is measured against, down to the tie-breaks, because every frozen universe hash was
            // taken over exactly this sequence.
            var byBaseline = grouping is PdfLineGrouping.VisualLineV2 or PdfLineGrouping.VisualLineSegmentV3;
            IReadOnlyList<Letter> letters = byBaseline
                ? visible
                    .OrderByDescending(l => l.StartBaseLine.Y)
                    .ThenBy(l => l.BoundingBox.Left)
                    .ThenBy(l => l.Value, StringComparer.Ordinal)
                    .ToList()
                : visible
                    .OrderByDescending(MidY)
                    .ThenBy(l => l.BoundingBox.Left)
                    .ToList();

            var buckets = new List<IReadOnlyList<Letter>>();
            if (byBaseline)
            {
                buckets.AddRange(PdfVisualLineBucket.Split(letters, PdfVisualLineBucket.Of));
            }
            else
            {
                List<Letter>? current = null;
                double currentY = 0;
                foreach (var letter in letters)
                {
                    var y = MidY(letter);
                    var tolerance = Math.Max(1.5, Math.Max(letter.FontSize, letter.BoundingBox.Height) * 0.30);
                    if (current is null || Math.Abs(currentY - y) > tolerance)
                    {
                        current = [];
                        buckets.Add(current);
                        currentY = y;
                    }
                    else
                    {
                        currentY = ((currentY * current.Count) + y) / (current.Count + 1);
                    }
                    current.Add(letter);
                }
            }

            if (grouping == PdfLineGrouping.VisualLineSegmentV3)
                buckets = Segment(buckets, page);

            foreach (var bucket in buckets)
            {
                var ordered = bucket.OrderBy(l => l.BoundingBox.Left).ToList();
                var pieces = new List<string>();
                var matchPieces = new List<string>();
                // Built while the glyphs are in hand. Deriving it afterwards from the two strings
                // would be guessing at an alignment that is known exactly here.
                var spanMap = new List<PdfVerbatimSpanMapEntry>();
                var rawLength = 0;
                var verbatimLength = 0;
                var glyphOrdinal = 0;
                var boldFlags = new List<bool>();
                var italicFlags = new List<bool>();
                var fontNames = new List<string>();
                var fillColors = new List<string>();
                Letter? previous = null;
                foreach (var letter in ordered)
                {
                    if (previous is not null)
                    {
                        var gap = letter.BoundingBox.Left - previous.BoundingBox.Right;
                        if (gap > Math.Max(1.2, Math.Max(previous.FontSize, previous.BoundingBox.Height) * 0.18))
                        {
                            rawLength += 1;
                            pieces.Add(" ");
                            boldFlags.Add(boldFlags.Count > 0 && boldFlags[^1]);
                            italicFlags.Add(italicFlags.Count > 0 && italicFlags[^1]);
                            fontNames.Add(fontNames.Count > 0 ? fontNames[^1] : "");
                            fillColors.Add(fillColors.Count > 0 ? fillColors[^1] : "");
                        }
                        if (IsMatchWordGapForAudit(gap, previous.FontSize, previous.BoundingBox.Height))
                        {
                            verbatimLength += 1;
                            matchPieces.Add(" ");
                        }
                    }

                    spanMap.Add(new PdfVerbatimSpanMapEntry(
                        verbatimLength, letter.Value.Length,
                        rawLength, letter.Value.Length,
                        glyphOrdinal++, page.Number,
                        letter.BoundingBox.Left, letter.BoundingBox.Right,
                        letter.BoundingBox.Bottom, letter.BoundingBox.Top));
                    verbatimLength += letter.Value.Length;
                    rawLength += letter.Value.Length;
                    pieces.Add(letter.Value);
                    matchPieces.Add(letter.Value);
                    var fontName = NormalizeFontName(letter.FontName ?? letter.FontDetails?.Name ?? "");
                    var fillColor = ColorKey(letter.FillColor ?? letter.Color);
                    foreach (var _ in letter.Value)
                    {
                        boldFlags.Add(letter.FontDetails?.IsBold ?? false);
                        italicFlags.Add(letter.FontDetails?.IsItalic ?? false);
                        fontNames.Add(fontName);
                        fillColors.Add(fillColor);
                    }
                    previous = letter;
                }

                var raw = string.Concat(pieces);
                var text = NormalizeSpace(raw);
                var matchText = NormalizeSpace(string.Concat(matchPieces));
                var canonicalMatch = PdfTextUtilities.CanonicalForMatch(matchText);
                if (text.Length == 0) continue;

                var boldRatio = boldFlags.Count == 0 ? 0.0 : boldFlags.Count(b => b) / (double)boldFlags.Count;
                var italicRatio = italicFlags.Count == 0 ? 0.0 : italicFlags.Count(b => b) / (double)italicFlags.Count;
                var leadingBoldLen = 0;
                while (leadingBoldLen < boldFlags.Count && boldFlags[leadingBoldLen]) leadingBoldLen++;
                var leadingBoldPrefix = leadingBoldLen > 0 && leadingBoldLen < raw.Length
                    ? NormalizeSpace(raw[..leadingBoldLen])
                    : "";

                lines.Add(new PdfLine(
                    page.Number,
                    ordered.Average(MidY),
                    ordered.Average(l => l.FontSize),
                    text,
                    boldRatio,
                    leadingBoldPrefix,
                    italicRatio,
                    ordered.Min(l => l.BoundingBox.Left),
                    ordered.Max(l => l.BoundingBox.Right),
                    Dominant(fontNames),
                    Dominant(fillColors),
                    canonicalMatch,
                    matchText,
                    ordered.Min(l => l.BoundingBox.Bottom),
                    ordered.Max(l => l.BoundingBox.Top))
                {
                    Projection = new PdfSourceTextProjection(
                        raw, string.Concat(matchPieces), spanMap, PdfSourceTextProjection.CurrentVersion),
                });
            }
        }
        return lines;
    }

    /// <summary>
    /// Divides each reconstructed row wherever a vertical corridor separates two regions of the
    /// page. The row order is kept and each row's segments follow it left to right, so the page
    /// still reads top to bottom and the glyphs of a row stay together and in order.
    /// </summary>
    private static List<IReadOnlyList<Letter>> Segment(
        List<IReadOnlyList<Letter>> rows, UglyToad.PdfPig.Content.Page page)
    {
        var ordered = rows
            .Select(row => row.OrderBy(l => l.BoundingBox.Left).ToArray())
            .ToArray();

        // The page's own word space, taken from the gaps the projection already treats as spaces.
        var spaces = new List<double>();
        foreach (var row in ordered)
            for (var index = 1; index < row.Length; index++)
            {
                var previous = row[index - 1];
                var gap = row[index].BoundingBox.Left - previous.BoundingBox.Right;
                if (IsMatchWordGapForAudit(gap, previous.FontSize, previous.BoundingBox.Height))
                    spaces.Add(gap);
            }

        var cuts = PdfVisualRegion.Cuts(
            ordered.Select(row => (IReadOnlyList<PdfVisualRegion.Box>)row
                .Select(l => new PdfVisualRegion.Box(l.BoundingBox.Left, l.BoundingBox.Right))
                .ToArray()).ToArray(),
            PdfVisualRegion.WordGap(spaces));

        var segments = new List<IReadOnlyList<Letter>>(rows.Count);
        for (var row = 0; row < ordered.Length; row++)
        {
            var start = 0;
            foreach (var cut in cuts[row])
            {
                segments.Add(ordered[row][start..(cut + 1)]);
                start = cut + 1;
            }

            segments.Add(ordered[row][start..]);
        }

        return segments;
    }

    private static double MidY(Letter l) => (l.BoundingBox.Bottom + l.BoundingBox.Top) / 2.0;

    internal static bool IsMatchWordGapForAudit(double gap, double fontSize, double glyphHeight) =>
        gap > Math.Max(1.8, Math.Max(fontSize, glyphHeight) * 0.27);

    private static string NormalizeFontName(string fontName)
    {
        var name = fontName;
        var plus = name.IndexOf('+');
        if (plus >= 0 && plus + 1 < name.Length) name = name[(plus + 1)..];
        return name.Trim().ToLowerInvariant();
    }

    private static string ColorKey(UglyToad.PdfPig.Graphics.Colors.IColor? color)
    {
        if (color is null) return "";
        try
        {
            var (r, g, b) = color.ToRGBValues();
            return $"{Math.Round(r, 2):0.00},{Math.Round(g, 2):0.00},{Math.Round(b, 2):0.00}";
        }
        catch
        {
            return color.ColorSpace.ToString();
        }
    }

    private static string Dominant(IReadOnlyList<string> values) =>
        values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "";

    private static string NormalizeSpace(string text)
    {
        var trimmed = text.Trim();
        var result = new System.Text.StringBuilder(trimmed.Length);
        var lastWasSpace = false;
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) result.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                result.Append(c);
                lastWasSpace = false;
            }
        }
        return result.ToString();
    }
}
