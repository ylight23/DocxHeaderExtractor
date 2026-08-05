using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// "PHẦN I. …", "Chương 2. …", "Điều 5. …" là một document number format, phải nhận ra bằng HÌNH
/// DẠNG chứ không bằng danh sách từ khoá. Bản cũ dùng danh sách cứng (chương|phần|mục|điều|
/// chapter|section|…) và nằm sau cờ luật-từ-ngữ, mà giao diện lại mặc định TẮT cờ đó — nên ở đúng
/// cấu hình chạy thật, "PHẦN I. CƠ SỞ LÝ LUẬN" không được một điểm đánh số nào.
/// </summary>
public sealed class LabelledNumberFormatTests
{
    private static SlimParagraph Line(string text) => new()
    {
        Index = 0,
        StableId = "body[1]/p[1]",
        Text = text,
        StyleId = "Normal",
        StyleName = "Normal",
        FontSizePt = 13,
        BodyFontSizePt = 13,
    };

    private static SlimParagraph Classified(string text, bool lexical)
    {
        var p = Line(text);
        HeadingHeuristics.Classify(p, new ExtractionOptions { UseLexicalRules = lexical });
        return p;
    }

    [Theory]
    // Tiếng Việt, La Mã và Ả Rập, dấu ngắt khác nhau.
    [InlineData("PHẦN I. CƠ SỞ LÝ LUẬN")]
    [InlineData("Chương 2. Phương pháp nghiên cứu")]
    [InlineData("Điều 5. Trách nhiệm của các bên")]
    // Từ nhãn không nằm trong bất kỳ danh sách nào — đó chính là điểm của luật hình dạng.
    [InlineData("Quyển 3. Hồ sơ thiết kế")]
    [InlineData("Abschnitt 4. Technische Anforderungen")]
    [InlineData("Section 3: Design constraints")]
    // Không dấu ngắt sau số — phần tên viết hoa là thứ phân biệt với câu văn có số.
    [InlineData("Chương 1 Tổng quan")]
    public void Dang_tu_nhan_kem_so_duoc_nhan_ke_ca_khi_tat_luat_tu_ngu(string text)
    {
        // Cấu hình mà giao diện chạy mặc định: luật từ ngữ TẮT.
        var p = Classified(text, lexical: false);

        Assert.Equal(ParagraphRole.HeadingCandidate, p.Role);
        Assert.True(p.Score >= 0.55, $"điểm {p.Score} — thiếu điểm đánh số cấu trúc");
    }

    [Theory]
    // Sau số là dấu gạch chéo, không phải dấu ngắt mục.
    [InlineData("Ngày 14/01/2026 các đơn vị báo cáo tình hình thực hiện nhiệm vụ được giao")]
    // Không có phần tên mục theo sau.
    [InlineData("Trang 5")]
    // Câu văn có số, phần sau viết thường.
    [InlineData("Ngày 14 tháng 01 năm 2026 các đơn vị hoàn thành việc báo cáo số liệu.")]
    // Chú thích hình/bảng: đúng hình dạng nhưng là caption.
    [InlineData("Hình 3. Sơ đồ khối của hệ thống")]
    public void Khong_nham_voi_cau_van_va_chu_thich(string text)
    {
        var p = Classified(text, lexical: false);

        // Kiểm ĐIỂM chứ không kiểm Role: một dòng có thể vẫn lọt vào diện hỏi qua luật vớt dòng
        // độc lập (lớp ứng viên yếu nhất, điểm đúng bằng ngưỡng 0,45). Thứ đang kiểm ở đây là luật
        // đánh số không được cộng 0,55 cho những dòng này.
        Assert.True(p.Score < 0.55, $"điểm {p.Score} — đã cộng nhầm điểm đánh số cấu trúc");
    }
}
