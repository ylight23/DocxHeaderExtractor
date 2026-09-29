using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public class NumberMarkerParserTests
{

    [Theory]
    [InlineData("I. TÌNH HÌNH TRÊN KHÔNG", NumberMarkerKind.Roman, 1, 1)]
    [InlineData("IV. KÍP BAN NGÀY 02/01/2026", NumberMarkerKind.Roman, 1, 4)]
    [InlineData("2. Nội dung cấp hai", NumberMarkerKind.Arabic, 1, 2)]
    [InlineData("3.1. Trong dự báo", NumberMarkerKind.Arabic, 2, 1)]
    [InlineData("1.2.3 Chi tiết", NumberMarkerKind.Arabic, 3, 3)]
    [InlineData("A) Phụ lục", NumberMarkerKind.Letter, 1, 1)]
    public void Parse_tach_dung_ky_hieu(string text, NumberMarkerKind kind, int depth, int value)
    {
        var t = PdfMarkerFactsParser.ParseStrict(text);

        Assert.NotNull(t);
        Assert.Equal(kind, t!.Value.Kind);
        Assert.Equal(depth, t.Value.Depth);
        Assert.Equal(value, t.Value.Value);
    }

    /// <summary>Bản gõ tay hay quên dấu cách; tầng chấm điểm bỏ qua, hậu kiểm thì không được phép.</summary>
    [Fact]
    public void Parse_chap_nhan_thieu_dau_cach_sau_so()
    {
        var t = PdfMarkerFactsParser.ParseStrict("1.MUC (chỉ số tổng hợp): 5005/2401");

        Assert.NotNull(t);
        Assert.Equal(NumberMarkerKind.Arabic, t!.Value.Kind);
        Assert.Equal(1, t.Value.Value);
    }

    [Theory]
    [InlineData("MIL. Viết tắt không phải số La Mã")]
    [InlineData("Không có đánh số ở đây")]
    [InlineData("- Gạch đầu dòng")]
    // Mất luôn dấu chấm thì không nhận: "1MUC" không phân biệt được với "3G", "4K".
    [InlineData("1MUC (chỉ số tổng hợp): 5005/2401")]
    // Số dài không được cắt thành mục: "2024" không phải mục 20.
    [InlineData("2024 Báo cáo năm")]
    [InlineData("50339/5039/2401")]
    [InlineData("32/32/0 dòng số liệu")]
    [InlineData("A: 04, B: 04,")]
    [InlineData("1: 04/04")]
    [InlineData("a) 01/02")]
    public void Parse_tra_null_khi_khong_phai_danh_so(string text) =>
        Assert.Null(PdfMarkerFactsParser.ParseStrict(text));

    /// <summary>
    /// TODO mục 3: dạng "nhãn + số" phải sinh ra token. Trước đây <c>Parse</c> chỉ có mẫu Ả Rập /
    /// La Mã / chữ cái nên <c>Chương 1.</c> không phân tích được — lý do gốc của bug 87,2% ở §5.
    /// <para>
    /// NHÃN phải nằm trong chữ ký: nếu <c>Chương 1.</c> ra <c>Arabic:1</c> thì nó trùng chữ ký với
    /// <c>1.</c> trần và <c>SignatureTiers</c> gộp hai tầng khác nhau làm một.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Chương 1. Tổng quan", "chương", 1)]
    [InlineData("PHẦN I. CƠ SỞ LÝ LUẬN", "phần", 1)]
    [InlineData("Abschnitt 4. Grundlagen", "abschnitt", 4)]
    public void Nhan_cong_so_sinh_ra_token_va_nhan_nam_trong_chu_ky(string text, string label, int value)
    {
        var token = PdfMarkerFactsParser.ParseStrict(text);

        Assert.NotNull(token);
        Assert.Equal(NumberMarkerKind.Labelled, token!.Value.Kind);
        Assert.Equal(label, token.Value.Label);
        Assert.Equal(value, token.Value.Value);
        Assert.Equal($"Labelled({label}):1", token.Value.Signature);

        // Và chữ ký đó phải KHÁC chữ ký của số trần cùng giá trị.
        Assert.NotEqual(PdfMarkerFactsParser.ParseStrict("1. Khái niệm")!.Value.Signature, token.Value.Signature);
    }

    /// <summary>
    /// Parser cố ý hẹp: nhận nhầm chú thích bảng thành nhãn cấu trúc sẽ tạo marker fact giả.
    /// Chú thích bảng phải trượt.
    /// </summary>
    [Theory]
    [InlineData("Bảng 1.2 Đối chiếu thuật ngữ")]
    [InlineData("Trang 5")]
    [InlineData("Ngày 14 tháng 01 năm 2026")]
    public void Nhan_cong_so_khong_an_nham_chu_thich_va_cau_van(string text)
    {
        Assert.NotEqual(NumberMarkerKind.Labelled, PdfMarkerFactsParser.ParseStrict(text)?.Kind);
    }

    // ---- Bảng chữ cái tiếng Việt (Nghị định 30/2020) -------------------------------------
    // Điểm đánh bằng "chữ cái tiếng Việt theo thứ tự bảng chữ cái tiếng Việt": đ đứng ngay sau
    // d, và f j w z không tồn tại. Ba test dưới ghim ba tình huống mà một bảng chữ cái cố định
    // chắc chắn làm sai một trong hai.

    /// <summary>Chữ có dấu phải lọt được qua regex; trước đây [A-Za-z] khiến đ) vô hình hoàn toàn.</summary>
    [Theory]
    [InlineData("đ) Kinh phí bảo đảm")]
    [InlineData("ă) Mục có dấu")]
    public void Parse_nhan_dien_chu_cai_tieng_Viet_co_dau(string text)
    {
        var t = PdfMarkerFactsParser.ParseStrict(text);

        Assert.NotNull(t);
        Assert.Equal(NumberMarkerKind.Letter, t!.Value.Kind);
    }

    // ---- Nhãn + số KHÔNG có dấu ngắt (Nghị định 30/2020 bị bản chuyển PDF dán liền) -----------

    /// <summary>
    /// Nghị định 30/2020: từ "Chương" cùng số thứ tự nằm một dòng riêng, tiêu đề dòng ngay dưới.
    /// Bản chuyển PDF→DOCX dán hai dòng thành <c>Chương II QUY ĐỊNH CHUNG</c> — không còn dấu chấm.
    /// Đo được hậu quả trên <c>082_Bo_luat_Lao_dong_2019_EN</c>: 26 <c>Chapter</c> + 221
    /// <c>Article</c> mà TẤT CẢ cấp 1, vì <c>Chapter</c> không parse được nên chỉ còn MỘT chữ ký.
    /// </summary>
    [Theory]
    [InlineData("Chương II QUY ĐỊNH CHUNG", "chương", 2)]
    [InlineData("Chapter II EMPLOYMENT AND RECRUITMENT", "chapter", 2)]
    [InlineData("PHẦN I NHỮNG VẤN ĐỀ CHUNG", "phần", 1)]
    public void Nhan_khong_dau_ngat_van_doc_duoc(string text, string label, int value)
    {
        var t = PdfMarkerFactsParser.ParseStrict(text);

        Assert.NotNull(t);
        Assert.Equal(NumberMarkerKind.Labelled, t!.Value.Kind);
        Assert.Equal(label, t.Value.Label);
        Assert.Equal(value, t.Value.Value);
    }

    /// <summary>
    /// Chốt chặn duy nhất của nhánh không-dấu-ngắt là phần còn lại phải bắt đầu bằng chữ HOA.
    /// Thiếu nó thì tham chiếu chéo giữa câu bị nhận thành đề mục và hậu kiểm đi báo thiếu những
    /// mục không tồn tại. Đây là test giết đột biến "bỏ lookahead \p{Lu}".
    /// </summary>
    [Theory]
    [InlineData("Điều 3 của Bộ luật này quy định")]
    [InlineData("khoản 2 Điều này thì áp dụng")]
    [InlineData("Chương 5 gồm các nội dung sau")]
    public void Tham_chieu_cheo_khong_thanh_nhan(string text)
    {
        Assert.NotEqual(NumberMarkerKind.Labelled, PdfMarkerFactsParser.ParseStrict(text)?.Kind);
    }

    /// <summary>Dạng có dấu ngắt phải giữ nguyên hành vi — nới không được làm hỏng đường cũ.</summary>
    [Theory]
    [InlineData("Chương 1. Tổng quan", "chương", 1)]
    [InlineData("Article 5. Rights of employees", "article", 5)]
    public void Nhan_co_dau_ngat_khong_doi(string text, string label, int value)
    {
        var t = PdfMarkerFactsParser.ParseStrict(text);

        Assert.NotNull(t);
        Assert.Equal(NumberMarkerKind.Labelled, t!.Value.Kind);
        Assert.Equal(label, t.Value.Label);
        Assert.Equal(value, t.Value.Value);
    }

    /// <summary>
    /// <b>Rủi ro do nới nhánh không-dấu-ngắt.</b> Chú thích hình/bảng có đúng hình dạng
    /// "nhãn + số + chữ hoa": <c>Bảng 3 Thống kê</c>, <c>Hình 2 Sơ đồ</c>. Trước khi nới, dấu ngắt
    /// bắt buộc đã loại chúng; sau khi nới thì không còn gì loại.
    /// <para>
    /// Chú thích lọt thành <see cref="NumberMarkerKind.Labelled"/> sẽ là marker fact giả mà mô hình nhìn thấy.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Bảng 3 Thống kê số liệu khảo sát")]
    [InlineData("Hình 2 Sơ đồ tổng thể hệ thống")]
    [InlineData("Biểu 4 Kết quả đối chiếu")]
    [InlineData("Table 5 Summary Of Results")]
    [InlineData("Figure 1 System Architecture")]
    public void Chu_thich_hinh_bang_khong_thanh_nhan_cau_truc(string text)
    {
        Assert.NotEqual(NumberMarkerKind.Labelled, PdfMarkerFactsParser.ParseStrict(text)?.Kind);
    }
}
