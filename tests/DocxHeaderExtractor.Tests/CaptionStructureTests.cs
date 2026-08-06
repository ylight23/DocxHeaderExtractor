using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Chú thích bảng nhận diện bằng CẤU TRÚC chứ không bằng danh sách từ khoá. Lý do đổi: CaptionRx
/// nằm sau cờ UseLexicalRules, mà giao diện web mặc định TẮT cờ đó — tức ở chế độ chạy thật không
/// còn bộ lọc chú thích nào. Đo trên một báo cáo thực tập thật (1183 đoạn, đáp án 61 heading):
/// 13 chú thích bị tác giả gán style Heading3 nên vào thẳng tập ứng viên với điểm 1.0.
/// </summary>
public sealed class CaptionStructureTests
{
    [Fact]
    public void Chu_thich_dung_truoc_bang_bi_loai_du_mang_style_heading()
    {
        var p = Caption(0, "Bảng 1.2: Tình hình huy động vốn giai đoạn 2022-2024");
        p.PrecedesTable = true;

        HeadingHeuristics.Classify(p, new ExtractionOptions { UseLexicalRules = false });

        Assert.Equal(ParagraphRole.Normal, p.Role);
        Assert.Equal(0, p.Score);
    }

    /// <summary>
    /// Vế tách hai nhóm sạch nhất: heading đánh số thật trong tài liệu Word mang NumberingId do
    /// danh sách numbering sinh ra; số trong "Bảng 1.2:" là gõ tay nên không có NumberingId.
    /// </summary>
    [Fact]
    public void Heading_co_numbering_cua_Word_dung_truoc_bang_thi_khong_bi_loai()
    {
        var p = Caption(0, "2.2 Mô tả các sản phẩm dịch vụ", numberingId: 4);
        p.PrecedesTable = true;

        HeadingHeuristics.Classify(p, new ExtractionOptions { UseLexicalRules = false });

        Assert.NotEqual(ParagraphRole.Normal, p.Role);
    }

    /// <summary>
    /// Chốt chống ăn nhầm họ đề mục phổ biến nhất của văn bản hành chính. "Chương 1." cũng là
    /// "từ + số" và cũng có thể đứng trước một bảng, nhưng số MỘT phần — luật đòi số nhiều phần.
    /// </summary>
    [Theory]
    [InlineData("Chương 1. Quy định chung")]
    [InlineData("Điều 5. Trách nhiệm của các bên")]
    [InlineData("Phụ lục 1: Danh sách tài khoản")]
    public void De_muc_danh_so_MOT_phan_dung_truoc_bang_van_la_ung_vien(string text)
    {
        var p = Caption(0, text);
        p.PrecedesTable = true;

        HeadingHeuristics.Classify(p, new ExtractionOptions { UseLexicalRules = false });

        Assert.NotEqual(ParagraphRole.Normal, p.Role);
    }

    [Fact]
    public void Chu_thich_KHONG_dung_truoc_bang_thi_luat_cau_truc_khong_dung_toi()
    {
        var p = Caption(0, "Bảng 1.2: Tình hình huy động vốn giai đoạn 2022-2024");
        p.PrecedesTable = false;

        HeadingHeuristics.Classify(p, new ExtractionOptions { UseLexicalRules = false });

        Assert.NotEqual(ParagraphRole.Normal, p.Role);
    }

    private static SlimParagraph Caption(int index, string text, int? numberingId = null) => new()
    {
        Index = index,
        StableId = $"p[{index}]",
        Text = text,
        StyleId = "Heading3",
        StyleName = "Heading 3",
        Bold = true,
        FontSizePt = 13,
        BodyFontSizePt = 13,
        NumberingId = numberingId,
    };
}
