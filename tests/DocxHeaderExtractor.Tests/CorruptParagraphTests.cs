using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Paragraph hỏng trong chính file nguồn (spec §3.6) — hai luồng run xen kẽ phân biệt bởi
/// <c>w:position</c> nên mỗi ký tự có hai bản. Phiên phân tích trước đã render ra ảnh để kiểm chứng:
/// Word vẽ đúng như vậy, không phải lỗi parser. Không có bước này thì mô hình suy luận trên rác.
/// </summary>
public class CorruptParagraphTests
{
    [Fact]
    public void Ky_tu_nhan_doi_thi_nhan_ra_la_hong()
    {
        Assert.True(CorruptParagraphDetector.IsDoubled("HHììnnhh  11..11  SSơơ  đđồồ  ttổổ  cchhứứcc"));
    }

    [Fact]
    public void Van_ban_binh_thuong_khong_bi_nhan_nham()
    {
        Assert.False(CorruptParagraphDetector.IsDoubled("Hình 1.1 Sơ đồ tổ chức bộ máy quản lý"));
        Assert.False(CorruptParagraphDetector.IsDoubled("1.1.3. Mạng xã hội và vai trò của nó"));
    }

    /// <summary>
    /// Chuỗi ngắn có thể trùng cặp do ngẫu nhiên — dưới ngưỡng độ dài thì không được kết luận.
    /// </summary>
    [Fact]
    public void Chuoi_qua_ngan_thi_khong_ket_luan()
    {
        Assert.False(CorruptParagraphDetector.IsDoubled("aabb"));
        Assert.False(CorruptParagraphDetector.IsDoubled(""));
        Assert.False(CorruptParagraphDetector.IsDoubled(null));
    }

    /// <summary>
    /// Ghim NGƯỠNG, không chỉ ghim hai đầu. Chuỗi này có 4/12 cặp trùng (33%) — dưới 0,55 nên KHÔNG
    /// hỏng, nhưng sẽ bị kết luận nhầm nếu ai đó hạ ngưỡng xuống 0,30. Mutation test bắt được lỗ
    /// này: hạ 0,55 → 0,30 mà không test nào đổ.
    /// </summary>
    [Fact]
    public void Ty_le_cap_trung_duoi_nguong_thi_khong_ket_luan_hong()
    {
        Assert.False(CorruptParagraphDetector.IsDoubled("aabbccddefghijklmnopqrst"));
    }

    /// <summary>Tiếng Việt có phụ âm đôi thật (<c>nh</c>, <c>ng</c>) — không được đủ để kết luận hỏng.</summary>
    [Fact]
    public void Phu_am_doi_tieng_Viet_khong_du_de_ket_luan()
    {
        Assert.False(CorruptParagraphDetector.IsDoubled("Nhanh chóng nghiêm ngặt nghiên cứu ngành nghề"));
    }

    /// <summary>
    /// Dot-leader, gạch dưới điền form và dấu chấm lửng là chuỗi ký tự lặp HỢP LỆ, không phải hai luồng
    /// run của ca gốc. Đo trên corpus trước khi sửa: 601/601 đoạn bị gắn cờ (100%) đều thuộc dạng này —
    /// heuristic cũ ghép cặp cả dấu câu nên dương giả có hệ thống (handoff §174).
    /// </summary>
    [Theory]
    [InlineData("8.6 Summary . . . . . . . . . . . . . . . . . . . . . . . . . . . . 128")]
    [InlineData("Country: _____________________________________________________")]
    [InlineData("Tên người bán:………………………………………………………………………………………………")]
    [InlineData("Total Equity (Table 2) . . . . . . . . . . . . . . . . . . 45,123 44,001")]
    public void Chuoi_ky_tu_lap_hop_le_khong_bi_ket_luan_hong(string text)
    {
        Assert.False(CorruptParagraphDetector.IsDoubled(text));
    }

    /// <summary>
    /// Ca gốc vẫn phải bắt được KỂ CẢ khi nằm lẫn trong dot-leader — nếu chỉ loại dấu câu mà làm hỏng
    /// khả năng phát hiện gốc thì bản sửa vô nghĩa.
    /// </summary>
    [Fact]
    public void Ky_tu_nhan_doi_van_bat_duoc_khi_lan_voi_dot_leader()
    {
        Assert.True(CorruptParagraphDetector.IsDoubled("HHììnnhh  11..11  SSơơ  đđồồ . . . . . . . . . . 4422"));
    }
}
