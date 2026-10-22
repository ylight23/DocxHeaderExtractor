using DocxHeaderExtractor.Sources.Docx;

namespace DocxHeaderExtractor.Sources.Pdf;

/// <summary>Source measurements only: a large page image with sparse extractable text needs OCR, even if it has a native footer.</summary>
public sealed record PdfSourceIntegrity(int Pages, long TextChars, IReadOnlyList<int> ScanPages)
{
    public bool RequiresOcr => ScanPages.Count > 0;
    public string Kind => ScanPages.Count == Pages && Pages > 0 ? "ScannedPdf" : RequiresOcr ? "MixedPdf" : "NativePdf";

    public static PdfSourceIntegrity Read(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        long text = 0;
        var scans = new List<int>();
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var chars = page.Letters.Sum(l => (long)l.Value.Count(c => !char.IsWhiteSpace(c)));
            text += chars;
            var pageImage = page.NumberOfImages > 0 && page.GetImages().Any(image =>
                image.BoundingBox.Width >= DocxSourceIntegrity.PageCoverage * page.Width &&
                image.BoundingBox.Height >= DocxSourceIntegrity.PageCoverage * page.Height);
            if (chars < DocxSourceIntegrity.TextCharsPerPageImage && pageImage) scans.Add(page.Number);
        }
        return new(pdf.NumberOfPages, text, scans);
    }
}
