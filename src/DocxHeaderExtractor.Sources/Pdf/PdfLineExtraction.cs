using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Sources.Pdf;

internal static class PdfLineExtraction
{
    /// <summary>
    /// Glyphs gathered into visual lines by baseline and vertical overlap, each row then cut where a
    /// vertical corridor divides it into separate regions; typography under
    /// <see cref="PdfSourceFactsVersions.Current"/>.
    /// </summary>
    public static IReadOnlyList<PdfLine> ExtractLines(PdfDocument doc)
    {
        // Decided once, from the PDF's own font dictionaries: PdfGlyph is the only source of coordinates.
        var geometry = PdfFontEmbedding.ModeFor(doc);
        // PdfPig reads the pages one at a time, on this thread. The glyph work of each page does not depend on the others, so it runs in parallel.
        var pages = doc.GetPages()
            .Select(page => (page.Number, Letters: page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToArray()))
            .ToArray();
        var perPage = new List<PdfLine>[pages.Length];
        Parallel.For(0, pages.Length, i => perPage[i] = LinesOfPage(pages[i].Number, pages[i].Letters, geometry));
        return perPage.SelectMany(lines => lines).ToList();
    }

    /// <summary>The lines of one page, from its visible letters. Every cache is local to the page, so pages can run in parallel.</summary>
    private static List<PdfLine> LinesOfPage(int pageNumber, UglyToad.PdfPig.Content.Letter[] pageLetters, PdfGeometryMode geometry)
    {
        const PdfSourceFactsVersion facts = PdfSourceFactsVersions.Current;
        var lines = new List<PdfLine>();
        // Per-glyph font and colour facts are pure functions of a font name or a colour object, and a page has a handful of each.
        var fontNameCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var boldNameCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var colorKeyCache = new Dictionary<UglyToad.PdfPig.Graphics.Colors.IColor, string>(ReferenceEqualityComparer.Instance);
        var visible = pageLetters.Select(l => PdfGlyph.Of(l, geometry));

        // Sorted by baseline, so every glyph of one line arrives together and a single open
        // bucket is enough. Coordinates are snapped, and the last tie-break is the glyph value,
        // so the order does not depend on parser noise or on the fonts installed on the host.
        IReadOnlyList<PdfGlyph> letters = ReadingOrder(visible);

        var buckets = Segment(PdfVisualLineBucket.Split(letters, PdfVisualLineBucket.Of).ToList());

        foreach (var bucket in buckets)
        {
            var ordered = bucket.OrderBy(l => l.Left).ToList();
            var pieces = new List<string>();
            var matchPieces = new List<string>();
            // Built while the glyphs are in hand. Deriving it afterwards from the two strings
            // would be guessing at an alignment that is known exactly here.
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
                        matchPieces.Add(" ");
                    }
                }

                pieces.Add(letter.Value);
                matchPieces.Add(letter.Value);
                var rawFont = letter.FontName ?? letter.FontDetails?.Name ?? "";
                if (!fontNameCache.TryGetValue(rawFont, out var fontName)) fontNameCache[rawFont] = fontName = NormalizeFontName(rawFont);
                var fillColor = CachedColorKey(letter.FillColor ?? letter.Color, colorKeyCache);
                var declaredBold = letter.FontDetails?.IsBold ?? false;
                if (!boldNameCache.TryGetValue(rawFont, out var namedBold)) boldNameCache[rawFont] = namedBold = PdfLineTypography.NameStatesBold(rawFont);
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
                pageNumber,
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
                    raw, string.Concat(matchPieces), PdfSourceTextProjection.CurrentVersion),
                Typography = typography,
            });
        }
        return lines;
    }

    /// <summary>
    /// Divides each reconstructed row wherever a vertical corridor separates two regions of the
    /// page. The row order is kept and each row's segments follow it left to right, so the page
    /// still reads top to bottom and the glyphs of a row stay together and in order.
    /// </summary>
    private static List<IReadOnlyList<PdfGlyph>> Segment(
        List<IReadOnlyList<PdfGlyph>> rows)
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

    /// <summary>
    /// The glyphs by baseline from the top, then left to right, then by value. The keys are read once into arrays and the sort
    /// breaks every tie by input position, so the order is the one a stable LINQ sort gives, without a delegate call per comparison.
    /// </summary>
    internal static IReadOnlyList<PdfGlyph> ReadingOrder(IEnumerable<PdfGlyph> glyphs)
    {
        var items = glyphs as PdfGlyph[] ?? glyphs.ToArray();
        var baseline = new double[items.Length];
        var left = new double[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            baseline[i] = items[i].Baseline;
            left[i] = items[i].Left;
        }
        var order = new int[items.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var c = baseline[b].CompareTo(baseline[a]);
            if (c != 0) return c;
            c = left[a].CompareTo(left[b]);
            if (c != 0) return c;
            c = string.CompareOrdinal(items[a].Value, items[b].Value);
            return c != 0 ? c : a.CompareTo(b);
        });
        var result = new PdfGlyph[items.Length];
        for (var i = 0; i < order.Length; i++) result[i] = items[order[i]];
        return result;
    }

    private static string CachedColorKey(UglyToad.PdfPig.Graphics.Colors.IColor? color, Dictionary<UglyToad.PdfPig.Graphics.Colors.IColor, string> cache)
    {
        if (color is null) return "";
        if (!cache.TryGetValue(color, out var key)) cache[color] = key = ColorKey(color);
        return key;
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
