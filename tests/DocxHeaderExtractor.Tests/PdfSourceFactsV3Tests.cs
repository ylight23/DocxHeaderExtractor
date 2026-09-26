using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PDF_SOURCE_FACTS_V3 on a synthetic PDF: a two-line title in simulated small caps - each word's initial drawn at
/// 18pt, the rest at 13.5pt, both through the text matrix - over 10pt body. Under V2 a line's size is the mean of its
/// glyphs, so the two lines of one title read as different sizes (they have different shares of initials); under V3
/// both read 13.5 dominant, 18 max. Text and geometry do not move between versions.
/// </summary>
public sealed class PdfSourceFactsV3Tests
{
    [Fact]
    public void Small_caps_lines_of_one_title_differ_under_v2_mean()
    {
        var lines = Lines(PdfSourceFactsVersion.V2_EffectivePointSize);
        var (first, second) = (lines[0], lines[1]);
        Assert.NotEqual(Math.Round(first.FontSize, 1), Math.Round(second.FontSize, 1));
    }

    [Fact]
    public void Small_caps_lines_of_one_title_read_one_dominant_size_under_v3()
    {
        var lines = Lines(PdfSourceFactsVersion.V3_RobustGlyphStatistics);
        var (first, second, body) = (lines[0], lines[1], lines[2]);
        Assert.Equal(13.5, first.FontSize, 1);
        Assert.Equal(13.5, second.FontSize, 1);
        Assert.Equal(10.0, body.FontSize, 1);

        var glyphs = first.Typography!.Glyphs!;
        Assert.Equal(13.5, glyphs.DominantPointSize, 1);
        Assert.Equal(13.5, glyphs.MedianPointSize, 1);
        Assert.Equal(13.5, glyphs.MinPointSize, 1);
        Assert.Equal(18.0, glyphs.MaxPointSize, 1);
        Assert.Equal("times-bold", glyphs.DominantFontName);
        Assert.Equal(1.0, glyphs.BoldGlyphRatio, 3);
        Assert.Equal(0.0, glyphs.ItalicGlyphRatio, 3);
        Assert.Equal(0.0, body.Typography!.Glyphs!.BoldGlyphRatio, 3);
        Assert.Equal(1.0, first.BoldRatio, 3);
    }

    [Fact]
    public void V3_moves_no_text_and_no_geometry()
    {
        static object Geometry(PdfLine l) => (l.Page, l.Y, l.Text, l.Left, l.Right, l.Bottom, l.Top, l.Projection.VerbatimText, l.Projection.SpanMap.Count);
        Assert.Equal(Lines(PdfSourceFactsVersion.V2_EffectivePointSize).Select(Geometry), Lines(PdfSourceFactsVersion.V3_RobustGlyphStatistics).Select(Geometry));
        Assert.Equal(Lines(PdfSourceFactsVersion.V1_NominalFontSize).Select(Geometry), Lines(PdfSourceFactsVersion.V3_RobustGlyphStatistics).Select(Geometry));
    }

    [Fact]
    public void V2_is_unchanged_by_v3()
    {
        // V2 still reports the mean of the glyphs' effective sizes (the frozen PDF_SOURCE_FACTS_V2 definition).
        foreach (var line in Lines(PdfSourceFactsVersion.V2_EffectivePointSize))
            Assert.Equal(line.Typography!.EffectivePointSize, line.FontSize, 6);
    }

    private static IReadOnlyList<PdfLine> Lines(PdfSourceFactsVersion facts)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(SmallCapsPdf());
        return PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3, facts);
    }

    /// <summary>"STATEMENT OF SUBSCRIPTIONS TO" / "CAPITAL STOCK AND VOTING POWER" with 18pt initials, then a body line.</summary>
    private static byte[] SmallCapsPdf()
    {
        var content = new StringBuilder();
        void Line(string[] words, double y)
        {
            var x = 72.0;
            foreach (var word in words)
            {
                // The initial at 18pt, the rest at 13.5pt, both as Tf 1 scaled by Tm; a word space after each word.
                content.Append($"BT /F2 1 Tf 18 0 0 18 {x:0.##} {y:0.##} Tm ({word[0]}) Tj ET\n");
                x += 12.5;
                if (word.Length > 1)
                {
                    content.Append($"BT /F2 1 Tf 13.5 0 0 13.5 {x:0.##} {y:0.##} Tm ({word[1..]}) Tj ET\n");
                    x += (word.Length - 1) * 9.5;
                }
                x += 6;
            }
        }
        Line(["STATEMENT", "OF", "SUBSCRIPTIONS", "TO"], 700);
        Line(["CAPITAL", "STOCK", "AND", "VOTING", "POWER"], 682);
        content.Append("BT /F1 1 Tf 10 0 0 10 72 650 Tm (Body text set at ten points for the whole paragraph line.) Tj ET\n");
        var stream = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold >>",
        };
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(text.ToString()));
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
