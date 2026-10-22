using OpenCvSharp;
using DocxHeaderExtractor.Contracts.Scan;

namespace DocxHeaderExtractor.Sources.Scan;

/// <summary>
/// Text the OCR engine left out: ink outside every OCR line box, in rows of letter-sized marks. A stamp, a fold or a faded print can cover a
/// line so that the engine finds nothing there; the ink still shows where the line is. The rows are measurements of the page image; they are
/// not read here. Their alias is <see cref="Alias"/>, which no OCR line uses.
/// </summary>
public static class ScanUncoveredText
{
    public const string Version = "SCAN_UNCOVERED_TEXT_V1";

    /// <summary>A mark is letter-sized: at most this tall and this wide. Larger ink (a stamp, a rule, a photograph) is not text.</summary>
    public const int MinMarkHeight = 10, MaxMarkHeight = 40, MaxMarkWidth = 60, MinMarkArea = 40;

    /// <summary>A covered box is widened by this many pixels, so that accents and descenders of an OCR line are not read as uncovered text.</summary>
    public const int CoverPadding = 10;

    /// <summary>Marks belong to one row when their centres are within this many pixels vertically and this far apart horizontally.</summary>
    public const int RowTolerance = 6, MaxGap = 40;

    /// <summary>A row is text when it holds at least this many marks and spans at least this many pixels.</summary>
    public const int MinMarks = 4, MinRowWidth = 80, MinRowHeight = 12;

    /// <summary>The alias of an uncovered row on a page, numbered in reading order. It differs from the OCR lines' aliases (P001-L001), so no
    /// existing alias or cached answer changes.</summary>
    public static string Alias(int page, int index) => $"P{page:000}-G{index:000}";

    /// <summary>The rows of letter-sized ink on a page image that no covered box touches, top to bottom.</summary>
    public static IReadOnlyList<PixelBox> Find(Mat grey, IReadOnlyList<PixelBox> covered)
    {
        using var bin = new Mat();
        Cv2.Threshold(grey, bin, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var mask = new Mat(grey.Size(), MatType.CV_8UC1, Scalar.All(255));
        foreach (var b in covered)
            Cv2.Rectangle(mask, new Rect(b.Left - CoverPadding, b.Top - CoverPadding, b.Width + 2 * CoverPadding, b.Height + 2 * CoverPadding), Scalar.All(0), -1);
        using var free = new Mat();
        Cv2.BitwiseAnd(bin, mask, free);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var clean = new Mat();
        Cv2.MorphologyEx(free, clean, MorphTypes.Open, kernel);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(clean, labels, stats, centroids);
        var marks = new List<PixelBox>();
        for (var i = 1; i < n; i++)
        {
            var w = stats.At<int>(i, 2); var h = stats.At<int>(i, 3); var a = stats.At<int>(i, 4);
            if (w > MaxMarkWidth || h < MinMarkHeight || h > MaxMarkHeight || a < MinMarkArea) continue;
            marks.Add(new PixelBox(stats.At<int>(i, 0), stats.At<int>(i, 1), w, h));
        }
        return Rows(marks);
    }

    /// <summary>Joins marks into rows: sorted by centre height, a mark joins the open row when it lies on the same band and close enough.</summary>
    internal static IReadOnlyList<PixelBox> Rows(IReadOnlyList<PixelBox> marks)
    {
        var sorted = marks.OrderBy(m => m.Top + m.Height / 2).ThenBy(m => m.Left).ToList();
        var rows = new List<List<PixelBox>>();
        foreach (var m in sorted)
        {
            var cy = m.Top + m.Height / 2;
            var row = rows.LastOrDefault();
            var last = row?[^1];
            if (row is not null && last is not null
                && Math.Abs((last.Top + last.Height / 2) - cy) <= RowTolerance
                && m.Left - last.Right <= MaxGap)
                row.Add(m);
            else rows.Add([m]);
        }
        return rows.Where(r => r.Count >= MinMarks)
            .Select(r => PixelBox.Union(r))
            .Where(b => b.Width >= MinRowWidth && b.Height >= MinRowHeight)
            .OrderBy(b => b.Top).ThenBy(b => b.Left).ToArray();
    }
}
