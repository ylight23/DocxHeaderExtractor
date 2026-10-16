using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Source.Docx;

namespace DocxHeaderExtractor.Web;

/// <summary>Local source measurements for the upload UI. The server repeats them before extraction; no client route claim is trusted.</summary>
public sealed record SourceInspection(string Format, string Kind, string Route, bool RequiresOcr, bool Supported,
    bool RequiresOpenRouter, bool CanWriteback, string Message, long TextChars = 0, int PageImages = 0)
{
    public static SourceInspection Read(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        switch (UploadedSourceDetector.Detect(path))
        {
            case SourceType.Docx:
                var docx = DocxSourceIntegrity.Read(path);
                return docx.Kind switch
                {
                    DocxSourceKind.ScannedImageOnly => new("docx", "scan", "scan", true, true, true, false,
                        $"Bản scan trong Word: {docx.PageImages} ảnh trang. Tự dùng OCR → VLM chép chữ → Qwen chọn heading.", docx.TextChars, docx.PageImages),
                    DocxSourceKind.Mixed => new("docx", "mixed", "docx", false, true, false, true,
                        "Word có văn bản và ảnh trang. Dùng lớp văn bản; các ảnh xen kẽ chưa được OCR.", docx.TextChars, docx.PageImages),
                    _ => new("docx", "native", "docx", false, true, false, true,
                        "Word có văn bản: dùng route DOCX, không cần OCR.", docx.TextChars, docx.PageImages),
                };
            case SourceType.Pdf:
                using (var pdf = UglyToad.PdfPig.PdfDocument.Open(path))
                {
                    long text = 0;
                    int imageOnlyPages = 0;
                    foreach (var page in pdf.GetPages())
                    {
                        ct.ThrowIfCancellationRequested();
                        var chars = page.Letters.Sum(l => (long)l.Value.Count(c => !char.IsWhiteSpace(c)));
                        text += chars;
                        // A logo is not a scanned page. A page-sized image with only a footer/header still needs OCR.
                        var pageImage = page.NumberOfImages > 0 && page.GetImages().Any(image =>
                            image.BoundingBox.Width >= DocxSourceIntegrity.PageCoverage * page.Width &&
                            image.BoundingBox.Height >= DocxSourceIntegrity.PageCoverage * page.Height);
                        if (chars < DocxSourceIntegrity.TextCharsPerPageImage && pageImage) imageOnlyPages++;
                    }
                    if (imageOnlyPages > 0)
                        return new("pdf", text == 0 ? "scan" : "mixed", "pdf-scan", true, false, true, false,
                            $"PDF có {imageOnlyPages} trang ảnh không có hoặc có rất ít chữ trích xuất được, cần OCR. Web hiện hỗ trợ scan trong DOCX; hãy dùng bản DOCX chứa ảnh trang.", text, imageOnlyPages);
                    if (text == 0)
                        return new("pdf", "empty", "pdf", false, false, false, false,
                            "PDF không có chữ trích xuất được và không nhận diện được ảnh trang để OCR.");
                    return new("pdf", "native", "pdf", false, true, true, true,
                        "PDF có lớp chữ: dùng route PDF, không cần OCR.", text);
                }
            default:
                return new("unknown", "conversion", "docx", false, true, false, true,
                    "Định dạng cần chuyển sang DOCX trước khi xử lý; loại nguồn sẽ được kiểm tra sau chuyển đổi.");
        }
    }
}
