using DocxHeaderExtractor.DocumentProcessing.Vision;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfRegionRasterizerTests
{
    private static string SamplePdf() => Path.Combine(
        "todo10_8", "heading_corpus_100", "03_tai_chinh_ke_toan",
        "052_WBG_Trust_Fund_FIS_December_2025.pdf");

    [Fact]
    public void RenderCropPng_produces_a_valid_png_sized_by_dpi_and_crop_area()
    {
        var pdf = SamplePdf();
        if (!File.Exists(pdf)) return;

        // Vùng chứa ca cha-con "Cost Recovery" thật trên trang 27 (handoff §172).
        var png = PdfRegionRasterizer.RenderCropPng(
            pdf, pageNumber1Based: 27, left: 70, bottomPdfY: 680, right: 400, topPdfY: 740, dpi: 150);

        Assert.NotEmpty(png);
        // Chữ ký PNG: 89 50 4E 47 0D 0A 1A 0A.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);

        using var image = SkiaSharp.SKBitmap.Decode(png);
        // Vùng crop 330x60 điểm PDF ở 150 DPI (150/72 điểm/inch) -> ~687x125 px, cho phép sai số làm tròn.
        Assert.InRange(image.Width, 670, 700);
        Assert.InRange(image.Height, 115, 135);
    }

    [Fact]
    public void RenderCropPng_rejects_inverted_bounds()
    {
        var pdf = SamplePdf();
        if (!File.Exists(pdf)) return;

        Assert.Throws<ArgumentException>(() =>
            PdfRegionRasterizer.RenderCropPng(pdf, 1, left: 100, bottomPdfY: 700, right: 50, topPdfY: 780));
        Assert.Throws<ArgumentException>(() =>
            PdfRegionRasterizer.RenderCropPng(pdf, 1, left: 50, bottomPdfY: 780, right: 100, topPdfY: 700));
    }

    [Fact]
    public void RenderCropPng_rejects_page_below_one()
    {
        var pdf = SamplePdf();
        if (!File.Exists(pdf)) return;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfRegionRasterizer.RenderCropPng(pdf, 0, left: 50, bottomPdfY: 700, right: 100, topPdfY: 780));
    }
}
