using OpenCvSharp;
using DocxHeaderExtractor.Contracts.Scan;

namespace DocxHeaderExtractor.Sources.Scan;

/// <summary>
/// The text lines of a page image, found by ink alone: letter-sized components are kept (stamp rings, frames, rules and speckle are not), their
/// rows of ink are grouped into bands, a band taller than its neighbours is split at its deepest valley, and each band is trimmed to the columns
/// that hold a real share of its height. This is the segmentation of the hybrid reader (hybrid.py, segment), in C#.
/// </summary>
public static class ScanLineSegmenter
{
    public const string Version = "SCAN_LINE_SEGMENT_V2";

    /// <summary>The smallest line a band may be (pixels), and the share of the typical line height a band may stand above before it is split.</summary>
    public const int MinLineHeight = 8;

    /// <summary>Components taller or wider than these, or smaller in ink than the speckle limit, are not text.</summary>
    public const int MaxComponentHeight = 80, MaxComponentWidth = 120, MinComponentArea = 6;

    /// <summary>A wide, very flat component is a dot leader or a rule.</summary>
    public const int RuleWidth = 40, RuleHeight = 8;

    /// <summary>The line lies in the ink rows whose smoothed count is at least this share of the median nonzero count.</summary>
    public const double InkRowShare = 0.08;

    /// <summary>A column belongs to a line when it holds at least this share of the band's height (and at least two pixels).</summary>
    public const double ColumnShare = 0.15;

    /// <summary>
    /// Removes only the ring pixels of a stamp, as the hybrid reader does: a circle is found by Hough, refitted by least squares to the dark
    /// pixels near it, and accepted only when it is dark all the way round (at least 80% of 360 angles). Returns the image and what was done.
    /// </summary>
    /// <summary>
    /// The circle found at a coarse reduction, then searched again at <paramref name="fineDivisor"/> in a window around it only: the same
    /// search as the fine one, on a fraction of the page. Null when the coarse search finds no circle.
    /// </summary>
    static (double Cx, double Cy, double R)? SeedFromCoarse(Mat grey, int coarseDivisor, int fineDivisor)
    {
        using var coarse = new Mat();
        Cv2.Resize(grey, coarse, new Size(grey.Width / coarseDivisor, grey.Height / coarseDivisor), 0, 0, InterpolationFlags.Area);
        using var coarseBlur = new Mat();
        Cv2.MedianBlur(coarse, coarseBlur, 3);
        var found = Cv2.HoughCircles(coarseBlur, HoughModes.Gradient, 1, 500 / coarseDivisor, 100, HalfHoughVotes, 90 / coarseDivisor, 220 / coarseDivisor);
        if (found.Length == 0) return null;
        var cx0 = coarseDivisor * (double)found[0].Center.X; var cy0 = coarseDivisor * (double)found[0].Center.Y; var r0 = coarseDivisor * (double)found[0].Radius;
        // The window: the coarse circle with a margin of 40 page pixels, in the fine page's coordinates.
        var margin = 40 + coarseDivisor;
        var x0 = (int)Math.Max(0, (cx0 - r0 - margin) / fineDivisor); var y0 = (int)Math.Max(0, (cy0 - r0 - margin) / fineDivisor);
        var x1 = (int)Math.Min(grey.Width / fineDivisor, (cx0 + r0 + margin) / fineDivisor); var y1 = (int)Math.Min(grey.Height / fineDivisor, (cy0 + r0 + margin) / fineDivisor);
        using var fine = new Mat();
        Cv2.Resize(grey, fine, new Size(grey.Width / fineDivisor, grey.Height / fineDivisor), 0, 0, InterpolationFlags.Area);
        using var window = new Mat(fine, new Rect(x0, y0, x1 - x0, y1 - y0));
        using var windowBlur = new Mat();
        Cv2.MedianBlur(window, windowBlur, 3);
        var circles = Cv2.HoughCircles(windowBlur, HoughModes.Gradient, 1, 500 / fineDivisor, 100, HalfHoughVotes, 90 / fineDivisor, 220 / fineDivisor);
        if (circles.Length == 0) return null;
        return (fineDivisor * (x0 + (double)circles[0].Center.X), fineDivisor * (y0 + (double)circles[0].Center.Y), fineDivisor * (double)circles[0].Radius);
    }

    /// <summary>The Hough accumulator threshold on the half-size page: the votes of a circle shrink with its circumference.</summary>
    public const int HalfHoughVotes = 30;

    /// <summary>The coarse search runs at a quarter of the page and the fine one at half, inside a window around the coarse circle: the same line boxes as
    /// the half-size search on all 133 stored pages of four documents, at a lower cost.</summary>
    /// <summary>The circle of an accepted stamp, in the page's pixels.</summary>
    public sealed record StampCircle(double Cx, double Cy, double R);

    public static (Mat Image, string Note, StampCircle? Stamp) RemoveStamp(Mat grey, int searchDivisor = 2, int coarseDivisor = 4)
    {
        // The circle is searched on the page at half size (the Hough search on the full page took seconds a page); it only seeds the fit below,
        // which runs on the full page's dark pixels, and the acceptance test is unchanged.
        var seed = coarseDivisor > 0 ? SeedFromCoarse(grey, coarseDivisor, searchDivisor) : null;
        if (seed is null)
        {
            using var small = new Mat();
            Cv2.Resize(grey, small, new Size(grey.Width / searchDivisor, grey.Height / searchDivisor), 0, 0, InterpolationFlags.Area);
            using var blurred = new Mat();
            Cv2.MedianBlur(small, blurred, 3);
            var circles = Cv2.HoughCircles(blurred, HoughModes.Gradient, 1, 500 / searchDivisor, 100, HalfHoughVotes, 90 / searchDivisor, 220 / searchDivisor);
            if (circles.Length == 0) return (grey.Clone(), "no circle", null);
            seed = (searchDivisor * (double)circles[0].Center.X, searchDivisor * (double)circles[0].Center.Y, searchDivisor * (double)circles[0].Radius);
        }
        var (cx, cy, r) = seed.Value;
        var dark = DarkPoints(grey);
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var near = dark.Where(p => Math.Abs(Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) - r) <= Math.Max(25, 0.15 * r)).ToArray();
            if (near.Length < 200) break;
            (cx, cy, r) = FitCircle(near);
        }
        var hits = 0;
        for (var k = 0; k < 360; k++)
        {
            var a = 2 * Math.PI * k / 360;
            var x = (int)Math.Round(cx + r * Math.Cos(a)); var y = (int)Math.Round(cy + r * Math.Sin(a));
            if (x < 0 || x >= grey.Width || y < 0 || y >= grey.Height) continue;
            if (AnyDark(grey, x, y)) hits++;
        }
        var share = hits / 360.0;
        if (share < 0.8) return (grey.Clone(), $"circle rejected (share {share:0.00})", null);
        var image = grey.Clone();
        foreach (var p in dark)
        {
            var d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
            if (Math.Abs(d - r) <= 9) image.Set(p.Y, p.X, (byte)255);
        }
        return (image, $"ring removed at ({cx:0},{cy:0}) r={r:0.0} share {share:0.00}", new StampCircle(cx, cy, r));
    }

    /// <summary>The pixels darker than 128, row by row (the order of a scan from the top left).</summary>
    static List<(int X, int Y)> DarkPoints(Mat grey)
    {
        using var dark = new Mat();
        Cv2.Threshold(grey, dark, 127, 255, ThresholdTypes.BinaryInv);
        using var found = new Mat();
        Cv2.FindNonZero(dark, found);
        if (found.Empty()) return [];
        found.GetArray(out Point[] points);
        return points.Select(p => (p.X, p.Y)).ToList();
    }

    static bool AnyDark(Mat grey, int x, int y)
    {
        for (var j = Math.Max(0, y - 4); j <= Math.Min(grey.Height - 1, y + 4); j++)
            for (var i = Math.Max(0, x - 4); i <= Math.Min(grey.Width - 1, x + 4); i++)
                if (grey.At<byte>(j, i) < 128) return true;
        return false;
    }

    /// <summary>Least squares circle through points: solves x²+y² = 2·cx·x + 2·cy·y + c for cx, cy and c, then r = sqrt(c + cx² + cy²).</summary>
    static (double Cx, double Cy, double R) FitCircle(IReadOnlyList<(int X, int Y)> points)
    {
        double[,] m = new double[3, 4];
        foreach (var p in points)
        {
            double[] row = [2.0 * p.X, 2.0 * p.Y, 1.0];
            double rhs = (double)p.X * p.X + (double)p.Y * p.Y;
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++) m[i, j] += row[i] * row[j];
                m[i, 3] += row[i] * rhs;
            }
        }
        for (var c = 0; c < 3; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < 3; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c])) pivot = r;
            for (var j = 0; j < 4; j++) (m[c, j], m[pivot, j]) = (m[pivot, j], m[c, j]);
            for (var r = 0; r < 3; r++)
            {
                if (r == c || m[c, c] == 0) continue;
                var f = m[r, c] / m[c, c];
                for (var j = 0; j < 4; j++) m[r, j] -= f * m[c, j];
            }
        }
        var cx = m[0, 3] / m[0, 0]; var cy = m[1, 3] / m[1, 1]; var cc = m[2, 3] / m[2, 2];
        return (cx, cy, Math.Sqrt(cc + cx * cx + cy * cy));
    }

    /// <summary>The lines of a page image (grey), top to bottom, reading order within a row.</summary>
    /// <param name="stamp">The stamp's circle, when one was removed. The ink inside it is the emblem and the stamp's lettering, which lies in every
    /// row of the block; it is left out of the row profile that finds the lines, so the text the stamp overlaps is still found. The columns
    /// of a line still come from all its ink, so the crop keeps the text under the stamp.</param>
    public static IReadOnlyList<PixelBox> Segment(Mat grey, StampCircle? stamp = null)
    {
        using var bw = new Mat();
        Cv2.Threshold(grey, bw, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(bw, labels, stats, centroids, PixelConnectivity.Connectivity8);
        using var mask = new Mat(bw.Size(), MatType.CV_8UC1, Scalar.All(0));
        for (var i = 1; i < n; i++)
        {
            var x = stats.At<int>(i, 0); var y = stats.At<int>(i, 1); var w = stats.At<int>(i, 2); var h = stats.At<int>(i, 3); var a = stats.At<int>(i, 4);
            if (h > MaxComponentHeight || w > MaxComponentWidth || a < MinComponentArea) continue;
            if (w > RuleWidth && h < RuleHeight) continue;
            // As the hybrid reader does, the whole rectangle of a kept component is copied, neighbours inside it included.
            bw.SubMat(y, y + h, x, x + w).CopyTo(mask.SubMat(y, y + h, x, x + w));
        }
        var rows = bw.Height;
        using var rowMask = mask.Clone();
        // The stamp's disc, a little wider than its ring, leaves the row profile; the ring itself was already removed from the image.
        if (stamp is { } disc) Cv2.Circle(rowMask, new Point((int)Math.Round(disc.Cx), (int)Math.Round(disc.Cy)), (int)Math.Round(disc.R + 8), Scalar.All(0), -1);
        var counts = new double[rows];
        for (var y = 0; y < rows; y++) counts[y] = Cv2.CountNonZero(rowMask.Row(y));
        var prof = Smooth(counts);
        var nonzero = prof.Where(v => v > 0).OrderBy(v => v).ToArray();
        var threshold = nonzero.Length > 0 ? InkRowShare * Median(nonzero) : 0;
        var bands = new List<(int Top, int Bottom)>();
        int? start = null;
        for (var y = 0; y < rows; y++)
        {
            var ink = prof[y] > threshold;
            if (ink && start is null) start = y;
            if (!ink && start is { } s) { bands.Add((s, y - 1)); start = null; }
        }
        if (start is { } last) bands.Add((last, rows - 1));
        bands = bands.Where(b => b.Bottom - b.Top + 1 >= MinLineHeight).ToList();
        if (bands.Count > 0)
        {
            var typical = Median(bands.Select(b => (double)(b.Bottom - b.Top + 1)).ToArray());
            bands = bands.SelectMany(b => SplitAtValley(b, prof, typical)).ToList();
        }
        var boxes = new List<PixelBox>();
        foreach (var (top, bottom) in bands)
        {
            var h = bottom - top + 1;
            if (h < MinLineHeight) continue;
            // The ink of every column of the band in one pass (the mask holds 255 for ink).
            using var band = mask.RowRange(top, bottom + 1);
            using var sums = new Mat();
            Cv2.Reduce(band, sums, ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32S);
            var columns = new List<int>();
            for (var x = 0; x < mask.Width; x++)
            {
                var column = sums.At<int>(0, x) / 255;
                if (column >= Math.Max(2, ColumnShare * h)) columns.Add(x);
            }
            if (columns.Count < 10) continue;
            var left = columns[0]; var right = columns[^1];
            boxes.Add(new PixelBox(left, top, right - left + 1, h));
        }
        return boxes.OrderBy(b => b.Top).ThenBy(b => b.Left).ToArray();
    }

    /// <summary>A band taller than 1.4 times the typical line, and more than two minimum lines high, is cut at its deepest valley between 30% and
    /// 70% of its height when that valley is under 60% of the band's median count; the cut repeats until no piece qualifies.</summary>
    static IEnumerable<(int Top, int Bottom)> SplitAtValley((int Top, int Bottom) band, double[] prof, double typical)
    {
        var pieces = new List<(int Top, int Bottom)> { band };
        bool changed;
        do
        {
            changed = false;
            var next = new List<(int Top, int Bottom)>();
            foreach (var (p, q) in pieces)
            {
                var h = q - p + 1;
                if (h > 1.4 * typical && h > 2 * MinLineHeight)
                {
                    var seg = prof[p..(q + 1)];
                    var lo = (int)(0.3 * h); var hi = (int)(0.7 * h);
                    var cut = lo;
                    for (var k = lo; k <= hi; k++) if (seg[k] < seg[cut]) cut = k;
                    if (seg[cut] < 0.6 * Median(seg))
                    {
                        next.Add((p, p + cut - 1)); next.Add((p + cut, q)); changed = true; continue;
                    }
                }
                next.Add((p, q));
            }
            pieces = next;
        } while (changed);
        return pieces;
    }

    /// <summary>A three-row average with zero padding, as numpy.convolve(prof, ones(3)/3, mode='same') gives.</summary>
    static double[] Smooth(double[] counts)
    {
        var smooth = new double[counts.Length];
        for (var y = 0; y < counts.Length; y++)
        {
            var sum = counts[y] + (y > 0 ? counts[y - 1] : 0) + (y + 1 < counts.Length ? counts[y + 1] : 0);
            smooth[y] = sum / 3.0;
        }
        return smooth;
    }

    static double Median(double[] values)
    {
        var s = values.OrderBy(v => v).ToArray();
        var m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
    }
}
