using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.Sources.Routing;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.Sources.Pdf;
using MessagePack;
using OpenCvSharp;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Common;

namespace DocxHeaderExtractor.Sources.Scan;

/// <summary>
/// The OCR evidence of a scan made without an OCR engine: each page image has its stamp rings removed, and its text lines are the bands of ink
/// found by <see cref="ScanLineSegmenter"/>. The lines carry no OCR text: the vision model's blind transcription gives each line its reading. The
/// evidence has one view, <see cref="ScanOcrPreflight.OriginalView"/>, so the rest of the scan reads it as it reads the OCR evidence.
/// </summary>
public sealed class ScanSegmentPreflight(string storeRoot)
{
    public const string Engine = "opencv-segmenter";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Reads the evidence back when it exists for this source; otherwise makes it from the page images and writes it.</summary>
    public ScanOcrEvidence Run(string docxPath, CancellationToken ct = default)
    {
        var isPdf = UploadedSourceDetector.Detect(docxPath) == SourceType.Pdf;
        var integrity = isPdf ? null : DocxSourceIntegrity.Read(docxPath);
        var sourceSha = integrity?.SourceSha256 ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(docxPath)));
        var sourceKind = integrity?.Kind.ToString() ?? PdfSourceIntegrity.Read(docxPath, ct).Kind;
        var parameters = string.Join("|", ScanOcrEvidence.CurrentVersion, sourceSha, Engine, ScanLineSegmenter.Version, "stamp-rings-removed");
        if (isPdf) parameters += "|" + PdfScanPageRenderer.CacheBasis;
        var parametersSha = TesseractOcr.Sha256(parameters);
        var final = Path.Combine(storeRoot, sourceSha[..16], parametersSha[..12]);
        if (ScanOcrPreflight.TryLoad(final, sourceSha, parametersSha) is { } stored) return stored;

        var pages = isPdf
            ? PdfScanPageRenderer.Render(docxPath, null, ct).Select(p => new ScanOcrPreflight.PageImage(p.Page, p.Bytes, ".png", p.Sha256, p.Width, p.Height)).ToArray()
            : ScanOcrPreflight.PageImages(docxPath, integrity!, null).ToArray();
        var temp = Path.Combine(storeRoot, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "pages"));
        try
        {
            foreach (var page in pages) File.WriteAllBytes(Path.Combine(temp, "pages", $"page-{page.Page:000}{page.Extension}"), page.Bytes);
            // Pages are independent and the stamp search (Hough) takes seconds a page: they run side by side, and keep their order.
            var viewPages = new OcrPage[pages.Length];
            Parallel.For(0, pages.Length, new ParallelOptions { CancellationToken = ct }, k =>
            {
                var page = pages[k];
                using var grey = Cv2.ImDecode(page.Bytes, ImreadModes.Grayscale);
                var (image, _) = ScanLineSegmenter.RemoveStamp(grey);
                using (image)
                {
                    var boxes = ScanLineSegmenter.Segment(image);
                    var lines = boxes.Select((box, i) => new OcrLine($"P{page.Page:000}-{ScanOcrPreflight.OriginalView}-L{i + 1:000}", "", box, 0, 0, false, [])).ToArray();
                    var raw = JsonSerializer.Serialize(boxes, Json);
                    viewPages[k] = new OcrPage(page.Page, page.Sha256, page.Width, page.Height, TesseractOcr.Sha256(raw), lines);
                }
            });
            var spec = new OcrViewSpec(ScanOcrPreflight.OriginalView, "stamp rings removed; text lines are bands of ink (OpenCV)", 1.0, Engine,
                ScanLineSegmenter.Version, "none", 0);
            var evidence = new ScanOcrEvidence(ScanOcrEvidence.CurrentVersion, sourceSha, sourceKind, pages.Length, parametersSha,
                [new OcrView(spec, EvidenceOrigin.OcrDerived, viewPages)],
                [new OcrViewGap(ScanOcrPreflight.UpscaledView, "NOT_MADE_BY_SEGMENTER")])
            { PageImageBasis = isPdf ? PdfScanPageRenderer.CacheBasis : null };
            File.WriteAllBytes(Path.Combine(temp, ScanOcrPreflight.EvidenceFile), MessagePackSerializer.Serialize(evidence, ScanOcrPreflight.EvidenceBinary));
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try { Directory.Move(temp, final); }
            catch (IOException) when (Directory.Exists(final))
            {
                return ScanOcrPreflight.TryLoad(final, sourceSha, parametersSha) ?? throw new InvalidOperationException("SCAN_EVIDENCE_RACE_UNREADABLE");
            }
            return evidence;
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }
}
