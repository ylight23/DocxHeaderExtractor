using DocxHeaderExtractor.Sources.Pdf;
using SkiaSharp;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfVisualCropTests
{
    // A 120-DPI letter page (1020 x 1320 px, 612 x 792 pt) with ink from row 300 to row 900.
    static SKBitmap Page(int inkTop = 300, int inkBottom = 900)
    {
        var bitmap = new SKBitmap(1020, 1320);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(new SKRect(100, inkTop, 800, inkBottom), paint);
        return bitmap;
    }

    static double Pt(int px) => 792 - px * 72d / PdfVisualSelectionImages.Dpi;

    [Fact]
    public void The_band_keeps_every_ink_row_and_box_with_the_bleed()
    {
        using var page = Page();
        // One box lies below the ink (rows 950-980): the band must reach it.
        var crop = PdfVisualSelectionImages.Crop(page, 1, [(Pt(950), Pt(980))]);
        Assert.True(crop.Verified);
        Assert.True(crop.Cropped);
        Assert.Equal(300 - PdfVisualSelectionImages.BleedPx, crop.TopPx);
        Assert.Equal(980 + PdfVisualSelectionImages.BleedPx, crop.BottomPx);
        Assert.InRange(crop.TopPt, Pt(crop.TopPx) - 0.1, Pt(crop.TopPx) + 0.1);
        Assert.True(crop.TopPt > crop.BottomPt);
    }

    [Fact]
    public void A_box_outside_the_page_keeps_the_page_whole()
    {
        using var page = Page();
        var crop = PdfVisualSelectionImages.Crop(page, 1, [(900, 880)]); // above the 792-pt page top
        Assert.False(crop.Verified);
        Assert.False(crop.Cropped);
        Assert.Equal(1, crop.BoxesOutsidePage);
        Assert.Equal((0, page.Height), (crop.TopPx, crop.BottomPx));
    }

    [Fact]
    public void Ink_reaching_the_edge_is_never_cut()
    {
        using var page = Page(inkTop: 5, inkBottom: 1318);
        var crop = PdfVisualSelectionImages.Crop(page, 1, []);
        Assert.Equal((0, page.Height), (crop.TopPx, crop.BottomPx));
        Assert.False(crop.Cropped);
    }

    [Fact]
    public void A_blank_page_is_sent_whole()
    {
        using var page = new SKBitmap(1020, 1320);
        using (var canvas = new SKCanvas(page)) canvas.Clear(SKColors.White);
        Assert.False(PdfVisualSelectionImages.Crop(page, 1, []).Cropped);
    }
}
