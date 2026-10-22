using DocxHeaderExtractor.Contracts.Scan;

namespace DocxHeaderExtractor.Sources.Scan;

/// <summary>
/// What the page image shows of one OCR line's type, measured on its own box and nothing else: the mean length of the horizontal runs of dark
/// pixels in the box (the width of the vertical strokes, in pixels), the same per pixel of the box's height, the share of the box that is dark,
/// and the number of runs counted.
/// </summary>
public sealed record LineTypography(string LineId, double StrokeWidthPx, double StrokePerHeight, double InkShare, int Runs);

/// <summary>
/// Measures the type of every line of a page on its image (<see cref="PngRaster"/>, dark at the bilevel threshold).
/// </summary>
public static class ScanTypography
{
    /// <summary>V3: the page image is read as grey by OpenCV (seven times faster than the managed PNG reader; the same values on three of four
    /// stored scans, and within 0.02 px of stroke on the fourth, an RGBA PDF scan).</summary>
    public const string Version = "SCAN_TYPOGRAPHY_V3";

    public static IReadOnlyList<LineTypography> Measure(PngRaster raster, OcrPage page)
    {
        var cut = (int)Math.Floor(TesseractOcr.BilevelThresholdShare * 255);
        var raw = page.Lines.Select(line =>
        {
            var b = line.Box;
            long dark = 0, runs = 0, runLength = 0;
            int x0 = Math.Max(0, b.Left), x1 = Math.Min(raster.Width, b.Left + b.Width), y0 = Math.Max(0, b.Top), y1 = Math.Min(raster.Height, b.Top + b.Height);
            for (var y = y0; y < y1; y++)
            {
                var run = 0;
                for (var x = x0; x < x1; x++)
                {
                    if (raster[x, y] <= cut) { dark++; run++; }
                    else if (run > 0) { runs++; runLength += run; run = 0; }
                }
                if (run > 0) { runs++; runLength += run; }
            }
            var area = Math.Max(1, (x1 - x0) * (y1 - y0));
            return (line, stroke: runs == 0 ? 0 : (double)runLength / runs, ink: (double)dark / area, runs: (int)Math.Min(int.MaxValue, runs));
        }).ToArray();
        return raw.Select(r => new LineTypography(r.line.Id, Math.Round(r.stroke, 2),
            Math.Round(r.stroke / Math.Max(1, r.line.Box.Height), 4), Math.Round(r.ink, 3), r.runs)).ToArray();
    }

    /// <summary>Every line of the primary view measured, page by page in parallel; a page whose image cannot be read has no measurements.</summary>
    public static IReadOnlyDictionary<string, LineTypography> MeasureAll(ScanOcrEvidence evidence, string evidenceEntry, string? magick, CancellationToken ct = default)
    {
        using var cancellation = TesseractOcr.WithCancellation(ct);
        ct.ThrowIfCancellationRequested();
        var primary = evidence.Views.First(v => v.Spec.ViewId == ScanOcrPreflight.OriginalView);
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, LineTypography>(StringComparer.Ordinal);
        Parallel.ForEach(primary.Pages, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, page =>
        {
            if (ScanOcrPreflight.PageImagePath(evidenceEntry, page.Page) is not { } image || (PngRaster.LoadGrey(image) ?? PngRaster.Load(image, magick)) is not { } raster) return;
            foreach (var t in Measure(raster, page)) result[t.LineId] = t;
        });
        return result;
    }
}
