namespace DocxHeaderExtractor.Sources.Pdf;

/// <summary>
/// The vertical band of one page that its selection image shows. Pixels are of the 120-DPI render, top-left origin; points are PDF
/// points, bottom-left origin, so <see cref="TopPt"/> is the larger value. The band holds every inked row and every parser line box of
/// the page plus <see cref="BleedPx"/>; <see cref="Verified"/> is false, and the page is sent uncropped, when any of them would fall outside.
/// </summary>
internal sealed record PdfPageImageCrop(int Page, int PageHeightPx, double PageHeightPt, int TopPx, int BottomPx, int BleedPx,
    int? InkTopPx, int? InkBottomPx, int? BoxTopPx, int? BoxBottomPx, int BoxCount, int BoxesOutsidePage, bool Verified)
{
    public double TopPt => Math.Round(PageHeightPt - TopPx * 72d / PdfVisualSelectionImages.Dpi, 1);
    public double BottomPt => Math.Round(PageHeightPt - BottomPx * 72d / PdfVisualSelectionImages.Dpi, 1);
    public bool Cropped => Verified && (TopPx > 0 || BottomPx < PageHeightPx);
}
