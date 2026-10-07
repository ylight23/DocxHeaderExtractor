using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfStyleKeyTests
{
    [Theory]
    [InlineData(11.24, 0.5, 11)]
    [InlineData(11.26, 0.5, 11.5)]
    [InlineData(11.25, 0.5, 11)]
    [InlineData(11.75, 0.5, 12)]
    [InlineData(11.24, 0, 11.24)]
    [InlineData(11.24, -1, 11.24)]
    public void Style_identity_preserves_bucket_font_and_fill(double size, double bucket, double expected)
    {
        var line = new PdfLine(1, 100, size, "source", 0, "", 0, 10, 100, "Font-A", "fill-A");
        Assert.Equal(new PdfStyleKey(expected, "Font-A", "fill-A"), PdfStyleKey.StyleOf(line, bucket));
    }

    [Fact]
    public void Default_bucket_remains_half_a_point()
    {
        var line = new PdfLine(1, 100, 11.26, "source", 0, "", 0, 10, 100, "Font-A", "fill-A");
        Assert.Equal(PdfStyleKey.StyleOf(line, 0.5), PdfStyleKey.StyleOf(line));
    }
}
