using MessagePack;

namespace DocxHeaderExtractor.Sources.Pdf;

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
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfLineTypography(
    [property: Key(0)] PdfSourceFactsVersion Version,
    [property: Key(1)] double NominalFontSize,
    [property: Key(2)] double EffectivePointSize,
    [property: Key(3)] double FontBoldFlagRatio,
    [property: Key(4)] double FontNameBoldRatio,
    [property: Key(5)] double DerivedBoldRatio,
    [property: Key(6)] string BoldEvidenceSource,
    [property: Key(7)] string FontName)
{
    /// <summary>
    /// The glyph-level statistics PDF_SOURCE_FACTS_V3 reports (computed under every version, reported only by V3).
    /// </summary>
    [Key(8)]
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
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfGlyphStatistics(
    [property: Key(0)] double DominantPointSize,
    [property: Key(1)] double MedianPointSize,
    [property: Key(2)] double MinPointSize,
    [property: Key(3)] double MaxPointSize,
    [property: Key(4)] string DominantFontName,
    [property: Key(5)] double BoldGlyphRatio,
    [property: Key(6)] double ItalicGlyphRatio)
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
