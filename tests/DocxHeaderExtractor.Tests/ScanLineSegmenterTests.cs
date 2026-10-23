using DocxHeaderExtractor.Sources.Scan;
using OpenCvSharp;

namespace DocxHeaderExtractor.Tests;

/// <summary>The line segmenter on drawn pages: the lines of a title that a stamp overlaps are found once the stamp's disc is left out of the row profile.</summary>
public sealed class ScanLineSegmenterTests
{
    static Mat PageWithStampedTitle(out ScanLineSegmenter.StampCircle stamp)
    {
        var page = new Mat(1200, 1000, MatType.CV_8UC1, Scalar.All(255));
        // Body text: the page's typical line.
        for (var i = 0; i < 12; i++)
            Cv2.PutText(page, "Body text of the document, line " + i, new Point(60, 640 + 40 * i), HersheyFonts.HersheySimplex, 0.8, Scalar.All(0), 2);
        // Four title lines, as on 023 page 23: the first sits beside the disc, the others run into it.
        Cv2.PutText(page, "PHU LUC", new Point(640, 140), HersheyFonts.HersheySimplex, 1.0, Scalar.All(0), 2);
        string[] overlapped = ["MAU LOI CHUNG, MAU SO", "BAN HANH KEM THEO NGHI DINH", "NGAY 16 THANG 02 NAM 2015"];
        for (var i = 0; i < overlapped.Length; i++)
            Cv2.PutText(page, overlapped[i], new Point(300, 200 + 60 * i), HersheyFonts.HersheySimplex, 1.0, Scalar.All(0), 2);
        stamp = new ScanLineSegmenter.StampCircle(440, 215, 150);
        // The stamp's ring and its emblem: dense ink inside the disc, so every row of the block holds ink there.
        Cv2.Circle(page, new Point(440, 215), 150, Scalar.All(0), 4);
        for (var y = 80; y < 350; y += 6)
            for (var x = 320; x < 560; x += 6)
                if ((x - 440) * (x - 440) + (y - 215) * (y - 215) < 130 * 130)
                    Cv2.Rectangle(page, new Rect(x, y, 3, 3), Scalar.All(0), -1);
        return page;
    }

    [Fact]
    public void A_stamp_over_a_title_block_leaves_each_title_line_its_own_box()
    {
        using var page = PageWithStampedTitle(out var stamp);

        var title = ScanLineSegmenter.Segment(page, stamp).Where(b => b.Top < 420).OrderBy(b => b.Top).ToArray();

        Assert.True(title.Length == 4, $"expected 4 title lines, got {title.Length}: " + string.Join(" ", title.Select(b => $"[{b.Top}+{b.Height}]")));
        Assert.All(title, b => Assert.True(b.Height < 60));
    }

    [Fact]
    public void A_single_large_line_stays_one_line()
    {
        using var page = new Mat(1200, 1000, MatType.CV_8UC1, Scalar.All(255));
        Cv2.PutText(page, "HEADING", new Point(120, 220), HersheyFonts.HersheySimplex, 3.0, Scalar.All(0), 6);

        var block = ScanLineSegmenter.Segment(page).Where(b => b.Top < 400).ToArray();

        Assert.Single(block);
    }
}
