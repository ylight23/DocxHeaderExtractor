namespace DocxHeaderExtractor.DocumentProcessing.Source.Scan;

/// <summary>
/// What the page image shows of one OCR line's type, measured, not judged: the mean length of the horizontal runs of dark pixels in its box (the
/// width of its vertical strokes: thick in bold type) and the share of its box that is dark, each also against the median of the page's lines
/// (lines with fewer than <see cref="ScanTypography.MinRuns"/> runs, specks, are left out of the median). Height against the page is in the
/// layout facts already.
/// </summary>
public sealed record LineTypography(string LineId, double StrokeWidthPx, double StrokeVsPageMedian, double StrokePerHeightVsPageMedian,
    double InkShare, double InkVsPageMedian, int Runs);

/// <summary>
/// Measures the type of every line of a page on its image (<see cref="PngRaster"/>, dark at the bilevel threshold). Measured on 023 against the
/// gold heading lines: every bold line at 1.29 times the page's median stroke or more (10th percentile; median 1.35), every regular heading line
/// and 98% of the body at 1.06 or less; the body lines above 1.15 were bold in the image too (the title block, signature, motto).
/// </summary>
public static class ScanTypography
{
    public const string Version = "SCAN_TYPOGRAPHY_V1";
    public const int MinRuns = 20;

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
        var counted = raw.Where(r => r.runs >= MinRuns).ToArray();
        double Median(IEnumerable<double> v) { var s = v.Order().ToArray(); return s.Length == 0 ? 0 : s[s.Length / 2]; }
        var stroke = Median(counted.Select(r => r.stroke));
        var perHeight = Median(counted.Select(r => r.stroke / Math.Max(1, r.line.Box.Height)));
        var ink = Median(counted.Select(r => r.ink));
        double Ratio(double v, double m) => m <= 0 ? 0 : Math.Round(v / m, 2);
        return raw.Select(r => new LineTypography(r.line.Id, Math.Round(r.stroke, 2), Ratio(r.stroke, stroke),
            Ratio(r.stroke / Math.Max(1, r.line.Box.Height), perHeight), Math.Round(r.ink, 3), Ratio(r.ink, ink), r.runs)).ToArray();
    }

    /// <summary>Every line of the primary view measured, page by page in parallel; a page whose image cannot be read has no measurements.</summary>
    public static IReadOnlyDictionary<string, LineTypography> MeasureAll(ScanOcrEvidence evidence, string evidenceEntry, string? magick)
    {
        var primary = evidence.Views.First(v => v.Spec.ViewId == ScanOcrPreflight.OriginalView);
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, LineTypography>(StringComparer.Ordinal);
        Parallel.ForEach(primary.Pages, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, page =>
        {
            if (ScanOcrPreflight.PageImagePath(evidenceEntry, page.Page) is not { } image || PngRaster.Load(image, magick) is not { } raster) return;
            foreach (var t in Measure(raster, page)) result[t.LineId] = t;
        });
        return result;
    }
}
