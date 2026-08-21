using System.Drawing;
using PDFtoImage;
using SkiaSharp;

namespace DocxHeaderExtractor.Core.Vision;

/// <summary>
/// Kết xuất một vùng crop nhỏ trên một trang PDF thành PNG — mảnh còn thiếu duy nhất để bắt đầu vai
/// trò VLM đã khảo sát (handoff §143): PdfPig chỉ đọc text/letter, không raster hoá. KHÔNG render cả
/// trang rồi cắt sau — PDFtoImage/PDFium render trực tiếp đúng vùng <see cref="RectangleF"/> yêu cầu,
/// đúng ràng buộc phần cứng đã ghi (RTX 3060 12GB, ảnh tốn token gấp nhiều lần văn bản).
/// <para>
/// Toạ độ đầu vào là hệ PDF-NATIVE (gốc dưới-trái, Y tăng lên trên) — cùng hệ mà <c>PdfLine.Y</c> đã
/// dùng ở mọi route PDF khác trong dự án (<c>PdfFinancialReportOutline</c>, <c>PdfStyleClusterProfile</c>...).
/// Gọi trực tiếp từ toạ độ style-cluster/line đã đo được, không cần đổi hệ toạ độ ở nơi gọi; việc lật
/// trục Y sang hệ ảnh (gốc trên-trái) làm bên trong.
/// </para>
/// </summary>
public static class PdfRegionRasterizer
{
    /// <summary>
    /// Render vùng [<paramref name="left"/>, <paramref name="right"/>] × [<paramref name="bottomPdfY"/>,
    /// <paramref name="topPdfY"/>] (điểm PDF, hệ PDF-native) của trang <paramref name="pageNumber1Based"/>
    /// thành PNG. <paramref name="dpi"/> tính TRÊN VÙNG CROP, không phải trên cả trang — khớp khuyến
    /// nghị "hạ DPI 100–120" vì kích thước ảnh cuối phụ thuộc kích thước vùng crop thật, không phải
    /// khổ giấy.
    /// </summary>
    public static byte[] RenderCropPng(
        string pdfPath,
        int pageNumber1Based,
        double left,
        double bottomPdfY,
        double right,
        double topPdfY,
        int dpi = 110)
    {
        if (pageNumber1Based < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber1Based), "Trang tính từ 1.");
        if (right <= left)
            throw new ArgumentException($"right ({right}) phải lớn hơn left ({left}).", nameof(right));
        if (topPdfY <= bottomPdfY)
            throw new ArgumentException(
                $"topPdfY ({topPdfY}) phải lớn hơn bottomPdfY ({bottomPdfY}) — hệ PDF-native, Y tăng lên trên.",
                nameof(topPdfY));

        var pageIndex = pageNumber1Based - 1;
        // Conversion's `string` overload đọc BASE64 CONTENT, không phải đường dẫn file — dùng overload
        // byte[] để tránh nhầm (đã tự vấp lỗi này khi kiểm bằng PDF thật, xem test).
        var pdfBytes = File.ReadAllBytes(pdfPath);
        var pageSize = Conversion.GetPageSize(pdfBytes, pageIndex);

        // Lật trục Y: PDF-native gốc dưới-trái -> hệ ảnh (System.Drawing/PDFtoImage Bounds) gốc trên-trái.
        var top = pageSize.Height - topPdfY;
        var height = topPdfY - bottomPdfY;
        var width = right - left;
        var bounds = new RectangleF((float)left, (float)top, (float)width, (float)height);

        var options = new RenderOptions(
            Dpi: dpi,
            Bounds: bounds,
            DpiRelativeToBounds: true,
            WithAnnotations: false,
            WithFormFill: false);

        using var bitmap = Conversion.ToImage(pdfBytes, pageIndex, password: null, options);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
