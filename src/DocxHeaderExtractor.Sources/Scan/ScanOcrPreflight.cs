using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MessagePack;
using DocumentFormat.OpenXml.Packaging;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.Sources.Routing;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Common;

namespace DocxHeaderExtractor.Sources.Scan;

/// <summary>
/// The OCR preflight of a scanned DOCX or PDF. It runs before any agent conversation, once per source and parameter set, and writes
/// immutable evidence that the evidence tools read later:
/// <list type="bullet">
/// <item>the page images: original DOCX image bytes in body order, or full PDF pages rendered at a recorded DPI, each with its SHA-256;</item>
/// <item>one view per preprocessing: <c>ocr-original</c> (the image as it is) and <c>ocr-upscaled</c> (grey, scaled), each with the engine,
/// its version and parameters, the raw TSV of every page and its hash, and the lines with word boxes and confidences;</item>
/// <item>the views that could not be made, with the reason.</item>
/// </list>
/// Nothing here corrects a reading or chooses between views: two views that read a line differently are both kept, and <see cref="OcrViewComparer"/>
/// only says where they differ. The store directory is keyed by the source hash and the parameter hash; an existing entry is read back, never
/// rewritten.
/// </summary>
public sealed class ScanOcrPreflight(string storeRoot)
{
    public const string OriginalView = "ocr-original", UpscaledView = "ocr-upscaled";

    /// <param name="Parallelism">Engine processes at once over both views (each runs one thread); null for the machine's processor count. It is
    /// not part of the evidence's parameters: the same pages read the same at any parallelism.</param>
    public sealed record Options(string Languages = "vie+eng", int PageSegmentationMode = 1, double UpscaleFactor = 1.5, int? MaxPages = null,
        int? Parallelism = null);

    /// <summary>UTF-8 without a byte order mark, so that the files read the same in every tool.</summary>
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The evidence entry file: MessagePack, written and read with the standard resolver.</summary>
    internal const string EvidenceFile = "evidence.msgpack";

    internal static readonly MessagePackSerializerOptions EvidenceBinary = MessagePackSerializerOptions.Standard;

    /// <summary>Reads the evidence back when it exists for this source and these parameters; otherwise makes it and writes it.</summary>
    public ScanOcrEvidence Run(string docxPath, Options? options = null, CancellationToken ct = default)
    {
        using var cancellation = TesseractOcr.WithCancellation(ct);
        ct.ThrowIfCancellationRequested();
        options ??= new Options();
        var isPdf = UploadedSourceDetector.Detect(docxPath) == SourceType.Pdf;
        var integrity = isPdf ? null : DocxSourceIntegrity.Read(docxPath);
        var sourceSha = integrity?.SourceSha256 ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(docxPath)));
        var sourceKind = integrity?.Kind.ToString() ?? PdfSourceIntegrity.Read(docxPath, ct).Kind;
        var tesseract = TesseractOcr.FindTesseract() ?? throw new InvalidOperationException("TESSERACT_NOT_FOUND");
        var engineVersion = TesseractOcr.Version(tesseract);
        var installed = TesseractOcr.Languages(tesseract);
        var missing = options.Languages.Split('+').Where(l => !installed.Contains(l)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("TESSERACT_LANGUAGE_MISSING:" + string.Join(",", missing));
        var magick = TesseractOcr.FindImageMagick();

        var parameters = string.Join("|", ScanOcrEvidence.CurrentVersion, sourceSha, engineVersion, options.Languages,
            options.PageSegmentationMode, options.UpscaleFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture), options.MaxPages, magick is not null);
        if (isPdf) parameters += "|" + PdfScanPageRenderer.CacheBasis;
        var parametersSha = TesseractOcr.Sha256(parameters);
        var final = Path.Combine(storeRoot, sourceSha[..16], parametersSha[..12]);
        if (TryLoad(final, sourceSha, parametersSha) is { } stored) return stored;

        var temp = Path.Combine(storeRoot, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "pages"));
        try
        {
            var pages = isPdf
                ? PdfScanPageRenderer.Render(docxPath, options.MaxPages, ct).Select(p => new PageImage(p.Page, p.Bytes, ".png", p.Sha256, p.Width, p.Height)).ToArray()
                : PageImages(docxPath, integrity!, options.MaxPages);
            var imagePaths = pages.Select(p =>
            {
                var file = Path.Combine(temp, "pages", $"page-{p.Page:000}{p.Extension}");
                File.WriteAllBytes(file, p.Bytes);
                return file;
            }).ToArray();

            var views = new List<OcrView>();
            var gaps = new List<OcrViewGap>();
            if (pages.Count == 0) gaps.Add(new OcrViewGap(OriginalView, "NO_PAGE_IMAGES:" + sourceKind));
            else
            {
                var specs = new List<(OcrViewSpec Spec, string? Magick)>
                {
                    (new OcrViewSpec(OriginalView, "none", 1.0, TesseractOcr.EngineName, engineVersion, options.Languages, options.PageSegmentationMode), null),
                };
                if (magick is null) gaps.Add(new OcrViewGap(UpscaledView, "IMAGEMAGICK_NOT_FOUND"));
                else
                    specs.Add((new OcrViewSpec(UpscaledView, "grey, scaled " + options.UpscaleFactor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x (ImageMagick)",
                        options.UpscaleFactor, TesseractOcr.EngineName, engineVersion, options.Languages, options.PageSegmentationMode), magick));
                views.AddRange(MakeViews(specs, pages, imagePaths, tesseract, temp, Math.Max(1, options.Parallelism ?? Environment.ProcessorCount), ct));
            }
            var evidence = new ScanOcrEvidence(ScanOcrEvidence.CurrentVersion, sourceSha, sourceKind, pages.Count, parametersSha, views, gaps)
            { PageImageBasis = isPdf ? PdfScanPageRenderer.CacheBasis : null };
            ct.ThrowIfCancellationRequested();
            File.WriteAllBytes(Path.Combine(temp, EvidenceFile), MessagePackSerializer.Serialize(evidence, EvidenceBinary));
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try { Directory.Move(temp, final); }
            catch (IOException) when (Directory.Exists(final))
            {
                // Another run wrote the same entry first; it is the same evidence, so it is read back and this one is dropped.
                return TryLoad(final, sourceSha, parametersSha) ?? throw new InvalidOperationException("SCAN_EVIDENCE_RACE_UNREADABLE");
            }
            return evidence;
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>The store entry that holds an evidence (its page images are in its pages directory).</summary>
    public string EntryOf(ScanOcrEvidence evidence) => Path.Combine(storeRoot, evidence.SourceSha256[..16], evidence.ParametersSha256[..12]);

    /// <summary>The stored image of one page of an entry.</summary>
    public static string? PageImagePath(string entry, int page) =>
        Directory.Exists(Path.Combine(entry, "pages")) ? Directory.GetFiles(Path.Combine(entry, "pages"), $"page-{page:000}.*").FirstOrDefault() : null;

    /// <summary>The evidence stored in an entry, when it is there and is for this source and these parameters.</summary>
    public static ScanOcrEvidence? TryLoad(string entry, string sourceSha256, string parametersSha256)
    {
        var file = Path.Combine(entry, EvidenceFile);
        if (!File.Exists(file)) return null;
        var evidence = MessagePackSerializer.Deserialize<ScanOcrEvidence>(File.ReadAllBytes(file), EvidenceBinary)
            ?? throw new InvalidOperationException("SCAN_EVIDENCE_UNREADABLE");
        if (evidence.SourceSha256 != sourceSha256 || evidence.ParametersSha256 != parametersSha256 || evidence.Version != ScanOcrEvidence.CurrentVersion)
            throw new InvalidOperationException("SCAN_EVIDENCE_ENTRY_MISMATCH");
        return evidence;
    }

    internal sealed record PageImage(int Page, byte[] Bytes, string Extension, string Sha256, int Width, int Height);

    /// <summary>The images that cover a page, in body order, numbered from 1.</summary>
    internal static IReadOnlyList<PageImage> PageImages(string docxPath, DocxSourceIntegrity integrity, int? maxPages)
    {
        using var doc = WordprocessingDocument.Open(docxPath, false);
        var main = doc.MainDocumentPart!;
        var result = new List<PageImage>();
        foreach (var image in integrity.BodyImages.Where(i => i.CoversPage))
        {
            if (maxPages is { } max && result.Count >= max) break;
            var part = main.GetPartById(image.RelationshipId);
            using var stream = part.GetStream();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var (w, h) = TesseractOcr.ImageSize(bytes);
            var ext = part.ContentType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/tiff" => ".tif", _ => ".bin" };
            result.Add(new PageImage(result.Count + 1, bytes, ext, Convert.ToHexStringLower(SHA256.HashData(bytes)), w, h));
        }
        return result;
    }

    /// <summary>Every page of every view as one queue of engine runs, the heaviest first (the upscaled view's pages take about twice as long), so
    /// that no worker waits while another view still has pages.</summary>
    static IEnumerable<OcrView> MakeViews(IReadOnlyList<(OcrViewSpec Spec, string? Magick)> specs, IReadOnlyList<PageImage> pages, string[] imagePaths,
        string tesseract, string temp, int parallelism, CancellationToken ct)
    {
        var result = specs.Select(_ => new OcrPage[pages.Count]).ToArray();
        foreach (var (spec, _) in specs) Directory.CreateDirectory(Path.Combine(temp, "views", spec.ViewId));
        var jobs = Enumerable.Range(0, specs.Count).OrderByDescending(v => specs[v].Spec.Scale)
            .SelectMany(v => Enumerable.Range(0, pages.Count).Select(k => (View: v, Page: k))).ToArray();
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(jobs, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, job =>
        {
            var (spec, magick) = specs[job.View];
            var k = job.Page;
            var dir = Path.Combine(temp, "views", spec.ViewId);
            var input = imagePaths[k];
            if (magick is not null)
            {
                var scaled = Path.Combine(dir, $"page-{pages[k].Page:000}.png");
                if (!TesseractOcr.Upscale(magick, input, scaled, spec.Scale)) throw new InvalidOperationException("IMAGEMAGICK_FAILED:page " + pages[k].Page);
                input = scaled;
            }
            var tsv = TesseractOcr.RecognizeTsv(tesseract, input, spec.Languages, spec.PageSegmentationMode);
            File.WriteAllText(Path.Combine(dir, $"page-{pages[k].Page:000}.tsv"), tsv, Utf8);
            if (magick is not null) File.Delete(input);   // the scaled image is reproducible from the original and the spec
            result[job.View][k] = new OcrPage(pages[k].Page, pages[k].Sha256, pages[k].Width, pages[k].Height, TesseractOcr.Sha256(tsv),
                Lines(pages[k].Page, spec.ViewId, TesseractOcr.ParseTsv(tsv, spec.Scale)));
        });
        return specs.Select((s, v) => new OcrView(s.Spec, EvidenceOrigin.OcrDerived, result[v]));
    }

    /// <summary>The lines of one page with their measurements: box, mean and lowest confidence, and whether another line overlaps them.</summary>
    internal static IReadOnlyList<OcrLine> Lines(int page, string viewId, IReadOnlyList<(string Key, IReadOnlyList<OcrWord> Words)> raw)
    {
        var boxes = raw.Select(l => PixelBox.Union(l.Words.Select(w => w.Box))).ToArray();
        return raw.Select((l, i) =>
        {
            var text = string.Join(" ", l.Words.Select(w => w.Text));
            var nfc = text.Normalize(NormalizationForm.FormC);
            return new OcrLine($"P{page:000}-{viewId}-L{i + 1:000}", text, boxes[i],
                Math.Round(l.Words.Average(w => w.Confidence), 1), Math.Round(l.Words.Min(w => w.Confidence), 1),
                Enumerable.Range(0, boxes.Length).Any(j => j != i && Overlap(boxes[i], boxes[j])), l.Words) { TextNfc = nfc == text ? null : nfc };
        }).ToArray();
    }

    /// <summary>Two lines overlap when their horizontal ranges meet and they share at least <see cref="OverlapShare"/> of the shorter one's height.</summary>
    public const double OverlapShare = 0.3;

    static bool Overlap(PixelBox a, PixelBox b)
    {
        if (Math.Min(a.Right, b.Right) <= Math.Max(a.Left, b.Left)) return false;
        var shared = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return shared > 0 && shared >= OverlapShare * Math.Min(a.Height, b.Height);
    }
}

/// <summary>
/// Where two views of the same pages read a line differently. Lines are paired by the overlap of their boxes (in the pixels of the original
/// image); the words of a pair are compared after Unicode NFC, and every stretch where they differ is reported with both readings and the status
/// <see cref="VerificationStatus.Unverified"/>. Nothing is chosen: which reading is right is not this class's to say.
/// </summary>
public static class OcrViewComparer
{
    public const double MinIoU = 0.5;

    public static IReadOnlyList<AlignedLine> Compare(OcrView a, OcrView b)
    {
        var result = new List<AlignedLine>();
        var pagesB = b.Pages.ToDictionary(p => p.Page);
        foreach (var page in a.Pages)
        {
            var others = pagesB.TryGetValue(page.Page, out var pb) ? pb.Lines : [];
            var used = new HashSet<int>();
            foreach (var line in page.Lines)
            {
                var best = Enumerable.Range(0, others.Count).Where(j => !used.Contains(j))
                    .Select(j => (j, IoU: line.Box.IoU(others[j].Box))).Where(x => x.IoU >= MinIoU).OrderByDescending(x => x.IoU).FirstOrDefault();
                if (best.IoU < MinIoU) { result.Add(new AlignedLine(page.Page, line.Id, null, 0, false, [])); continue; }
                used.Add(best.j);
                var diffs = Differences(Tokens(line.Text), Tokens(others[best.j].Text));
                result.Add(new AlignedLine(page.Page, line.Id, others[best.j].Id, Math.Round(best.IoU, 2), diffs.Count == 0, diffs));
            }
        }
        return result;
    }

    static string[] Tokens(string text) => text.Normalize(NormalizationForm.FormC).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The stretches between the longest common subsequence of the two token lists.</summary>
    internal static IReadOnlyList<TextDifference> Differences(string[] x, string[] y)
    {
        var lcs = new int[x.Length + 1, y.Length + 1];
        for (int i = x.Length - 1; i >= 0; i--)
            for (int j = y.Length - 1; j >= 0; j--)
                lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var result = new List<TextDifference>();
        int a = 0, b = 0, sa = 0, sb = 0;
        void Flush()
        {
            if (a > sa || b > sb)
                result.Add(new TextDifference(string.Join(" ", x[sa..a]), string.Join(" ", y[sb..b]), VerificationStatus.Unverified));
        }
        while (a < x.Length || b < y.Length)
        {
            if (a < x.Length && b < y.Length && x[a] == y[b]) { Flush(); a++; b++; sa = a; sb = b; }
            else if (b >= y.Length || (a < x.Length && lcs[a + 1, b] >= lcs[a, b + 1])) a++;
            else b++;
        }
        Flush();
        return result;
    }
}
