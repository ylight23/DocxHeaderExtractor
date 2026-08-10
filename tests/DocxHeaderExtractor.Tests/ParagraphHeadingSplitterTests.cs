using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Đoạn gộp là hình dạng thật của 83/95 file trong corpus todo10_8 (bản chuyển PDF→DOCX).
/// Các test dưới lấy nguyên văn từ 001_Bo_luat_Dan_su_91-2015-QH13.docx.
/// </summary>
public class ParagraphHeadingSplitterTests
{
    [Fact]
    public void Tach_duoc_tieu_de_bi_gop_chung_voi_than_bai()
    {
        var slices = ParagraphHeadingSplitter.Split(
            "Điều 4. Áp dụng Bộ luật dân sự 1. Bộ luật này là luật chung điều chỉnh các quan hệ " +
            "dân sự. 2. Luật khác có liên quan điều chỉnh quan hệ dân sự trong các lĩnh vực cụ thể.");

        Assert.Single(slices);
        Assert.Equal("Điều 4. Áp dụng Bộ luật dân sự", slices[0].Text);
    }

    /// <summary>Một đoạn có thể gộp nhiều điều liền nhau; phải ra hết, đúng thứ tự.</summary>
    [Fact]
    public void Tach_duoc_nhieu_tieu_de_trong_cung_mot_doan()
    {
        var slices = ParagraphHeadingSplitter.Split(
            "2. Người thành niên có năng lực hành vi dân sự đầy đủ. Điều 21. Người chưa thành niên " +
            "1. Người chưa thành niên là người chưa đủ mười tám tuổi. Điều 22. Mất năng lực hành vi " +
            "dân sự 1. Khi một người do bệnh tâm thần.");

        Assert.Equal(2, slices.Count);
        Assert.Equal("Điều 21. Người chưa thành niên", slices[0].Text);
        Assert.Equal("Điều 22. Mất năng lực hành vi dân sự", slices[1].Text);
    }

    /// <summary>
    /// Nhãn là DẠNG chứ không phải danh sách từ: luật phải chạy trên tiếng Anh mà không sửa gì.
    /// Test này giết đột biến "thay regex nhãn bằng bảng từ khoá tiếng Việt".
    /// </summary>
    [Fact]
    public void Nhan_la_dang_tong_quat_khong_phai_bang_tu_khoa()
    {
        var slices = ParagraphHeadingSplitter.Split(
            "Article 4. Scope of application 1. This Code governs civil relations. " +
            "2. Other laws may apply.");

        Assert.Single(slices);
        Assert.Equal("Article 4. Scope of application", slices[0].Text);
    }

    /// <summary>
    /// Đoạn bình thường — một mốc ở đầu, phần còn lại là nhan đề — KHÔNG được chẻ. Không có test
    /// này thì bộ cắt sẽ băm nhỏ mọi heading lành lặn của 12 file soạn thẳng trong Word.
    /// </summary>
    [Theory]
    [InlineData("Điều 21. Người chưa thành niên")]
    [InlineData("Chương II. Quy định chung")]
    public void Doan_heading_binh_thuong_khong_bi_che_doi(string text)
    {
        Assert.Empty(ParagraphHeadingSplitter.Split(text));
    }

    /// <summary>Đoạn văn xuôi thuần không có mốc thì không được bịa ra mục nào.</summary>
    [Fact]
    public void Doan_van_xuoi_khong_sinh_muc()
    {
        Assert.Empty(ParagraphHeadingSplitter.Split(
            "Khi thực hiện nghĩa vụ chuyển giao vật đặc định thì phải giao đúng vật đó."));
    }

    /// <summary>
    /// Số thuần chỉ là mốc KẾT THÚC, không bao giờ là tiêu đề. Khoản "1. Bộ luật này là luật
    /// chung…" là văn xuôi; nhận nó làm nhan đề thì precision sập trên mọi văn bản pháp quy.
    /// </summary>
    [Fact]
    public void So_thuan_khong_duoc_nhan_lam_tieu_de()
    {
        var slices = ParagraphHeadingSplitter.Split(
            "1. Bộ luật này là luật chung điều chỉnh các quan hệ dân sự. " +
            "2. Luật khác có liên quan điều chỉnh quan hệ dân sự trong lĩnh vực cụ thể.");

        Assert.Empty(slices);
    }

    /// <summary>Lát cắt phải trỏ đúng vị trí trong đoạn gốc để nơi gọi tái lập được ngữ cảnh.</summary>
    [Fact]
    public void Lat_cat_giu_dung_vi_tri_trong_doan_goc()
    {
        const string text = "2. Người thành niên có đủ năng lực. Điều 21. Người chưa thành niên 1. Là người chưa đủ mười tám tuổi.";

        var slices = ParagraphHeadingSplitter.Split(text);

        Assert.Single(slices);
        Assert.Equal(text.IndexOf("Điều 21.", StringComparison.Ordinal), slices[0].Start);
        Assert.Equal(slices[0].Text, text.Substring(slices[0].Start, slices[0].Length));
    }

    /// <summary>
    /// Nguyên văn từ 001_Bo_luat_Dan_su_91-2015-QH13.docx. Bản chuyển PDF dán mốc vào từ trước
    /// ("dân sự1.", "quốc tế.Điều 5."). Đây là hình dạng thật của 83/95 file trong corpus, và là
    /// test giết đột biến "đòi dấu cách trước mốc".
    /// </summary>
    [Fact]
    public void Tach_duoc_khi_moc_bi_dan_lien_vao_tu_truoc()
    {
        var slices = ParagraphHeadingSplitter.Split(
            "Điều 4. Áp dụng Bộ luật dân sự1. Bộ luật này là luật chung điều chỉnh các quan hệ dân sự." +
            "2. Luật khác có liên quan điều chỉnh quan hệ dân sự trong các lĩnh vực cụ thể." +
            "Điều 5. Áp dụng tập quán1. Tập quán là quy tắc xử sự có nội dung rõ ràng.");

        Assert.Equal(2, slices.Count);
        Assert.Equal("Điều 4. Áp dụng Bộ luật dân sự", slices[0].Text);
        Assert.Equal("Điều 5. Áp dụng tập quán", slices[1].Text);
    }

    /// <summary>
    /// Tham chiếu chéo giữa câu KHÔNG được nhận làm đề mục. Dấu ngắt bắt buộc sau số là thứ chặn
    /// nó, và nới lookbehind không được phép làm hỏng điều đó.
    /// </summary>
    [Fact]
    public void Tham_chieu_cheo_giua_cau_khong_thanh_muc()
    {
        var slices = ParagraphHeadingSplitter.Split(
            "Điều 4. Áp dụng Bộ luật dân sự1. Không được trái với các nguyên tắc cơ bản của pháp " +
            "luật dân sự quy định tại Điều 3 của Bộ luật này và khoản 2 Điều này thì áp dụng.");

        Assert.Single(slices);
        Assert.Equal("Điều 4. Áp dụng Bộ luật dân sự", slices[0].Text);
    }
}
