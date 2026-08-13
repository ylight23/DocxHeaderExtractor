using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Bộ dựng tất định thứ ba, cùng nguyên tắc với <c>--style-outline</c> và
/// <c>--numbering-outline</c>: đọc một dữ kiện cấu trúc cho cả tài liệu, không chấm điểm, không
/// ngưỡng. Dữ liệu dưới lấy nguyên văn từ một công văn hành chính thật.
/// </summary>
public class AdministrativeOutlineTests
{
    private static List<HeadingRecord> Build(params string[] texts)
    {
        List<SlimParagraph> ps = [];
        for (var i = 0; i < texts.Length; i++)
            ps.Add(new SlimParagraph { Index = i, Text = texts[i], FontSizePt = 13 });
        var doc = new SlimDocument { FileName = "t.docx", SourcePath = "t.docx", Paragraphs = ps }.Build();
        return AdministrativeOutline.Build(doc);
    }

    /// <summary>Cấp suy từ THỨ TỰ XUẤT HIỆN của chữ ký, không gán cứng theo loại ký hiệu.</summary>
    [Fact]
    public void Cap_suy_tu_thu_tu_xuat_hien_cua_ky_hieu()
    {
        var r = Build(
            "I. VÙNG TRỜI",
            "1. HKDD",
            "2. Máy bay quân sự Ta",
            "3. MB quân sự nước ngoài",
            "a) Trong dự báo",
            "b) Ngoài dự báo",
            "II. VÙNG BIỂN",
            "1. Vùng biển phía Bắc");

        Assert.Equal([1, 2, 2, 2, 3, 3, 1, 2], r.Select(h => h.Level));
        Assert.Equal("I. VÙNG TRỜI", r[0].Text);
        Assert.Equal("a) Trong dự báo", r[4].Text);
    }

    /// <summary>
    /// Không hardcode loại ký hiệu: tài liệu dùng <c>A.</c> thay <c>I.</c> và <c>1)</c> thay
    /// <c>1.</c> phải cho cùng một cây, không sửa gì.
    /// </summary>
    [Fact]
    public void Khong_phu_thuoc_loai_ky_hieu_cu_the()
    {
        var r = Build("A. PHẦN MỘT", "1) Mục con", "2) Mục con khác", "B. PHẦN HAI");

        Assert.Equal([1, 2, 2, 1], r.Select(h => h.Level));
    }

    /// <summary>
    /// Thân bài tách bằng ranh giới cấu trúc — dấu ngắt ĐẦU TIÊN mở ra số liệu. Dấu hai chấm nội
    /// bộ phải nằm trọn trong thân, đúng ca đã gây lỗi ở §57.3.
    /// </summary>
    [Fact]
    public void Tach_than_bai_tai_dau_ngat_dau_tien()
    {
        var r = Build(
            "I. TÌNH HÌNH QUÂN ĐỘI",
            "d) Tàu trực của Hải đội dân quân thường trực: 14 tàu làm nhiệm vụ trực trên biển " +
            "(QK4: 01, QK5: 05; QK7: 04; QK9: 04)");

        Assert.Equal("d) Tàu trực của Hải đội dân quân thường trực", r[1].Text);
        Assert.StartsWith("14 tàu", r[1].InlineBody!);
        Assert.Contains("QK9: 04", r[1].InlineBody!);
    }

    /// <summary>Không có số liệu sau dấu ngắt thì cả lát là nhan đề — không cắt bừa tại dấu ':'.</summary>
    [Fact]
    public void Khong_cat_khi_sau_dau_ngat_khong_phai_so_lieu()
    {
        var r = Build("I. TỔNG QUAN", "1. Kết quả thử nghiệm: đánh giá tổng thể");

        Assert.Equal("1. Kết quả thử nghiệm: đánh giá tổng thể", r[1].Text);
        Assert.Null(r[1].InlineBody);
    }

    /// <summary>
    /// Một chữ ký duy nhất KHÔNG suy ra được quan hệ lồng nhau — trả rỗng thay vì đoán. Đây là
    /// khác biệt cốt lõi với bộ chấm điểm: thà không trả gì còn hơn trả một cây bịa ra.
    /// </summary>
    [Fact]
    public void Mot_chu_ky_duy_nhat_thi_tra_rong()
    {
        Assert.Empty(Build("1. Mục một", "2. Mục hai", "3. Mục ba"));
    }

    /// <summary>
    /// Đoạn gộp kiểu bản chuyển PDF: mốc nằm GIỮA đoạn vẫn phải ra đủ. Dùng nguyên văn hình dạng
    /// corpus (<c>Điều N.</c> dính liền khoản, không dấu cách).
    /// </summary>
    [Fact]
    public void Doan_gop_van_ra_du_muc()
    {
        var r = Build(
            "Chương I QUY ĐỊNH CHUNG",
            "Điều 1. Phạm vi điều chỉnh1. Luật này quy định về quản lý.Điều 2. Đối tượng áp dụng");

        Assert.True(r.Count >= 3, $"chỉ ra {r.Count} mục");
        Assert.Contains(r, h => h.Text.StartsWith("Điều 1.", StringComparison.Ordinal));
        Assert.Contains(r, h => h.Text.StartsWith("Điều 2.", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>KHUYẾT TẬT ĐÃ BIẾT — giới hạn của <see cref="ParagraphHeadingSplitter.Segments"/>.</b>
    /// Trong đoạn gộp toàn CHỮ HOA, hai từ cuối của nhan đề cộng số của mục sau tạo ra một dạng
    /// "nhãn + số" giả: <c>VÙNG TRỜI 1.</c> khớp như thể <c>TRỜI</c> là nhãn và <c>1</c> là số, nên
    /// lát cắt thành <c>I. VÙNG</c> + <c>TRỜI 1. HKDD…</c>.
    /// <para>
    /// Chưa sửa: phân biệt "nhãn thật" với "từ cuối của nhan đề in hoa" cần biết nhan đề kết thúc
    /// ở đâu — chính là bài toán đang giải. Cần đáp án trên thể loại này để đo, xem TODO mục 4.
    /// </para>
    /// Khi sửa xong, test này SẼ ĐỎ. Đó là mục đích.
    /// </summary>
    [Fact]
    public void Doan_gop_toan_chu_hoa_bi_cat_nham_KHUYET_TAT_DA_BIET()
    {
        var r = Build("I. VÙNG TRỜI 1. HKDD: 12 chuyến 2. Máy bay quân sự Ta: 04 chuyến");

        Assert.Equal("I. VÙNG", r[0].Text);   // đúng phải là "I. VÙNG TRỜI"
    }

    /// <summary>Không có ngưỡng độ dài: mục dài bao nhiêu cũng là mục, vì ký hiệu là dữ kiện.</summary>
    [Fact]
    public void Khong_co_tran_do_dai()
    {
        var dai = new string('x', 400);
        var r = Build("I. TỔNG QUAN", $"1. Mục rất dài {dai}");

        Assert.Equal(2, r.Count);
        Assert.Equal(2, r[1].Level);
    }

    /// <summary>
    /// <b>Đây là lý do luật "cấp theo cha gần nhất" tồn tại.</b> Tài liệu bỏ qua một cấp:
    /// <c>a)</c> đứng ngay dưới <c>II.</c> mà không qua <c>1.</c>. Cấp đúng là <b>2</b> — cha gần
    /// nhất là <c>II.</c> ở cấp 1.
    /// <para>
    /// Gán cứng theo hạng ký hiệu (<c>a)</c> luôn là cấp 3 vì nó là chữ ký thứ ba xuất hiện) cho
    /// <b>3</b>, tạo ra một cây nhảy cấp 1 → 3 mà tài liệu không hề có. Đó chính là lỗi spec §4.4
    /// mô tả: <i>"cấp phải suy theo ngữ cảnh cha gần nhất, không gán cứng theo loại ký hiệu"</i>.
    /// </para>
    /// Không có test này thì mutation "level = hạng + 1" đi lọt — nó đã sống sót một lượt.
    /// </summary>
    [Fact]
    public void Bo_qua_mot_cap_thi_neo_theo_cha_gan_nhat()
    {
        var r = Build(
            "I. VÙNG TRỜI",
            "1. HKDD",
            "a) Trong dự báo",
            "II. VÙNG BIỂN",
            "a) Hoạt động của tàu Trung Quốc");

        Assert.Equal([1, 2, 3, 1, 2], r.Select(h => h.Level));
    }
}
