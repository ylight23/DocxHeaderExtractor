using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.PdfFonts;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

/// <summary>
/// The single coordinate policy for PDF glyphs. Every downstream stage (ordering, line grouping,
/// region cuts, word gaps, atoms and identities) reads glyph geometry only through
/// <see cref="PdfGlyph"/>, never from the parser's <c>BoundingBox</c>.
/// <para>
/// Why: for a font the PDF does not embed, PdfPig derives a glyph's bounding box from whichever font
/// the host has installed, so the same PDF yielded different boxes - and therefore different lines,
/// segments and source universes - on machines with different fonts. The glyph's baseline origin,
/// its advance and its font size come from the PDF itself and are the same everywhere, so geometry
/// is built from those alone. A document whose fonts are all embedded (or standard) keeps the parser's
/// boxes, because those already come from the PDF; <see cref="PdfFontEmbedding"/> decides per document.
/// </para>
/// </summary>
internal static class PdfGeometryPolicy
{
    /// <summary>Coordinates are snapped to this grid so a one-ulp parser difference cannot flip an ordering or a threshold.</summary>
    public const double Quantum = 0.001;

    /// <summary>Share of the font size drawn above the baseline in the font-independent glyph box.</summary>
    public const double AscentEm = 0.69;

    /// <summary>Share of the font size drawn below the baseline in the font-independent glyph box.</summary>
    public const double DescentEm = 0.03;

    /// <summary>
    /// Share of the font size each side of the advance is inset, standing in for the glyph's side
    /// bearings. The parser's word-gap thresholds were calibrated on ink-to-ink gaps, which exceed the
    /// advance-to-advance gap by about this much on both sides of a pair.
    /// </summary>
    public const double SideBearingEm = 0.05;

    /// <summary>Version of this policy; part of the source-universe identity it produces.</summary>
    public const string Version = "a99-pdf-glyph-geometry-v2-font-independent";

    public static double Snap(double value) =>
        Math.Round(value / Quantum, MidpointRounding.ToEven) * Quantum;
}

/// <summary>A PDF glyph with font-independent geometry plus the facts the pipeline still reads from the parser.</summary>
internal sealed class PdfGlyph
{
    private PdfGlyph(Letter source, double left, double right, double top, double bottom, double baseline)
    {
        Source = source;
        Left = left;
        Right = right;
        Top = top;
        Bottom = bottom;
        Baseline = baseline;
    }

    public Letter Source { get; }
    public double Left { get; }
    public double Right { get; }
    public double Top { get; }
    public double Bottom { get; }
    public double Baseline { get; }
    public double Height => Top - Bottom;

    public string Value => Source.Value;
    public double FontSize => Source.FontSize;
    public double PointSize => Source.PointSize;
    public string? FontName => Source.FontName;
    public FontDetails? FontDetails => Source.FontDetails;
    public IColor? FillColor => Source.FillColor;
    public IColor? Color => Source.Color;

    /// <summary>
    /// In <see cref="PdfGeometryMode.Parser"/> the parser's own boxes are used unchanged. In
    /// <see cref="PdfGeometryMode.FontIndependent"/> the horizontal extent is the glyph's advance along
    /// its baseline and the vertical extent a fixed em box around the baseline scaled by the font size;
    /// neither depends on the glyph outline.
    /// </summary>
    public static PdfGlyph Of(Letter letter, PdfGeometryMode mode)
    {
        ArgumentNullException.ThrowIfNull(letter);
        if (mode == PdfGeometryMode.Parser)
            return new PdfGlyph(letter, letter.BoundingBox.Left, letter.BoundingBox.Right,
                letter.BoundingBox.Top, letter.BoundingBox.Bottom, letter.StartBaseLine.Y);

        var start = letter.StartBaseLine;
        var end = letter.EndBaseLine;
        var baseline = PdfGeometryPolicy.Snap(start.Y);
        // PointSize is the size actually drawn; FontSize can be a unit size under a scaling matrix.
        var size = Math.Max(0.0, letter.PointSize > 0 ? letter.PointSize : letter.FontSize);
        var inset = size * PdfGeometryPolicy.SideBearingEm;
        var left = Math.Min(start.X, end.X);
        var right = Math.Max(start.X, end.X);
        // A glyph narrower than its two insets keeps a zero-width box at its centre.
        var width = right - left;
        var applied = Math.Min(inset, width / 2.0);
        return new PdfGlyph(
            letter,
            PdfGeometryPolicy.Snap(left + applied),
            PdfGeometryPolicy.Snap(right - applied),
            PdfGeometryPolicy.Snap(baseline + size * PdfGeometryPolicy.AscentEm),
            PdfGeometryPolicy.Snap(baseline - size * PdfGeometryPolicy.DescentEm),
            baseline);
    }
}
