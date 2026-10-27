using PDFtoImage;
using SkiaSharp;

namespace DocxHeaderExtractor.Sources.Pdf;

/// <summary>Renders compact page overviews for PDF selection. The bytes are source evidence, not labels.</summary>
internal static class PdfVisualSelectionImages
{
    public const int Dpi = 120;
    /// <summary>Space kept above the first and below the last kept row, in render pixels (about 0.2 inch).</summary>
    public const int BleedPx = 24;
    /// <summary>A pixel darker than this (0-255 luminance) is ink.</summary>
    const int InkLuminance = 245;

    internal static IReadOnlyDictionary<int, byte[]> RenderJpegOverviews(string path, IReadOnlyCollection<int> pages,
        CancellationToken cancellationToken = default, int maxWidth = 1200, int quality = 58, bool grayscale = false, bool bilevel = false) =>
        Render(path, pages, cancellationToken, maxWidth, quality, grayscale, bilevel, null).Images;

    /// <summary>As <see cref="RenderJpegOverviews"/>, but each page image shows only the band from its first to its last inked row or parser
    /// line box (<paramref name="boxesPt"/>: top and bottom in PDF points per page), plus <see cref="BleedPx"/>. Returns the crop of every page.</summary>
    internal static (IReadOnlyDictionary<int, byte[]> Images, IReadOnlyDictionary<int, PdfPageImageCrop> Crops) RenderCroppedJpegOverviews(string path,
        IReadOnlyCollection<int> pages, IReadOnlyDictionary<int, IReadOnlyList<(double TopPt, double BottomPt)>> boxesPt,
        CancellationToken cancellationToken = default, int maxWidth = 1200, int quality = 58, bool grayscale = false, bool bilevel = false) =>
        Render(path, pages, cancellationToken, maxWidth, quality, grayscale, bilevel, boxesPt);

    static (IReadOnlyDictionary<int, byte[]> Images, IReadOnlyDictionary<int, PdfPageImageCrop> Crops) Render(string path, IReadOnlyCollection<int> pages,
        CancellationToken cancellationToken, int maxWidth, int quality, bool grayscale, bool bilevel, IReadOnlyDictionary<int, IReadOnlyList<(double TopPt, double BottomPt)>>? boxesPt)
    {
        if (maxWidth < 256 || maxWidth > 2400) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        var requested = pages.ToHashSet();
        var result = new Dictionary<int, byte[]>();
        var crops = new Dictionary<int, PdfPageImageCrop>();
        using var stream = File.OpenRead(path);
        var page = 0;
        foreach (var rendered in Conversion.ToImages(stream, leaveOpen: true,
            options: new RenderOptions(Dpi: Dpi, WithAnnotations: true, WithFormFill: true)))
        using (rendered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            page++;
            if (!requested.Contains(page)) continue;
            var bitmap = rendered;
            SKBitmap? band = null;
            if (boxesPt is not null)
            {
                var crop = Crop(rendered, page, boxesPt.GetValueOrDefault(page) ?? []);
                crops.Add(page, crop);
                if (crop.Cropped)
                {
                    band = new SKBitmap();
                    if (!rendered.ExtractSubset(band, new SKRectI(0, crop.TopPx, rendered.Width, crop.BottomPx)))
                        throw new InvalidOperationException("PDF_VISUAL_CROP_FAILED");
                    bitmap = band;
                }
            }
            using (band)
                result.Add(page, Encode(bitmap, maxWidth, quality, grayscale, bilevel));
        }
        if (!requested.SetEquals(result.Keys)) throw new InvalidOperationException("PDF_VISUAL_PAGE_NOT_RENDERED");
        return (result, crops);
    }

    /// <summary>1-bit look: pixels above the mid-grey become white, the rest black, and the page is sent as a PNG. Smaller than grey JPEG, but
    /// grey bands and anti-aliasing are lost.</summary>
    const byte BilevelCut = 140;

    static byte[] Encode(SKBitmap bitmap, int maxWidth, int quality, bool grayscale, bool bilevel)
    {
        using var resized = bitmap.Resize(new SKImageInfo(Math.Min(maxWidth, bitmap.Width),
            Math.Max(1, (int)Math.Round(bitmap.Height * Math.Min((double)maxWidth / bitmap.Width, 1d)))),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        var image = resized ?? bitmap;
        if (bilevel)
        {
            var bilevelInfo = new SKImageInfo(image.Width, image.Height, SKColorType.Gray8, SKAlphaType.Opaque);
            using var grey = new SKBitmap(bilevelInfo);
            using (var canvas = new SKCanvas(grey)) canvas.DrawBitmap(image, 0, 0);
            using var black = new SKBitmap(bilevelInfo);
            for (var y = 0; y < bilevelInfo.Height; y++)
                for (var x = 0; x < bilevelInfo.Width; x++)
                {
                    var c = grey.GetPixel(x, y).Red > BilevelCut ? (byte)255 : (byte)0;
                    black.SetPixel(x, y, new SKColor(c, c, c));
                }
            using var png = SKImage.FromBitmap(black).Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        if (!grayscale)
        {
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            return encoded.ToArray();
        }
        // JPEG has no metadata flag for a semantic "gray page". Render pixels into Gray8 so the
        // wire image is actually monochrome; source text and geometry remain unchanged.
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("PDF_VISUAL_GRAY_SURFACE_FAILED");
        surface.Canvas.DrawBitmap(image, 0, 0);
        using var gray = surface.Snapshot();
        using var encodedGray = gray.Encode(SKEncodedImageFormat.Jpeg, quality);
        return encodedGray.ToArray();
    }

    /// <summary>The kept band of one rendered page: every inked row and every parser box, plus the bleed. Unverified (kept whole) when a
    /// box lies outside the page, since then the point-to-pixel mapping cannot be trusted.</summary>
    internal static PdfPageImageCrop Crop(SKBitmap rendered, int page, IReadOnlyList<(double TopPt, double BottomPt)> boxesPt)
    {
        var height = rendered.Height;
        var heightPt = height * 72d / Dpi;
        int? inkTop = null, inkBottom = null;
        for (var y = 0; y < height; y++)
            if (RowHasInk(rendered, y)) { inkTop ??= y; inkBottom = y + 1; }
        int? boxTop = null, boxBottom = null;
        var outside = 0;
        foreach (var (topPt, bottomPt) in boxesPt)
        {
            var top = (int)Math.Floor((heightPt - topPt) * Dpi / 72d);
            var bottom = (int)Math.Ceiling((heightPt - bottomPt) * Dpi / 72d);
            if (top < 0 || bottom > height || top >= bottom) { outside++; continue; }
            boxTop = Math.Min(boxTop ?? top, top);
            boxBottom = Math.Max(boxBottom ?? bottom, bottom);
        }
        var keepTop = Min(inkTop, boxTop);
        var keepBottom = Max(inkBottom, boxBottom);
        if (outside > 0 || keepTop is null || keepBottom is null)
            return new(page, height, heightPt, 0, height, BleedPx, inkTop, inkBottom, boxTop, boxBottom, boxesPt.Count, outside, false);
        var cropTop = Math.Max(0, keepTop.Value - BleedPx);
        var cropBottom = Math.Min(height, keepBottom.Value + BleedPx);
        var verified = cropTop <= keepTop && cropBottom >= keepBottom;
        return verified
            ? new(page, height, heightPt, cropTop, cropBottom, BleedPx, inkTop, inkBottom, boxTop, boxBottom, boxesPt.Count, 0, true)
            : new(page, height, heightPt, 0, height, BleedPx, inkTop, inkBottom, boxTop, boxBottom, boxesPt.Count, 0, false);
    }

    static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
    static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    static bool RowHasInk(SKBitmap bitmap, int y)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if ((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000 < InkLuminance) return true;
        }
        return false;
    }
}
