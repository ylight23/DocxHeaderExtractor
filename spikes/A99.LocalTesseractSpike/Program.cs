using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Eval.Accuracy99;
using Tesseract;
using UglyToad.PdfPig;

namespace A99.LocalTesseractSpike;

internal static class Program
{
    private const string EngineVersion = "5.2.0";
    private const string Language = "eng";
    private const string PageSegmentationMode = "SPARSE_TEXT";
    private const string OcrEngineMode = "LSTM_ONLY";
    private const int UserDefinedDpi = 300;

    public static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "extract" => Extract(args),
                "match" => Match(args),
                _ => Usage(),
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"SPIKE_ERROR={error.GetType().Name}: {error.Message}");
            return 2;
        }
    }

    private static int Extract(string[] args)
    {
        if (args.Length != 6)
            return Usage("extract <pdf> <page> <xobject> <tessdata> <raw-output.json>");

        var pdfPath = Path.GetFullPath(args[1]);
        var pageNumber = int.Parse(args[2], CultureInfo.InvariantCulture);
        var xObject = args[3];
        var tessdataPath = Path.GetFullPath(args[4]);
        var outputPath = Path.GetFullPath(args[5]);
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF source not found.", pdfPath);
        if (!Directory.Exists(tessdataPath)) throw new DirectoryNotFoundException(tessdataPath);

        var imageSource = ReadImageSource(pdfPath, pageNumber, xObject);
        var sourceImageOccurrence = imageSource.Occurrence;
        var rawImage = imageSource.RawBytes;

        var rawImageSha = Sha256(rawImage);
        if (!string.Equals(rawImageSha, sourceImageOccurrence.ImageSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Extracted image bytes do not match PdfPig source identity.");

        using var engine = new TesseractEngine(tessdataPath, Language, EngineMode.LstmOnly);
        engine.SetVariable("user_defined_dpi", UserDefinedDpi.ToString(CultureInfo.InvariantCulture));
        using var pix = Pix.LoadFromMemory(rawImage);
        using var pageResult = engine.Process(pix, PageSegMode.SparseText);
        var regions = new List<RawRegion>();
        regions.AddRange(CollectRegions(pageResult, PageIteratorLevel.TextLine, "LINE"));
        regions.AddRange(CollectRegions(pageResult, PageIteratorLevel.Word, "WORD"));

        var artifact = new RawArtifact(
            "a99-ir018-local-tesseract-raw-v1",
            Sha256(File.ReadAllBytes(pdfPath)),
            sourceImageOccurrence.StableOccurrenceId,
            sourceImageOccurrence.ImageSha256,
            sourceImageOccurrence.ImageWidth,
            sourceImageOccurrence.ImageHeight,
            new OcrConfiguration(EngineVersion, "Tesseract NuGet wrapper 5.2.0", Language,
                PageSegmentationMode, OcrEngineMode, UserDefinedDpi, "NONE", Sha256(File.ReadAllBytes(Path.Combine(tessdataPath, "eng.traineddata")))),
            regions);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine($"RAW_OCR_OUTPUT={outputPath}");
        Console.WriteLine($"SOURCE_IMAGE_SHA256={sourceImageOccurrence.ImageSha256}");
        Console.WriteLine($"RAW_REGION_COUNT={regions.Count}");
        return 0;
    }

    private static IReadOnlyList<RawRegion> CollectRegions(Page page, PageIteratorLevel level, string kind)
    {
        using var iterator = page.GetIterator();
        iterator.Begin();
        var regions = new List<RawRegion>();
        var index = 0;
        do
        {
            if (!iterator.TryGetBoundingBox(level, out var rect)) continue;
            var text = iterator.GetText(level) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) continue;
            using var crop = iterator.GetImage(level, 0, out _, out _);
            var cropPath = Path.Combine(Path.GetTempPath(), $"a99-tesseract-{Guid.NewGuid():N}.png");
            crop.Save(cropPath, Tesseract.ImageFormat.Png);
            var cropBytes = File.ReadAllBytes(cropPath);
            File.Delete(cropPath);
            regions.Add(new RawRegion(
                index++, kind, new PixelBBox(rect.X1, rect.Y1, rect.X2, rect.Y2),
                text, iterator.GetConfidence(level), index - 1, Sha256(cropBytes)));
        }
        while (iterator.Next(level));
        return regions;
    }

    private static int Match(string[] args)
    {
        if (args.Length != 7)
            return Usage("match <raw-ocr.json> <pdf> <page> <xobject> <expected-surface> <match-output.json>");

        var raw = JsonSerializer.Deserialize<RawArtifact>(File.ReadAllText(args[1]))
            ?? throw new InvalidDataException("Raw OCR artifact is empty.");
        var imageSource = ReadImageSource(Path.GetFullPath(args[2]), int.Parse(args[3], CultureInfo.InvariantCulture), args[4]);
        if (!string.Equals(raw.SourceImageOccurrenceId, imageSource.Occurrence.StableOccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(raw.SourceImageSha256, imageSource.Occurrence.ImageSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Raw OCR artifact is not from the exact source image supplied to matching.");
        var expected = Normalize(args[5]);
        var lineRegions = raw.AllDetectedRegions.Where(x => x.Kind == "LINE").OrderBy(x => x.SourceOrder).ToArray();
        var candidates = new List<MatchCandidate>();

        for (var start = 0; start < lineRegions.Length; start++)
        {
            var text = string.Empty;
            for (var end = start; end < Math.Min(lineRegions.Length, start + 4); end++)
            {
                if (end > start && !Adjacent(lineRegions[end - 1].PixelBBox, lineRegions[end].PixelBBox)) break;
                text = string.IsNullOrEmpty(text) ? lineRegions[end].RecognizedText : text + " " + lineRegions[end].RecognizedText;
                if (Normalize(text) == expected)
                {
                    var group = lineRegions[start..(end + 1)];
                    var bbox = new PixelBBox(group.Min(x => x.PixelBBox.Left), group.Min(x => x.PixelBBox.Top),
                        group.Max(x => x.PixelBBox.Right), group.Max(x => x.PixelBBox.Bottom));
                    var cropSha = CropPngSha(imageSource.RawBytes, bbox);
                    var pdfBBox = new PdfImagePixelBoundingBox(bbox.Left, bbox.Top, bbox.Right, bbox.Bottom);
                    var stableId = PdfSourceOccurrenceIdentityFactory.CreateImageRegionOccurrenceId(
                        imageSource.Occurrence.StableOccurrenceId, pdfBBox, cropSha);
                    candidates.Add(new MatchCandidate(start, end, text, bbox,
                        group.Select(x => x.RegionIndex).ToArray(), cropSha, stableId));
                }
            }
        }

        var result = new MatchArtifact("a99-ir018-local-tesseract-match-v1", expected,
            candidates.Count == 1 ? "UNIQUE_MATCH" : candidates.Count == 0 ? "NO_MATCH" : "AMBIGUOUS",
            candidates);
        File.WriteAllText(Path.GetFullPath(args[6]), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine($"MATCH_STATUS={result.Status}");
        Console.WriteLine($"MATCH_COUNT={candidates.Count}");
        return candidates.Count == 1 ? 0 : 1;
    }

    private static bool Adjacent(PixelBBox upper, PixelBBox lower) =>
        lower.Top >= upper.Top && lower.Top - upper.Bottom <= Math.Max(8, upper.Height * 1.5) &&
        Math.Abs(lower.Left - upper.Left) <= Math.Max(24, upper.Width * 0.35);

    private static ImageSource ReadImageSource(string pdfPath, int pageNumber, string xObject)
    {
        var pdf = PdfImageSourceOccurrenceReader.Read(pdfPath, "LOCAL-TESSERACT-SPIKE");
        var occurrence = pdf.Occurrences.Single(item =>
            item.Page == pageNumber && string.Equals(item.XObjectResourceName, xObject, StringComparison.Ordinal));
        using var document = PdfDocument.Open(pdfPath);
        var images = document.GetPage(pageNumber).GetImages().ToArray();
        var bytes = images[occurrence.PlacementIndex].RawMemory.ToArray();
        if (!string.Equals(Sha256(bytes), occurrence.ImageSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Extracted image bytes do not match PdfPig source identity.");
        return new ImageSource(occurrence, bytes);
    }

    private static string CropPngSha(byte[] sourceImageBytes, PixelBBox bbox)
    {
        using var input = new MemoryStream(sourceImageBytes, writable: false);
        using var bitmap = new Bitmap(input);
        if (bbox.Left < 0 || bbox.Top < 0 || bbox.Right > bitmap.Width || bbox.Bottom > bitmap.Height)
            throw new InvalidDataException("OCR region is outside the source image pixels.");
        using var crop = bitmap.Clone(new Rectangle(bbox.Left, bbox.Top, bbox.Width, bbox.Height), PixelFormat.Format32bppArgb);
        using var output = new MemoryStream();
        crop.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return Sha256(output.ToArray());
    }

    private static string Normalize(string value)
    {
        value = value.Normalize(NormalizationForm.FormC)
            .Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u201B', '\'').Replace('\u2032', '\'');
        return Regex.Replace(value, "\\s+", " ").Trim();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int Usage(string? details = null)
    {
        if (details is not null) Console.Error.WriteLine($"Usage: {details}");
        Console.Error.WriteLine("Commands: extract <pdf> <page> <xobject> <tessdata> <raw-output.json>");
        Console.Error.WriteLine("         match <raw-ocr.json> <pdf> <page> <xobject> <expected-surface> <match-output.json>");
        return 1;
    }

    private sealed record RawArtifact(string SchemaVersion, string SourcePdfSha256,
        string SourceImageOccurrenceId, string SourceImageSha256, int ImageWidth, int ImageHeight,
        OcrConfiguration Configuration, IReadOnlyList<RawRegion> AllDetectedRegions);
    private sealed record OcrConfiguration(string Engine, string Wrapper, string Language, string PageSegmentationMode,
        string OcrEngineMode, int UserDefinedDpi, string Preprocessing, string LanguageDataSha256);
    private sealed record RawRegion(int RegionIndex, string Kind, PixelBBox PixelBBox, string RecognizedText,
        float Confidence, int SourceOrder, string CropSha256);
    private sealed record PixelBBox(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
    private sealed record MatchArtifact(string SchemaVersion, string ExpectedSurfaceNormalized, string Status,
        IReadOnlyList<MatchCandidate> Candidates);
    private sealed record MatchCandidate(int FirstLineIndex, int LastLineIndex, string RecognizedText,
        PixelBBox PixelBBox, IReadOnlyList<int> ConstituentRegionIndices, string RegionImageSha256,
        string StableOccurrenceId);
    private sealed record ImageSource(PdfImageSourceOccurrence Occurrence, byte[] RawBytes);
}
