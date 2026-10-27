using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Sources.Scan;
using OpenCvSharp;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Sources.Pdf;

internal sealed record PdfGeometryOccurrence(string Alias, int Page, double Left, double Right, double Bottom, double Top);

/// <summary>Raster observations only. Text and source identities always remain the PDF parser's.</summary>
internal static class PdfGeometryMeasurements
{
    public const string Version = "PDF_RASTER_GEOMETRY_V1";
    public static IReadOnlyDictionary<int, JsonObject> MeasureDocument(string path,
        IReadOnlyList<PdfGeometryOccurrence> occurrences, CancellationToken ct = default)
    {
        var groups = occurrences.GroupBy(o => o.Page).ToDictionary(g => g.Key, g => g.ToArray());
        var result = new Dictionary<int, JsonObject>();
        JsonObject Unavailable(int page, string reason) => new()
        { ["version"] = Version, ["page"] = page, ["status"] = "UNAVAILABLE", ["reason"] = reason, ["semanticLabels"] = "NONE" };
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return groups.Keys.ToDictionary(page => page, page => Unavailable(page, "UNSUPPORTED_RENDER_PLATFORM"));
        try
        {
            using var document = PdfDocument.Open(path);
            using var stream = File.OpenRead(path);
            var pageNumber = 0;
            foreach (var bitmap in Conversion.ToImages(stream, leaveOpen: true,
                options: new RenderOptions(Dpi: PdfScanPageRenderer.Dpi, WithAnnotations: true, WithFormFill: true)))
            using (bitmap)
            {
                ct.ThrowIfCancellationRequested();
                pageNumber++;
                if (!groups.TryGetValue(pageNumber, out var members)) continue;
                var page = document.GetPage(pageNumber);
                if (page.Rotation.Value != 0 || Math.Abs(page.CropBox.Bounds.Left) > .01 ||
                    Math.Abs(page.CropBox.Bounds.Bottom) > .01 || Math.Abs(page.CropBox.Bounds.Width - page.Width) > .01 ||
                    Math.Abs(page.CropBox.Bounds.Height - page.Height) > .01)
                { result[pageNumber] = Unavailable(pageNumber, "UNSUPPORTED_COORDINATE_TRANSFORM"); continue; }
                try
                {
                    using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    var bytes = png.ToArray();
                    using var gray = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
                    var facts = Build(gray, pageNumber, page.Width, page.Height,
                        members.Select(o => (o.Alias, o.Left, o.Right, o.Bottom, o.Top)).ToArray());
                    facts["rasterSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    facts["renderer"] = PdfScanPageRenderer.CacheBasis;
                    if (facts["aliasesWithoutInk"]!.AsArray().Count != 0)
                        result[pageNumber] = Unavailable(pageNumber, "MAPPING_HAS_ALIASES_WITHOUT_INK");
                    else { facts["status"] = "AVAILABLE"; result[pageNumber] = facts; }
                }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
                { result[pageNumber] = Unavailable(pageNumber, "MEASUREMENT_FAILED:" + e.GetType().Name); }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            foreach (var page in groups.Keys.Where(p => !result.ContainsKey(p)))
                result[page] = Unavailable(page, "RENDER_FAILED:" + e.GetType().Name);
        }
        ct.ThrowIfCancellationRequested();
        foreach (var page in groups.Keys.Where(p => !result.ContainsKey(p))) result[page] = Unavailable(page, "PAGE_NOT_RENDERED");
        return result;
    }

    internal static JsonObject Build(Mat gray, int page, double widthPt, double heightPt,
        IReadOnlyList<(string Alias, double Left, double Right, double Bottom, double Top)> boxes)
    {
        if (gray.Empty() || widthPt <= 0 || heightPt <= 0) throw new InvalidOperationException("INVALID_RASTER");
        double sx = gray.Width / widthPt, sy = gray.Height / heightPt;
        Rect Map((string Alias, double Left, double Right, double Bottom, double Top) b)
        {
            var x = (int)Math.Floor(b.Left * sx); var y = (int)Math.Floor((heightPt - b.Top) * sy);
            var right = (int)Math.Ceiling(b.Right * sx); var bottom = (int)Math.Ceiling((heightPt - b.Bottom) * sy);
            if (x < -1 || y < -1 || right > gray.Width + 1 || bottom > gray.Height + 1 || right <= x || bottom <= y)
                throw new InvalidOperationException("ALIAS_BOX_OUTSIDE_RASTER:" + b.Alias);
            return new Rect(Math.Max(0, x), Math.Max(0, y), Math.Min(gray.Width, right) - Math.Max(0, x),
                Math.Min(gray.Height, bottom) - Math.Max(0, y));
        }
        var mapped = boxes.Select(b => (b.Alias, Box: Map(b))).ToArray();
        if (mapped.Select(b => b.Alias).Distinct().Count() != mapped.Length) throw new InvalidOperationException("DUPLICATE_ALIAS");
        using var ink = new Mat();
        Cv2.Threshold(gray, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        var frames = ScanFrames.Find(gray);
        var heights = mapped.Select(x => x.Box.Height).Order().ToArray();
        var tolerance = heights.Length == 0 ? 1 : Math.Max(1, heights[heights.Length / 2] * .25);
        int[] BoxArray(Rect b) => [b.X, b.Y, b.Width, b.Height];
        JsonArray Aliases(IEnumerable<string> values) => new(values.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
        var occurrences = new JsonArray();
        var withoutInk = new List<string>();
        foreach (var (alias, box) in mapped)
        {
            using var roi = new Mat(ink, box);
            var inkPixels = Cv2.CountNonZero(roi);
            // Preserve independent ink runs inside a parser alias (e.g. widely separated text on one row).
            // This measures pixels only; it does not split or rewrite the source alias.
            using var columnSums = new Mat();
            Cv2.Reduce(roi, columnSums, ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32S);
            var occupied = Enumerable.Range(0, box.Width).Where(x => columnSums.At<int>(0, x) > 0).ToArray();
            var runs = new List<int[]>();
            if (occupied.Length > 0)
            {
                var first = occupied[0]; var last = first;
                foreach (var x in occupied.Skip(1))
                {
                    if (x - last - 1 >= box.Height) { runs.Add([box.X + first, box.Y, last - first + 1, box.Height]); first = x; }
                    last = x;
                }
                runs.Add([box.X + first, box.Y, last - first + 1, box.Height]);
            }
            if (inkPixels == 0) withoutInk.Add(alias);
            double MidY(Rect r) => r.Y + r.Height / 2.0;
            var rowPeers = mapped.Where(other => other.Alias != alias &&
                Math.Min(box.Bottom, other.Box.Bottom) - Math.Max(box.Top, other.Box.Top) >= .5 * Math.Min(box.Height, other.Box.Height))
                .OrderBy(other => other.Box.Left).Select(other => other.Alias);
            var aligned = mapped.Where(other => other.Alias != alias && Math.Abs(other.Box.Left - box.Left) <= tolerance)
                .OrderBy(other => Math.Abs(MidY(other.Box) - MidY(box))).ToArray();
            var frame = frames.Where(f => f.Contains(box.X + box.Width / 2, box.Y + box.Height / 2))
                .OrderBy(f => (long)f.Width * f.Height).FirstOrDefault();
            occurrences.Add(new JsonObject
            {
                ["alias"] = alias, ["pixelBox"] = JsonSerializer.SerializeToNode(BoxArray(box)),
                ["inkPixelsInBox"] = inkPixels, ["inkFraction"] = Math.Round((double)inkPixels / (box.Width * box.Height), 4),
                ["horizontalInkRunsPx"] = JsonSerializer.SerializeToNode(runs),
                ["inkRunSeparationMinimumPx"] = box.Height,
                ["verticalOverlapPeers"] = Aliases(rowPeers), ["leftAlignedPeerCount"] = aligned.Length,
                ["nearestLeftAlignedPeers"] = Aliases(aligned.Take(4).Select(other => other.Alias)),
                ["enclosingDrawnFrame"] = frame is null ? null : JsonSerializer.SerializeToNode(frame)
            });
        }
        // Whitespace is measured from the rendered page, independently of source text and aliases.
        var blankBands = new List<int[]>();
        using var rowSums = new Mat();
        Cv2.Reduce(ink, rowSums, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32S);
        var rowCount = ink.Rows;
        for (var y = 0; y < rowCount;)
        {
            if (rowSums.At<int>(y, 0) != 0) { y++; continue; }
            var start = y++;
            while (y < rowCount)
            {
                if (rowSums.At<int>(y, 0) != 0) break;
                y++;
            }
            if (y - start >= tolerance) blankBands.Add([start, y]);
        }
        return new JsonObject
        {
            ["version"] = "PDF_RASTER_GEOMETRY_V1", ["page"] = page,
            ["measurementBasis"] = "OpenCV Otsu ink mask, drawn frames and blank horizontal bands; alias boxes transformed from PDF parser points. Peer relations derived from transformed boxes, not from OpenCV semantic recognition.",
            ["semanticLabels"] = "NONE", ["coordinateOrigin"] = "top-left-pixels",
            ["pageWidthPx"] = gray.Width, ["pageHeightPx"] = gray.Height,
            ["pageWidthPt"] = widthPt, ["pageHeightPt"] = heightPt,
            ["transform"] = "x=pdfX*widthPx/widthPt; y=(heightPt-pdfY)*heightPx/heightPt",
            ["leftAlignmentTolerancePx"] = tolerance,
            ["drawnFrames"] = JsonSerializer.SerializeToNode(frames), ["blankHorizontalBandsPx"] = JsonSerializer.SerializeToNode(blankBands),
            ["occurrences"] = occurrences, ["aliasesWithoutInk"] = Aliases(withoutInk)
        };
    }

}

