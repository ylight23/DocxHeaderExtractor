using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PDF_SOURCE_FACTS_V2 on synthetic PDFs: the same two lines - a 16pt Times-Bold title over 10pt Times-Roman body -
/// set once through the font operator (<c>16 Tf</c>) and once through the text matrix (<c>1 Tf</c>, scaled by
/// <c>Tm</c>). Under V1 the matrix-scaled one reads size 1 and no bold, the failure SRC-053 found; under V2 both
/// read the same typography, and in both versions the text and geometry do not move.
/// </summary>
public sealed class PdfSourceFactsV2Tests
{
    private const string Title = "Scaled Section Title";
    private const string Body = "Body text set at ten points for the whole paragraph line.";

    [Fact]
    public void Matrix_scaled_type_reads_flat_under_v1()
    {
        var lines = Lines(MatrixScaledPdf(), PdfSourceFactsVersion.V1_NominalFontSize);
        Assert.All(lines, line => Assert.Equal(1.0, line.FontSize, 3));
        Assert.All(lines, line => Assert.Equal(0.0, line.BoldRatio, 3));
    }

    [Fact]
    public void Matrix_scaled_type_reads_its_drawn_size_and_weight_under_v2()
    {
        var lines = Lines(MatrixScaledPdf(), PdfSourceFactsVersion.V2_EffectivePointSize);
        var title = lines.Single(l => l.Text == Title);
        var body = lines.Single(l => l.Text == Body);
        Assert.Equal(16.0, title.FontSize, 1);
        Assert.Equal(10.0, body.FontSize, 1);
        Assert.Equal(1.0, title.BoldRatio, 3);
        Assert.Equal(0.0, body.BoldRatio, 3);

        // Every fact keeps its origin: the size came from the matrix, the weight from the font's name.
        var typography = title.Typography!;
        Assert.Equal(PdfSourceFactsVersion.V2_EffectivePointSize, typography.Version);
        Assert.Equal(1.0, typography.NominalFontSize, 3);
        Assert.Equal(16.0, typography.EffectivePointSize, 1);
        Assert.Equal(0.0, typography.FontBoldFlagRatio, 3);
        Assert.Equal(1.0, typography.FontNameBoldRatio, 3);
        Assert.Equal(PdfLineTypography.FromFontName, typography.BoldEvidenceSource);
        Assert.Equal("times-bold", typography.FontName);
        Assert.Equal(PdfLineTypography.None, body.Typography!.BoldEvidenceSource);
    }

    [Fact]
    public void Operator_sized_type_reads_the_same_size_under_both_versions()
    {
        var v1 = Lines(OperatorSizedPdf(), PdfSourceFactsVersion.V1_NominalFontSize);
        var v2 = Lines(OperatorSizedPdf(), PdfSourceFactsVersion.V2_EffectivePointSize);
        Assert.Equal(v1.Select(l => Math.Round(l.FontSize, 3)), v2.Select(l => Math.Round(l.FontSize, 3)));
        Assert.Equal(16.0, v2.Single(l => l.Text == Title).FontSize, 1);
    }

    [Fact]
    public void Both_settings_of_the_same_type_read_alike_under_v2()
    {
        var matrix = Lines(MatrixScaledPdf(), PdfSourceFactsVersion.V2_EffectivePointSize);
        var sized = Lines(OperatorSizedPdf(), PdfSourceFactsVersion.V2_EffectivePointSize);
        Assert.Equal(sized.Select(l => (l.Text, Math.Round(l.FontSize, 1), l.BoldRatio >= 0.5)),
            matrix.Select(l => (l.Text, Math.Round(l.FontSize, 1), l.BoldRatio >= 0.5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_version_moves_no_text_and_no_geometry(bool segmented)
    {
        var grouping = segmented ? PdfLineGrouping.VisualLineSegmentV3 : PdfLineGrouping.MidpointV1;
        foreach (var pdf in new[] { MatrixScaledPdf(), OperatorSizedPdf() })
        {
            var v1 = Lines(pdf, PdfSourceFactsVersion.V1_NominalFontSize, grouping);
            var v2 = Lines(pdf, PdfSourceFactsVersion.V2_EffectivePointSize, grouping);
            Assert.Equal(v1.Select(Geometry), v2.Select(Geometry));
        }

        static object Geometry(PdfLine l) => (l.Page, l.Y, l.Text, l.Left, l.Right, l.Bottom, l.Top, l.Projection.VerbatimText, l.Projection.SpanMap.Count);
    }

    [Theory]
    [InlineData("Times-Bold", true)]
    [InlineData("ABCDEF+Arial-BoldMT", true)]
    [InlineData("Arial,BoldItalic", true)]
    [InlineData("SegoeUI-Semibold", true)]
    [InlineData("ArialBoldMT", true)]
    [InlineData("Helvetica-Black", true)]
    [InlineData("Times-Roman", false)]
    [InlineData("Times-Italic", false)]
    [InlineData("Blackadder", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_font_name_states_bold_only_in_its_style(string? name, bool bold) =>
        Assert.Equal(bold, PdfLineTypography.NameStatesBold(name));

    private static IReadOnlyList<PdfLine> Lines(
        byte[] pdf, PdfSourceFactsVersion facts, PdfLineGrouping grouping = PdfLineGrouping.VisualLineSegmentV3)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return PdfLineExtraction.ExtractLines(document, grouping, facts);
    }

    private static byte[] MatrixScaledPdf() => Pdf(
        $"BT /F2 1 Tf 16 0 0 16 72 700 Tm ({Title}) Tj ET\nBT /F1 1 Tf 10 0 0 10 72 670 Tm ({Body}) Tj ET\n");

    private static byte[] OperatorSizedPdf() => Pdf(
        $"BT /F2 16 Tf 72 700 Td ({Title}) Tj ET\nBT /F1 10 Tf 72 670 Td ({Body}) Tj ET\n");

    /// <summary>A one-page PDF over two Times faces, with a byte-exact cross-reference table.</summary>
    private static byte[] Pdf(string content)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /FirstChar 32 /LastChar 126 /Widths 8 0 R /FontDescriptor 7 0 R >>",
            // A descriptor that declares no weight - Serif | Nonsymbolic, no ForceBold, no FontWeight - as the
            // matrix-scaled source that exposed the failure has: the flag reads not bold, the name says Bold.
            "<< /Type /FontDescriptor /FontName /Times-Bold /Flags 34 /FontBBox [-168 -218 1000 935] /ItalicAngle 0 /Ascent 683 /Descent -217 /CapHeight 676 /StemV 139 >>",
            "[" + string.Join(" ", Enumerable.Repeat("500", 95)) + "]",
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
