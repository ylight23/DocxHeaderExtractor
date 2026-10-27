using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.Sources.Scan;
using OpenCvSharp;
using UglyToad.PdfPig;

/// <summary>Qualification-only PDF raster observations. Never reads text to infer semantic labels.</summary>
internal static class PdfRasterGeometry
{
    public static JsonObject Measure(string pdf, AgentSource source, int pageNumber, string output)
    {
        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(pageNumber);
        // Fail closed until rotated/cropped pages have a separately verified coordinate transform.
        if (page.Rotation.Value != 0 || Math.Abs(page.CropBox.Bounds.Left) > .01 || Math.Abs(page.CropBox.Bounds.Bottom) > .01 ||
            Math.Abs(page.CropBox.Bounds.Width - page.Width) > .01 || Math.Abs(page.CropBox.Bounds.Height - page.Height) > .01)
            throw new InvalidOperationException("UNSUPPORTED_RASTER_COORDINATE_TRANSFORM");
        var raster = PdfScanPageRenderer.Render(pdf, pageNumber).Single(p => p.Page == pageNumber);
        File.WriteAllBytes(Path.Combine(output, $"page-{pageNumber:00}.png"), raster.Bytes);
        using var gray = Cv2.ImDecode(raster.Bytes, ImreadModes.Grayscale);
        var boxes = source.Lines.Where(l => l.Page == pageNumber).Select(l =>
        {
            var b = l.Bbox ?? throw new InvalidOperationException("SOURCE_BBOX_MISSING");
            return (l.Alias, Left: b.GetProperty("left").GetDouble(), Right: b.GetProperty("right").GetDouble(),
                Bottom: b.GetProperty("bottom").GetDouble(), Top: b.GetProperty("top").GetDouble());
        }).ToArray();
        var result = Build(gray, pageNumber, page.Width, page.Height, boxes);
        result["rasterSha256"] = raster.Sha256;
        result["renderer"] = PdfScanPageRenderer.CacheBasis;
        using var overlay = new Mat();
        Cv2.CvtColor(gray, overlay, ColorConversionCodes.GRAY2BGR);
        foreach (var item in result["occurrences"]!.AsArray())
        {
            var b = item!["pixelBox"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
            Cv2.Rectangle(overlay, new Rect(b[0], b[1], b[2], b[3]), new Scalar(0, 0, 220), 1);
            Cv2.PutText(overlay, item["alias"]!.GetValue<string>(), new Point(b[0], Math.Max(12, b[1] - 2)),
                HersheyFonts.HersheySimplex, .30, new Scalar(200, 40, 0), 1);
        }
        Cv2.ImWrite(Path.Combine(output, $"page-{pageNumber:00}-mapping.png"), overlay);
        return result;
    }

    internal static JsonObject Build(Mat gray, int page, double widthPt, double heightPt,
        IReadOnlyList<(string Alias, double Left, double Right, double Bottom, double Top)> boxes) =>
        DocxHeaderExtractor.Sources.Pdf.PdfGeometryMeasurements.Build(gray, page, widthPt, heightPt, boxes);

    public static void SelfTest()
    {
        using var image = new Mat(200, 200, MatType.CV_8UC1, Scalar.White);
        Cv2.Rectangle(image, new Rect(10, 20, 40, 10), Scalar.Black, -1);
        Cv2.Rectangle(image, new Rect(80, 20, 50, 10), Scalar.Black, -1);
        Cv2.Rectangle(image, new Rect(10, 60, 40, 10), Scalar.Black, -1);
        var data = Build(image, 1, 100, 100, [("a", 5, 25, 85, 90), ("b", 40, 65, 85, 90), ("c", 5, 25, 65, 70)]);
        var a = data["occurrences"]![0]!;
        if (a["pixelBox"]!.ToJsonString() != "[10,20,40,10]" || a["verticalOverlapPeers"]!.ToJsonString() != "[\"b\"]" ||
            a["leftAlignedPeerCount"]!.GetValue<int>() != 1 || data["aliasesWithoutInk"]!.AsArray().Count != 0)
            throw new InvalidOperationException("GEOMETRY_SYNTHETIC_TEST_FAILED");
        var wide = Build(image, 1, 100, 100, [("wide", 5, 65, 85, 90)]);
        if (wide["occurrences"]![0]!["horizontalInkRunsPx"]!.AsArray().Count != 2)
            throw new InvalidOperationException("GEOMETRY_INTRA_ALIAS_INK_RUNS_TEST_FAILED");
        try { Build(image, 1, 100, 100, [("outside", -10, 20, 85, 90)]); }
        catch (InvalidOperationException e) when (e.Message.StartsWith("ALIAS_BOX_OUTSIDE_RASTER:")) { return; }
        throw new InvalidOperationException("GEOMETRY_OUTSIDE_BOX_NOT_REJECTED");
    }
}
