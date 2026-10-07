using UglyToad.PdfPig;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig.Content;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

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

    /// <summary>
    /// What the glyphs say about type, each fact with its origin, whichever version fed
    /// <see cref="FontSize"/> and <see cref="BoldRatio"/>. Null on a line built from a string.
    /// </summary>
    public PdfLineTypography? Typography { get; init; }
}

/// <summary>
/// The typography facts a PDF line reports. One version: the id travels in every request's style
/// facts, so it stays named rather than implied.
/// </summary>
internal enum PdfSourceFactsVersion
{
    /// <summary>
    /// PDF_SOURCE_FACTS_V3: per-glyph effective point sizes summarized robustly. A line's size is its
    /// dominant effective size - the size carrying most of its characters - not their mean. The line
    /// also reports the median, minimum and maximum size, its dominant font and its bold and italic
    /// glyph ratios. Weight is the font's declared flag or, where the font declares none, the style
    /// its name states.
    /// </summary>
    V3_RobustGlyphStatistics,
}

internal static class PdfSourceFactsVersions
{
    /// <summary>The version every PDF is built with.</summary>
    public const PdfSourceFactsVersion Current = PdfSourceFactsVersion.V3_RobustGlyphStatistics;

    public static string Id(PdfSourceFactsVersion version) => version switch
    {
        PdfSourceFactsVersion.V3_RobustGlyphStatistics => "PDF_SOURCE_FACTS_V3",
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };
}

/// <summary>
/// A line's typography with the origin of each fact, so an audit can tell a size read from the text
/// matrix from one read from the font operator, and a weight the font declares from one its name states.
/// Ratios are over the line's characters, the same weighting <see cref="PdfLine.BoldRatio"/> uses.
/// </summary>
internal sealed record PdfLineTypography(
    PdfSourceFactsVersion Version,
    double NominalFontSize,
    double EffectivePointSize,
    double FontBoldFlagRatio,
    double FontNameBoldRatio,
    double DerivedBoldRatio,
    string BoldEvidenceSource,
    string FontName)
{
    /// <summary>
    /// The glyph-level statistics PDF_SOURCE_FACTS_V3 reports (computed under every version, reported only by V3).
    /// </summary>
    public PdfGlyphStatistics? Glyphs { get; init; }

    public const string FromFontDetails = "FONT_DETAILS";
    public const string FromFontName = "FONT_NAME";
    public const string FromBoth = "FONT_DETAILS+FONT_NAME";
    public const string None = "NONE";

    /// <summary>
    /// Whether a font's name states a bold style: a weight word in the style part of a PostScript name
    /// ("Times-Bold", "Arial,BoldItalic", "SegoeUI-Semibold", "ArialBoldMT"). A generic fallback for fonts
    /// that declare no weight - the standard 14 fonts carry no descriptor at all.
    /// </summary>
    public static bool NameStatesBold(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName)) return false;
        var name = fontName;
        var plus = name.IndexOf('+');
        if (plus >= 0) name = name[(plus + 1)..];
        name = name.ToLowerInvariant();
        if (name.Contains("bold", StringComparison.Ordinal)) return true;
        var styleStart = name.IndexOfAny(['-', ',']);
        if (styleStart < 0) return false;
        var style = name[(styleStart + 1)..];
        return style.Contains("black", StringComparison.Ordinal) ||
               style.Contains("heavy", StringComparison.Ordinal) ||
               style.Contains("demi", StringComparison.Ordinal);
    }
}

/// <summary>
/// A line's glyphs summarized over their characters (spaces excluded): the dominant effective size (the size
/// carrying most characters, the smaller on a tie), the median, the smallest and the largest, the font carrying
/// most characters, and the share of characters in a bold weight (declared or named, as V2) and in italic.
/// Source observations only: nothing here says what a size means.
/// </summary>
internal sealed record PdfGlyphStatistics(
    double DominantPointSize,
    double MedianPointSize,
    double MinPointSize,
    double MaxPointSize,
    string DominantFontName,
    double BoldGlyphRatio,
    double ItalicGlyphRatio)
{
    /// <summary>Glyph sizes are compared at a tenth of a point: finer differences are rendering noise.</summary>
    public static double Round(double size) => Math.Round(size, 1);

    public static PdfGlyphStatistics Of(IReadOnlyList<(double Size, string Font, bool Bold, bool Italic)> characters)
    {
        if (characters.Count == 0) return new PdfGlyphStatistics(0, 0, 0, 0, "", 0, 0);
        var sizes = characters.Select(c => Round(c.Size)).ToArray();
        var dominant = sizes.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var ordered = sizes.Order().ToArray();
        var font = characters.Where(c => c.Font.Length > 0).GroupBy(c => c.Font)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault() ?? "";
        return new PdfGlyphStatistics(
            dominant,
            ordered[ordered.Length / 2],
            ordered[0],
            ordered[^1],
            font,
            characters.Count(c => c.Bold) / (double)characters.Count,
            characters.Count(c => c.Italic) / (double)characters.Count);
    }
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

internal static class PdfLineExtraction
{
    /// <summary>
    /// Glyphs gathered into visual lines by baseline and vertical overlap, each row then cut where a
    /// vertical corridor divides it into separate regions; typography under
    /// <see cref="PdfSourceFactsVersions.Current"/>.
    /// </summary>
    public static IReadOnlyList<PdfLine> ExtractLines(PdfDocument doc)
    {
        const PdfSourceFactsVersion facts = PdfSourceFactsVersions.Current;
        var lines = new List<PdfLine>();
        // Decided once, from the PDF's own font dictionaries: PdfGlyph is the only source of coordinates.
        var geometry = PdfFontEmbedding.ModeFor(doc);
        foreach (var page in doc.GetPages())
        {
            var visible = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => PdfGlyph.Of(l, geometry));

            // Sorted by baseline, so every glyph of one line arrives together and a single open
            // bucket is enough. Coordinates are snapped, and the last tie-break is the glyph value,
            // so the order does not depend on parser noise or on the fonts installed on the host.
            IReadOnlyList<PdfGlyph> letters = visible
                .OrderByDescending(l => l.Baseline)
                .ThenBy(l => l.Left)
                .ThenBy(l => l.Value, StringComparer.Ordinal)
                .ToList();

            var buckets = Segment(PdfVisualLineBucket.Split(letters, PdfVisualLineBucket.Of).ToList(), page);

            foreach (var bucket in buckets)
            {
                var ordered = bucket.OrderBy(l => l.Left).ToList();
                var pieces = new List<string>();
                var matchPieces = new List<string>();
                // Built while the glyphs are in hand. Deriving it afterwards from the two strings
                // would be guessing at an alignment that is known exactly here.
                var spanMap = new List<PdfVerbatimSpanMapEntry>();
                var rawLength = 0;
                var verbatimLength = 0;
                var glyphOrdinal = 0;
                var boldFlags = new List<bool>();
                var fontBoldFlags = new List<bool>();
                var nameBoldFlags = new List<bool>();
                var italicFlags = new List<bool>();
                var fontNames = new List<string>();
                var fillColors = new List<string>();
                var glyphCharacters = new List<(double Size, string Font, bool Bold, bool Italic)>();
                PdfGlyph? previous = null;
                foreach (var letter in ordered)
                {
                    if (previous is not null)
                    {
                        var gap = letter.Left - previous.Right;
                        if (gap > Math.Max(1.2, Math.Max(previous.FontSize, previous.Height) * 0.18))
                        {
                            rawLength += 1;
                            pieces.Add(" ");
                            boldFlags.Add(boldFlags.Count > 0 && boldFlags[^1]);
                            fontBoldFlags.Add(fontBoldFlags.Count > 0 && fontBoldFlags[^1]);
                            nameBoldFlags.Add(nameBoldFlags.Count > 0 && nameBoldFlags[^1]);
                            italicFlags.Add(italicFlags.Count > 0 && italicFlags[^1]);
                            fontNames.Add(fontNames.Count > 0 ? fontNames[^1] : "");
                            fillColors.Add(fillColors.Count > 0 ? fillColors[^1] : "");
                        }
                        if (IsMatchWordGapForAudit(gap, previous.FontSize, previous.Height))
                        {
                            verbatimLength += 1;
                            matchPieces.Add(" ");
                        }
                    }

                    spanMap.Add(new PdfVerbatimSpanMapEntry(
                        verbatimLength, letter.Value.Length,
                        rawLength, letter.Value.Length,
                        glyphOrdinal++, page.Number,
                        letter.Left, letter.Right,
                        letter.Bottom, letter.Top));
                    verbatimLength += letter.Value.Length;
                    rawLength += letter.Value.Length;
                    pieces.Add(letter.Value);
                    matchPieces.Add(letter.Value);
                    var fontName = NormalizeFontName(letter.FontName ?? letter.FontDetails?.Name ?? "");
                    var fillColor = ColorKey(letter.FillColor ?? letter.Color);
                    var declaredBold = letter.FontDetails?.IsBold ?? false;
                    var namedBold = PdfLineTypography.NameStatesBold(letter.FontName ?? letter.FontDetails?.Name);
                    foreach (var _ in letter.Value)
                    {
                        glyphCharacters.Add((letter.PointSize, fontName, declaredBold || namedBold, letter.FontDetails?.IsItalic ?? false));
                        boldFlags.Add(declaredBold);
                        fontBoldFlags.Add(declaredBold);
                        nameBoldFlags.Add(namedBold);
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

                var derivedFlags = fontBoldFlags.Zip(nameBoldFlags, (declared, named) => declared || named).ToList();
                var typography = new PdfLineTypography(
                    facts,
                    ordered.Average(l => l.FontSize),
                    ordered.Average(l => l.PointSize),
                    Ratio(fontBoldFlags),
                    Ratio(nameBoldFlags),
                    Ratio(derivedFlags),
                    BoldEvidenceSource(fontBoldFlags, nameBoldFlags),
                    Dominant(fontNames))
                {
                    Glyphs = PdfGlyphStatistics.Of(glyphCharacters),
                };
                boldFlags = derivedFlags;
                var size = typography.Glyphs!.DominantPointSize;

                var boldRatio = Ratio(boldFlags);
                var italicRatio = italicFlags.Count == 0 ? 0.0 : italicFlags.Count(b => b) / (double)italicFlags.Count;
                var leadingBoldLen = 0;
                while (leadingBoldLen < boldFlags.Count && boldFlags[leadingBoldLen]) leadingBoldLen++;
                var leadingBoldPrefix = leadingBoldLen > 0 && leadingBoldLen < raw.Length
                    ? NormalizeSpace(raw[..leadingBoldLen])
                    : "";

                lines.Add(new PdfLine(
                    page.Number,
                    ordered.Average(MidY),
                    size,
                    text,
                    boldRatio,
                    leadingBoldPrefix,
                    italicRatio,
                    ordered.Min(l => l.Left),
                    ordered.Max(l => l.Right),
                    Dominant(fontNames),
                    Dominant(fillColors),
                    canonicalMatch,
                    matchText,
                    ordered.Min(l => l.Bottom),
                    ordered.Max(l => l.Top))
                {
                    Projection = new PdfSourceTextProjection(
                        raw, string.Concat(matchPieces), spanMap, PdfSourceTextProjection.CurrentVersion),
                    Typography = typography,
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
    private static List<IReadOnlyList<PdfGlyph>> Segment(
        List<IReadOnlyList<PdfGlyph>> rows, UglyToad.PdfPig.Content.Page page)
    {
        var ordered = rows
            .Select(row => row.OrderBy(l => l.Left).ToArray())
            .ToArray();

        // The page's own word space, taken from the gaps the projection already treats as spaces.
        var spaces = new List<double>();
        foreach (var row in ordered)
            for (var index = 1; index < row.Length; index++)
            {
                var previous = row[index - 1];
                var gap = row[index].Left - previous.Right;
                if (IsMatchWordGapForAudit(gap, previous.FontSize, previous.Height))
                    spaces.Add(gap);
            }

        var cuts = PdfVisualRegion.Cuts(
            ordered.Select(row => (IReadOnlyList<PdfVisualRegion.Box>)row
                .Select(l => new PdfVisualRegion.Box(l.Left, l.Right))
                .ToArray()).ToArray(),
            PdfVisualRegion.WordGap(spaces));

        var segments = new List<IReadOnlyList<PdfGlyph>>(rows.Count);
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

    private static double Ratio(IReadOnlyList<bool> flags) =>
        flags.Count == 0 ? 0.0 : flags.Count(b => b) / (double)flags.Count;

    /// <summary>Which evidence made the line's bold characters bold.</summary>
    private static string BoldEvidenceSource(IReadOnlyList<bool> declared, IReadOnlyList<bool> named)
    {
        var fromDetails = declared.Any(b => b);
        var fromNameOnly = named.Where((b, i) => b && !declared[i]).Any();
        return fromDetails && fromNameOnly ? PdfLineTypography.FromBoth
            : fromDetails ? PdfLineTypography.FromFontDetails
            : fromNameOnly ? PdfLineTypography.FromFontName
            : PdfLineTypography.None;
    }

    private static double MidY(PdfGlyph l) => (l.Bottom + l.Top) / 2.0;

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
