using System.Collections.Concurrent;
using OpenCvSharp;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Scan;

/// <summary>A rectangle drawn on a page (a frame, or the outer border of a table), in pixels of the page image.</summary>
public sealed record DrawnFrame(int Left, int Top, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= Left && x < Left + Width && y >= Top && y < Top + Height;
}

/// <summary>
/// The rectangles drawn on a page image, found with OpenCV: the page is binarized (Otsu), the rules are kept by a morphological opening with a
/// long horizontal and a long vertical line, and every outer contour of those rules that spans both directions is a frame. It is a
/// measurement of what is drawn on the page; it says nothing about the text inside or outside a frame.
/// </summary>
public static class ScanFrames
{
    public const string Version = "SCAN_FRAMES_V1";

    /// <summary>A rule is a dark run at least this share of the page width (horizontal) or height (vertical).</summary>
    public const double RuleShare = 1.0 / 20;

    /// <summary>The frames drawn on one page image (greyscale), top to bottom.</summary>
    public static IReadOnlyList<DrawnFrame> Find(Mat grey)
    {
        using var bin = new Mat();
        Cv2.Threshold(grey, bin, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        int hLen = Math.Max(2, (int)(grey.Cols * RuleShare)), vLen = Math.Max(2, (int)(grey.Rows * RuleShare));
        using var hKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(hLen, 1));
        using var vKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, vLen));
        using var horizontal = new Mat();
        using var vertical = new Mat();
        using var rules = new Mat();
        Cv2.MorphologyEx(bin, horizontal, MorphTypes.Open, hKernel);
        Cv2.MorphologyEx(bin, vertical, MorphTypes.Open, vKernel);
        Cv2.BitwiseOr(horizontal, vertical, rules);
        Cv2.FindContours(rules, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        // A lone rule (an underline, a page edge) spans one direction only; a frame spans both.
        return contours.Select(c => Cv2.BoundingRect(c)).Where(r => r.Width >= hLen && r.Height >= vLen)
            .Select(r => new DrawnFrame(r.X, r.Y, r.Width, r.Height)).OrderBy(f => f.Top).ThenBy(f => f.Left).ToArray();
    }

    /// <summary>For every line of the primary view whose centre lies inside a drawn frame, the smallest such frame, by OCR line id; a page whose
    /// image cannot be read has no entries.</summary>
    public static IReadOnlyDictionary<string, DrawnFrame> MeasureAll(ScanOcrEvidence evidence, string evidenceEntry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var primary = evidence.Views.First(v => v.Spec.ViewId == ScanOcrPreflight.OriginalView);
        var result = new ConcurrentDictionary<string, DrawnFrame>(StringComparer.Ordinal);
        Parallel.ForEach(primary.Pages, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, page =>
        {
            if (ScanOcrPreflight.PageImagePath(evidenceEntry, page.Page) is not { } image || !File.Exists(image)) return;
            using var grey = Cv2.ImRead(image, ImreadModes.Grayscale);
            if (grey.Empty()) return;
            var frames = Find(grey);
            if (frames.Count == 0) return;
            foreach (var line in page.Lines)
            {
                int x = line.Box.Left + line.Box.Width / 2, y = line.Box.Top + line.Box.Height / 2;
                if (frames.Where(f => f.Contains(x, y)).OrderBy(f => f.Width * f.Height).FirstOrDefault() is { } frame) result[line.Id] = frame;
            }
        });
        return result;
    }
}
