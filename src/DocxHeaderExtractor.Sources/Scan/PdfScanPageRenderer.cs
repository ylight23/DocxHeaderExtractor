using System.Security.Cryptography;
using System.Runtime.Versioning;
using PDFtoImage;
using SkiaSharp;

namespace DocxHeaderExtractor.Sources.Scan;

internal sealed record RenderedPdfPage(int Page, byte[] Bytes, string Sha256, int Width, int Height);

/// <summary>Renders the whole page, including native overlays and annotations. Coordinates are pixels of this recorded raster, never PDF points.</summary>
internal static class PdfScanPageRenderer
{
    public const string Version = "PDF_SCAN_RASTER_V1";
    public const int Dpi = 200;
    public static string CacheBasis => Version + "|dpi=" + Dpi + "|annotations=true|forms=true|PDFtoImage=" + typeof(Conversion).Assembly.GetName().Version;

    public static IReadOnlyList<RenderedPdfPage> Render(string path, int? maxPages = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return RenderDesktop(path, maxPages, ct);
        throw new PlatformNotSupportedException("PDF_SCAN_RENDERING_REQUIRES_DESKTOP_RUNTIME");
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    static IReadOnlyList<RenderedPdfPage> RenderDesktop(string path, int? maxPages, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        var count = Conversion.GetPageCount(stream, leaveOpen: true);
        if (maxPages is { } max) count = Math.Min(count, Math.Max(0, max));
        if (count == 0) return [];
        stream.Position = 0;
        var result = new List<RenderedPdfPage>();
        foreach (var bitmap in Conversion.ToImages(stream, pages: 0..count, leaveOpen: true,
                     options: new RenderOptions(Dpi: Dpi, WithAnnotations: true, WithFormFill: true)))
        {
            using (bitmap)
            {
                ct.ThrowIfCancellationRequested();
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                var bytes = png.ToArray();
                result.Add(new(result.Count + 1, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), bitmap.Width, bitmap.Height));
            }
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
