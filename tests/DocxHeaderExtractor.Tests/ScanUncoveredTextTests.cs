using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using OpenCvSharp;

namespace DocxHeaderExtractor.Tests;

/// <summary>Ink that no OCR line covers is found as rows of letter-sized marks; a stamp, a rule and a covered line are not.</summary>
public sealed class ScanUncoveredTextTests
{
    static Mat Page()
    {
        var page = new Mat(new Size(1000, 1000), MatType.CV_8UC1, Scalar.All(255));
        // A row of letter-sized marks (ten marks 22 px tall, 9 px apart).
        for (var i = 0; i < 10; i++) Cv2.Rectangle(page, new Rect(100 + i * 18, 200, 10, 22), Scalar.All(0), -1);
        // A stamp: a large ring.
        Cv2.Circle(page, new Point(750, 750), 120, Scalar.All(0), 6);
        // A line that OCR covers, drawn as the same kind of row.
        for (var i = 0; i < 10; i++) Cv2.Rectangle(page, new Rect(100 + i * 18, 500, 10, 22), Scalar.All(0), -1);
        return page;
    }

    [Fact]
    public void Letter_rows_outside_covered_boxes_are_found_and_the_stamp_is_not()
    {
        using var page = Page();
        var covered = new[] { new PixelBox(100, 500, 180, 22) };

        var rows = ScanUncoveredText.Find(page, covered);

        var row = Assert.Single(rows);
        Assert.InRange(row.Left, 95, 105);
        Assert.InRange(row.Top, 195, 205);
        Assert.Equal("P023-G001", ScanUncoveredText.Alias(23, 1));
    }

    [Fact]
    public void A_page_with_every_line_covered_has_no_uncovered_rows()
    {
        using var page = Page();
        var covered = new[] { new PixelBox(100, 200, 180, 22), new PixelBox(100, 500, 180, 22) };
        Assert.Empty(ScanUncoveredText.Find(page, covered));
    }
}
