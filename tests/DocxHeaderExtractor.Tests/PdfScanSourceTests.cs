using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.Sources.Scan;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocxHeaderExtractor.Tests;

[Trait("Category", "Slow")]
public sealed class PdfScanSourceTests : IDisposable
{
    readonly string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dhx-pdf-scan-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    string MixedPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(200, 300);
        // The embedded scan is white: extracting just this image would lose the visible native overlay.
        page.AddPng(TestPng.Grey(40, 60), new PdfRectangle(0, 0, 200, 300));
        page.AddText("PDF OVERLAY", 20, new PdfPoint(20, 200), font);
        builder.AddPage(200, 300).AddText("Native tail", 20, new PdfPoint(20, 200), font);
        var path = Path.Combine(dir, "mixed.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    [Fact]
    public void Whole_page_rendering_keeps_native_overlays_page_order_and_a_stable_raster_basis()
    {
        var path = MixedPdf();
        var original = File.ReadAllBytes(path);
        var integrity = PdfSourceIntegrity.Read(path);
        Assert.True(integrity.RequiresOcr);
        Assert.Equal("MixedPdf", integrity.Kind);
        Assert.Equal(new[] { 1 }, integrity.ScanPages);
        var pages = PdfScanPageRenderer.Render(path);
        Assert.Equal(new[] { 1, 2 }, pages.Select(p => p.Page));
        Assert.InRange(pages[0].Width, 555, 557); // 200 pt at 200 DPI
        Assert.InRange(pages[0].Height, 832, 835);
        Assert.Contains(PngRaster.TryDecode(pages[0].Bytes)!.Grey, pixel => pixel < 100);
        Assert.Equal(pages.Select(p => p.Sha256), PdfScanPageRenderer.Render(path).Select(p => p.Sha256));
        Assert.Single(PdfScanPageRenderer.Render(path, maxPages: 1));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Pdf_ocr_runs_both_views_on_every_page_and_reuses_its_own_cache()
    {
        Assert.NotNull(TesseractOcr.FindTesseract());
        Assert.NotNull(TesseractOcr.FindImageMagick());
        var path = MixedPdf();
        var ocr = new ScanOcrPreflight(Path.Combine(dir, "ocr"));
        var options = new ScanOcrPreflight.Options(Languages: "eng", PageSegmentationMode: 6, Parallelism: 2);
        var evidence = ocr.Run(path, options);
        Assert.Equal(2, evidence.PageCount);
        Assert.Equal("MixedPdf", evidence.SourceKind);
        Assert.Contains("dpi=200", evidence.PageImageBasis);
        Assert.Equal(2, evidence.Views.Count);
        Assert.All(evidence.Views, view => Assert.Equal(new[] { 1, 2 }, view.Pages.Select(p => p.Page)));
        Assert.Contains(evidence.Views[0].Pages[0].Lines, line => line.Text.Contains("OVERLAY", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(evidence.Gaps);
        var file = Path.Combine(ocr.EntryOf(evidence), "evidence.msgpack");
        var modified = File.GetLastWriteTimeUtc(file);
        Assert.Equal(evidence.ParametersSha256, ocr.Run(path, options).ParametersSha256);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void Cancellation_stops_before_pdf_rendering()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfScanPageRenderer.Render("missing.pdf", ct: cts.Token));
    }
}
