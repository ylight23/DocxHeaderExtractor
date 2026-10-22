using DocxHeaderExtractor.Sources.Scan;
using OpenCvSharp;

namespace DocxHeaderExtractor.Tests;

/// <summary>The drawn frames of a page are a measurement of the image: a rectangle drawn on the page is found, a lone rule is not.</summary>
public sealed class ScanFramesTests
{
    static Mat Page()
    {
        var page = new Mat(new Size(1000, 1400), MatType.CV_8UC1, Scalar.All(255));
        // Text-like marks outside any frame.
        for (var y = 100; y < 300; y += 40) Cv2.Rectangle(page, new Rect(120, y, 400, 12), Scalar.All(0), -1);
        return page;
    }

    [Fact]
    public void A_drawn_rectangle_is_a_frame_and_a_lone_rule_is_not()
    {
        using var page = Page();
        Cv2.Rectangle(page, new Rect(100, 500, 800, 400), Scalar.All(0), 3);
        Cv2.Line(page, new Point(300, 1100), new Point(700, 1100), Scalar.All(0), 3);   // an underline: one direction only

        var frames = ScanFrames.Find(page);

        var frame = Assert.Single(frames);
        Assert.InRange(frame.Left, 95, 105);
        Assert.InRange(frame.Top, 495, 505);
        Assert.True(frame.Contains(500, 700));
        Assert.False(frame.Contains(500, 200));
    }

    [Fact]
    public void A_page_with_no_drawing_has_no_frame()
    {
        using var page = Page();
        Assert.Empty(ScanFrames.Find(page));
    }
}
