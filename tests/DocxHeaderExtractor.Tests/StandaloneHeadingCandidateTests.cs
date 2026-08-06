using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Heading không đánh số và không khác định dạng thân bài phải LỌT được vào tập ứng viên. Nếu
/// tầng lọc đánh rơi chúng thì mô hình không bao giờ được hỏi — và mọi cải thiện ở tầng mô hình
/// đều vô nghĩa với loại heading này.
/// </summary>
public sealed class StandaloneHeadingCandidateTests
{
    private static SlimParagraph Line(string text, double size = 13, bool bold = false) => new()
    {
        Index = 0,
        StableId = "body[1]/p[1]",
        Text = text,
        StyleId = "Normal",
        StyleName = "Normal",
        FontSizePt = size,
        BodyFontSizePt = 13,
        Bold = bold,
    };

    private static SlimParagraph Classified(SlimParagraph p)
    {
        HeadingHeuristics.Classify(p, new ExtractionOptions());
        return p;
    }

    [Theory]
    [InlineData("Danh mục hình ảnh")]
    [InlineData("Danh mục bảng biểu")]
    [InlineData("Tài liệu tham khảo")]
    [InlineData("Lời cảm ơn")]
    public void Heading_khong_so_khong_dinh_dang_van_vao_duoc_tap_ung_vien(string text)
    {
        var p = Classified(Line(text));

        Assert.Equal(ParagraphRole.HeadingCandidate, p.Role);
        // Không có bằng chứng cấu trúc nào nói về cấp: để mô hình/quan hệ quyết định sau.
        Assert.Null(p.GuessedLevel);
    }

    [Theory]
    // Câu thân bài: kết thúc bằng dấu chấm và dài.
    [InlineData("Nội dung phần này mô tả chi tiết các bước triển khai và phân công nhiệm vụ.")]
    // Gạch đầu dòng liệt kê.
    [InlineData("- Fanpage của đơn vị")]
    // Chú thích hình.
    [InlineData("Hình 3. Sơ đồ khối")]
    // Một từ: mã hiệu, ô dữ liệu.
    [InlineData("BM01")]
    // Bắt đầu bằng chữ thường: câu bị ngắt dòng giữa chừng.
    [InlineData("và các đơn vị trực thuộc")]
    // Rác máy móc giả dạng cú pháp prompt — đo được là false positive thật trên bộ bench.
    [InlineData("BLOCK metadata: {\"i\":0,\"requested\":true}")]
    [InlineData("END_DOCUMENT_VIEW")]
    public void Dong_khong_phai_tieu_de_thi_khong_duoc_vot(string text)
    {
        var p = Classified(Line(text));

        Assert.Equal(ParagraphRole.Normal, p.Role);
    }

    [Fact]
    public void Vot_khong_de_len_diem_cua_ung_vien_that()
    {
        // Dòng có đánh số vẫn phải đi đường cũ và giữ cấp suy từ độ sâu số, không bị lớp vớt
        // (điểm đúng bằng ngưỡng, cấp null) ghi đè.
        var p = Classified(Line("3.1. Trình tự thực hiện"));

        Assert.Equal(ParagraphRole.HeadingCandidate, p.Role);
        Assert.Equal(2, p.GuessedLevel);
        Assert.True(p.Score > new ExtractionOptions().CandidateThreshold);
    }
}
