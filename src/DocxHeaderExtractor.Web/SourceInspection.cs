using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Intake;
using DocxHeaderExtractor.Sources.Detect;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.Sources.Pdf;

namespace DocxHeaderExtractor.Web;

/// <summary>Local source measurements for the upload UI. The server repeats them before extraction; no client route claim is trusted.</summary>
public sealed record SourceInspection(string Format, string Kind, string Route, bool RequiresOcr, bool Supported,
    bool RequiresOpenRouter, bool CanWriteback, string Message, long TextChars = 0, int PageImages = 0)
{
    public static SourceInspection Read(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        switch (SourceDetector.Detect(path))
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
                var pdf = PdfSourceIntegrity.Read(path, ct);
                {
                    if (pdf.RequiresOcr)
                        return new("pdf", pdf.ScanPages.Count == pdf.Pages ? "scan" : "mixed", "scan", true, true, true, false,
                            $"PDF có {pdf.ScanPages.Count}/{pdf.Pages} trang cần OCR. Tự render toàn bộ trang → OCR → VLM chép chữ → Qwen chọn heading.", pdf.TextChars, pdf.ScanPages.Count);
                    if (pdf.TextChars == 0)
                        return new("pdf", "empty", "pdf", false, false, false, false,
                            "PDF không có chữ trích xuất được và không nhận diện được ảnh trang để OCR.");
                    return new("pdf", "native", "pdf", false, true, true, true,
                        "PDF có lớp chữ: dùng route PDF, không cần OCR.", pdf.TextChars);
                }
            default:
                return new("unknown", "conversion", "docx", false, true, false, true,
                    "Định dạng cần chuyển sang DOCX trước khi xử lý; loại nguồn sẽ được kiểm tra sau chuyển đổi.");
        }
    }
}
