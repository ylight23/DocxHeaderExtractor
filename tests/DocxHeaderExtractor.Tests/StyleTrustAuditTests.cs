using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// §7.1 và §9.7 cùng dừng ở chỗ <i>"cần một tín hiệu đo được rằng style của tài liệu NÀY có đáng tin
/// không — chưa có"</i>. Các test này khoá tín hiệu đó, và khoá cả điều quan trọng hơn: nó chỉ HẠ
/// QUYỀN, không bao giờ xoá đoạn.
/// </summary>
public sealed class StyleTrustAuditTests
{
    private static SlimParagraph Styled(int i, string text, int level, int tableDepth = 0) =>
        new()
        {
            Index = i,
            Text = text,
            StyleId = $"Heading{level}",
            StyleName = $"heading {level}",
            TableDepth = tableDepth,
        };

    private static SlimParagraph Body(int i) =>
        new() { Index = i, Text = $"Đoạn thân bài số {i} với đủ chữ để không bị coi là gì khác.", StyleId = "Normal" };

    private static List<SlimParagraph> Doc(IEnumerable<SlimParagraph> styled, int bodyCount)
    {
        var all = styled.ToList();
        for (var i = 0; i < bodyCount; i++) all.Add(Body(1000 + i));
        return all;
    }

    [Fact]
    public void Mau_qua_nho_thi_khong_ket_luan_gi()
    {
        var trust = StyleTrustAudit.Measure(Doc([Styled(0, "Chương 1", 1), Styled(1, "1.1 Phạm vi", 2)], 40));

        Assert.True(trust.SelectionTrusted);
        Assert.True(trust.LevelTrusted);
    }

    [Fact]
    public void Mot_cap_duy_nhat_tren_nhieu_muc_thi_style_khong_mang_thong_tin_cap()
    {
        // Ca báo cáo thật §7.1: tác giả gán Heading2 cho gần như mọi thứ, đúng cấp 40,7%.
        var styled = Enumerable.Range(0, 12).Select(i => Styled(i, $"Mục số {i}", 2));

        var trust = StyleTrustAudit.Measure(Doc(styled, 60));

        Assert.False(trust.LevelTrusted);
        Assert.True(trust.SelectionTrusted);
    }

    [Fact]
    public void Bo_cap_giua_chung_thi_con_so_trong_ten_style_khong_phai_do_sau()
    {
        // Ca khoá luận §9.7: Heading1 → Heading3 → Heading4, bỏ hẳn Heading2, đúng cấp ~28%.
        var styled = Enumerable.Range(0, 12)
            // Ngoặc BẮT BUỘC: `i % 3 switch {...}` bị C# parse thành `i % (3 switch {...})`.
            .Select(i => Styled(i, $"Mục số {i}", (i % 3) switch { 0 => 1, 1 => 3, _ => 4 }));

        var trust = StyleTrustAudit.Measure(Doc(styled, 60));

        Assert.True(trust.SkipsLevels);
        Assert.False(trust.LevelTrusted);
    }

    [Fact]
    public void Cay_style_lien_tuc_thi_giu_nguyen_quyen()
    {
        var styled = Enumerable.Range(0, 12)
            .Select(i => Styled(i, $"Mục số {i}", i % 3 + 1));

        var trust = StyleTrustAudit.Measure(Doc(styled, 60));

        Assert.True(trust.LevelTrusted);
        Assert.True(trust.SelectionTrusted);
    }

    [Fact]
    public void Mat_do_qua_cao_thi_tu_tieu_de_khong_con_nghia_gi()
    {
        // 10 đoạn mang style trên 12 đoạn không rỗng — không tài liệu thật nào 83% là đề mục.
        var styled = Enumerable.Range(0, 10).Select(i => Styled(i, $"Mục số {i}", i % 3 + 1));

        var trust = StyleTrustAudit.Measure(Doc(styled, 2));

        Assert.False(trust.SelectionTrusted);
    }

    [Fact]
    public void Style_ap_cho_o_bang_va_dong_ket_cau_thi_ha_quyen_chon()
    {
        // §7.4: 13 chú thích bảng mang Heading3 trên một báo cáo thật.
        var styled = Enumerable.Range(0, 8).Select(i => Styled(i, $"Mục số {i}", i % 3 + 1)).ToList();
        for (var i = 8; i < 12; i++)
            styled.Add(Styled(i, $"Ô dữ liệu {i}", 3, tableDepth: 1));

        var trust = StyleTrustAudit.Measure(Doc(styled, 60));

        Assert.Equal(4, trust.SuspectCount);
        Assert.False(trust.SelectionTrusted);
    }
}
