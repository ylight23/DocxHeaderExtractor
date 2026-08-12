using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Ca thật do người dùng báo: giao diện hiển thị NGUYÊN cả dòng làm tiêu đề, kể cả phần số liệu.
/// <para>
/// Nguyên nhân: <c>TryNumericPayloadBoundary</c> đòi phần sau dấu ngắt KHÔNG có một chữ cái nào,
/// nên nó bắt được <c>b. KQ Mỹ: 0/0 (0/0).</c> nhưng bỏ qua
/// <c>a) Trong dự báo: 01 tốp (như ngày 13/01).</c> — cùng tài liệu, cùng dãy, cùng hình dạng
/// "nhãn : số liệu", chỉ khác ở chỗ số liệu có kèm đơn vị.
/// </para>
/// </summary>
public class TachHeadingBodyCungDongTests
{
    private static SlimParagraph P(string text) => new()
    {
        Index = 0,
        Text = text,
        FontSizePt = 13,
    };

    private static (string Heading, string Body)? Split(string text)
    {
        var p = P(text);
        return InlineHeadingSplitter.TryFindBoundary(p, out var end, out var start)
            ? (text[..end], text[start..])
            : null;
    }

    /// <summary>Số liệu KÈM ĐƠN VỊ vẫn là số liệu — đây là ca người dùng báo.</summary>
    [Theory]
    [InlineData("a) Trong dự báo: 01 tốp (như ngày 13/01).", "a) Trong dự báo", "01 tốp (như ngày 13/01).")]
    [InlineData("b) Ngoài dự báo: 42 tốp/21 tốp đêm (giảm 06 tốp).", "b) Ngoài dự báo", "42 tốp/21 tốp đêm (giảm 06 tốp).")]
    [InlineData("b. KQ Mỹ: 0/0 (0/0).", "b. KQ Mỹ", "0/0 (0/0).")]
    [InlineData("11. Tàu cá VN: 4.722 chiếc (VBB=1.472, CV=315).", "11. Tàu cá VN", "4.722 chiếc (VBB=1.472, CV=315).")]
    public void Tach_duoc_nhan_khoi_so_lieu(string text, string heading, string body)
    {
        var r = Split(text);

        Assert.NotNull(r);
        Assert.Equal(heading, r!.Value.Heading);
        Assert.Equal(body, r.Value.Body);
    }

    /// <summary>
    /// Ràng buộc giữ luật hẹp: sau dấu ngắt phải là SỐ. Không có nó thì mọi tiêu đề chứa dấu hai
    /// chấm đều bị chẻ đôi, và đó là lỗi nặng hơn hẳn lỗi đang sửa.
    /// </summary>
    [Theory]
    [InlineData("3.1. Kết quả thử nghiệm: đánh giá tổng thể")]
    [InlineData("Ghi chú: xem phụ lục kèm theo")]
    [InlineData("I. TÌNH HÌNH CHUNG")]
    public void Khong_che_doi_tieu_de_khong_co_so_lieu(string text)
    {
        Assert.Null(Split(text));
    }

    /// <summary>
    /// <b>KHUYẾT TẬT ĐÃ BIẾT.</b> <c>c. KQ Philippin 0/0 (0/0)</c> KHÔNG có dấu ngắt nào — cùng
    /// một dãy với <c>b. KQ Mỹ:</c> mà tác giả viết hai kiểu khác nhau. Luật hiện tại đòi dấu ngắt
    /// nên bỏ qua ca này.
    /// <para>
    /// Chưa sửa vì luật "cắt tại số đầu tiên không cần dấu ngắt" sẽ chẻ cả
    /// <c>3.1. Kết quả 2024</c> và <c>Chương 2 Nội dung</c>. Cần đáp án người kiểm trên chính thể
    /// loại này để đo, không đoán được — xem TODO mục 4.
    /// </para>
    /// Khi sửa xong, test này SẼ ĐỎ. Đó là mục đích.
    /// </summary>
    [Fact]
    public void Khong_dau_ngat_thi_chua_tach_duoc_KHUYET_TAT_DA_BIET()
    {
        Assert.Null(Split("c. KQ Philippin 0/0 (0/0)"));
    }

    /// <summary>
    /// Token dữ liệu phải mở đầu bằng CHỮ SỐ, không phải bằng dấu câu. Chốt <c>i == 0</c> phía sau
    /// che được payload mở đầu bằng chữ, nhưng KHÔNG che payload mở đầu bằng <c>-</c> <c>.</c>
    /// <c>/</c> — lúc đó vòng lặp vẫn nuốt được dấu đó rồi gặp khoảng trắng và tưởng là token số.
    /// <para>
    /// Tìm ra nhờ một mutation SỐNG SÓT: bỏ điều kiện <c>char.IsDigit(payload[0])</c> mà không test
    /// nào đỏ. Theo §55.10, mutation sống sót có hai nguyên nhân — test yếu hoặc đột biến không
    /// thật. Ở đây là test yếu: hai bản CÓ khác nhau, đúng trên nhóm dưới đây.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("a) Ghi chú: - xem mục 5 của phụ lục")]
    [InlineData("b) Nhận xét: / theo báo cáo quý 3")]
    [InlineData("1. Lưu ý: . nội dung đã cập nhật 2024")]
    public void Payload_mo_dau_bang_dau_cau_khong_phai_so_lieu(string text)
    {
        Assert.Null(Split(text));
    }

    // ──────── Kiểm tra chéo ANH EM: mục không dấu ngắt chỉ ra ranh giới cho mục có ────────

    private static List<HeadingRecord> Day(params string[] texts)
    {
        List<SlimParagraph> ps = [];
        List<HeadingRecord> hs = [];
        for (var i = 0; i < texts.Length; i++)
        {
            ps.Add(new SlimParagraph { Index = i, Text = texts[i], FontSizePt = 13 });
            hs.Add(new HeadingRecord
            {
                Index = i, Level = 3, Text = texts[i],
                Source = HeadingSource.Heuristic, Confidence = 0.85,
            });
        }
        var doc = new SlimDocument { FileName = "t.docx", SourcePath = "t.docx", Paragraphs = ps }.Build();
        InlineHeadingSplitter.Apply(hs, doc);
        return hs;
    }

    /// <summary>
    /// Nguyên văn một dãy người dùng báo. Phần sau dấu hai chấm bắt đầu bằng CHỮ nên luật payload
    /// không cứu được — nhưng <c>a)</c> cùng dãy không có dấu ngắt và dừng đúng chỗ, đó là bằng
    /// chứng do CHÍNH TÀI LIỆU đưa ra về nơi ranh giới nằm.
    /// </summary>
    [Fact]
    public void Anh_em_khong_dau_ngat_chi_ra_ranh_gioi_cho_anh_em_co_dau_ngat()
    {
        var d = Day(
            "a) Hoạt động của tàu Trung Quốc.",
            "b) Hoạt động của tàu Philippin: Tàu BVBB-4409 ở ĐĐN bãi cạn Scarborough 52hl.",
            "c) Hoạt động của tàu Malaysia: Tàu TTP-114 ở Kỳ Vân; CSB-8305 ở Nam Luconia.",
            "d) Hoạt động của tàu Mỹ: Biên đội tàu Sân bay CVN-72 (gồm CVN-72; tàu Khu trục DDG-111, 112) ở ĐĐB Cỏ Rong 90hl.");

        Assert.Equal("a) Hoạt động của tàu Trung Quốc.", d[0].Text);   // không dấu ngắt → giữ nguyên
        Assert.Equal("b) Hoạt động của tàu Philippin", d[1].Text);
        Assert.Equal("c) Hoạt động của tàu Malaysia", d[2].Text);
        Assert.Equal("d) Hoạt động của tàu Mỹ", d[3].Text);
        Assert.StartsWith("Tàu BVBB-4409", d[1].InlineBody!);
        Assert.StartsWith("Biên đội tàu", d[3].InlineBody!);
    }

    /// <summary>
    /// Dấu ngắt bên trong THÂN không được cắt lần hai: <c>c)</c> có <c>;</c> giữa thân, phải nằm
    /// trọn trong <c>InlineBody</c>.
    /// </summary>
    [Fact]
    public void Dau_ngat_ben_trong_than_khong_bi_cat_tiep()
    {
        var d = Day(
            "a) Hoạt động của tàu Trung Quốc.",
            "c) Hoạt động của tàu Malaysia: Tàu TTP-114 ở Kỳ Vân; CSB-8305 ở Nam Luconia.");

        Assert.Contains("; CSB-8305", d[1].InlineBody!);
    }

    /// <summary>
    /// <b>Điều kiện anh em là thứ giữ luật hẹp.</b> Không mục nào trong dãy thiếu dấu ngắt thì
    /// KHÔNG cắt — <c>3.1. Kết quả thử nghiệm: đánh giá tổng thể</c> là nhan đề trọn vẹn, và cắt
    /// nó là lỗi nặng hơn hẳn lỗi đang sửa. Test này giết đột biến "cứ có dấu hai chấm là cắt".
    /// </summary>
    [Fact]
    public void Ca_day_deu_co_dau_ngat_thi_khong_cat()
    {
        var d = Day(
            "3.1. Kết quả thử nghiệm: đánh giá tổng thể",
            "3.2. Phạm vi áp dụng: toàn bộ hệ thống");

        Assert.Equal("3.1. Kết quả thử nghiệm: đánh giá tổng thể", d[0].Text);
        Assert.Null(d[0].InlineBody);
    }

    /// <summary>Một mục lẻ loi không có anh em thì không suy được gì — giữ nguyên.</summary>
    [Fact]
    public void Muc_le_loi_khong_co_anh_em_thi_giu_nguyen()
    {
        var d = Day("a) Hoạt động của tàu Philippin: Tàu BVBB-4409 ở Scarborough.");

        Assert.Equal("a) Hoạt động của tàu Philippin: Tàu BVBB-4409 ở Scarborough.", d[0].Text);
    }
}
