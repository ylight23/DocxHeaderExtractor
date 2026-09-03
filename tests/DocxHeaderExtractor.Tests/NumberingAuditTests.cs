using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public class NumberingAuditTests
{
    private static HeadingRecord H(int index, int level, string text) => new()
    {
        Index = index,
        Level = level,
        Text = text,
        Source = HeadingSource.Model,
        Confidence = 1.0,
    };

    [Theory]
    [InlineData("I. TÌNH HÌNH TRÊN KHÔNG", NumberKind.Roman, 1, 1)]
    [InlineData("IV. KÍP BAN NGÀY 02/01/2026", NumberKind.Roman, 1, 4)]
    [InlineData("2. Nội dung cấp hai", NumberKind.Arabic, 1, 2)]
    [InlineData("3.1. Trong dự báo", NumberKind.Arabic, 2, 1)]
    [InlineData("1.2.3 Chi tiết", NumberKind.Arabic, 3, 3)]
    [InlineData("A) Phụ lục", NumberKind.Letter, 1, 1)]
    public void Parse_tach_dung_ky_hieu(string text, NumberKind kind, int depth, int value)
    {
        var t = NumberingAudit.Parse(text);

        Assert.NotNull(t);
        Assert.Equal(kind, t!.Value.Kind);
        Assert.Equal(depth, t.Value.Depth);
        Assert.Equal(value, t.Value.Value);
    }

    /// <summary>Bản gõ tay hay quên dấu cách; tầng chấm điểm bỏ qua, hậu kiểm thì không được phép.</summary>
    [Fact]
    public void Parse_chap_nhan_thieu_dau_cach_sau_so()
    {
        var t = NumberingAudit.Parse("1.MUC (chỉ số tổng hợp): 5005/2401");

        Assert.NotNull(t);
        Assert.Equal(NumberKind.Arabic, t!.Value.Kind);
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
        Assert.Null(NumberingAudit.Parse(text));

    [Fact]
    public void Cung_dang_danh_so_ma_khac_cap_thi_bi_danh_dau()
    {
        // Ca tổng quát: mô hình trượt cấp cho II. còn I./III./IV. thì đúng.
        var headings = new List<HeadingRecord>
        {
            H(13, 1, "I. PHẦN ALPHA"),
            H(48, 5, "II. PHẦN BETA"),
            H(320, 1, "III. PHẦN GAMMA"),
            H(329, 1, "IV. PHẦN DELTA"),
        };

        var warnings = NumberingAudit.Run(headings);

        Assert.Single(warnings);
        Assert.Equal([48], warnings[0].Indexes);
        Assert.True(headings.Single(h => h.Index == 48).Disputed);
        Assert.All(headings.Where(h => h.Index != 48), h => Assert.False(h.Disputed));
    }

    [Fact]
    public void Cung_dang_va_cung_cap_thi_khong_canh_bao()
    {
        var headings = new List<HeadingRecord>
        {
            H(1, 1, "I. Phần một"),
            H(2, 1, "II. Phần hai"),
            H(3, 1, "III. Phần ba"),
        };

        Assert.Empty(NumberingAudit.Run(headings));
        Assert.All(headings, h => Assert.False(h.Disputed));
    }

    [Fact]
    public void Day_bat_dau_tu_2_thi_bao_thieu_muc_1()
    {
        // "1.MUC" bị tầng lọc đánh rơi (điểm 0.40 < ngưỡng 0.45) nên chỉ còn 2. và 3.
        var headings = new List<HeadingRecord>
        {
            H(22, 2, "2. Nội dung Beta"),
            H(26, 2, "3. Nội dung Gamma"),
        };

        var warnings = NumberingAudit.Run(headings);

        Assert.Single(warnings);
        Assert.Contains("thiếu mục 1", warnings[0].Message);
        Assert.Equal([22], warnings[0].Indexes);
    }

    [Fact]
    public void Nhay_coc_giua_day_thi_bao_thieu_muc_o_giua()
    {
        var headings = new List<HeadingRecord>
        {
            H(1, 1, "1. Một"),
            H(2, 1, "2. Hai"),
            H(3, 1, "5. Năm"),
        };

        var warnings = NumberingAudit.Run(headings);

        Assert.Single(warnings);
        Assert.Contains("thiếu mục 3, 4", warnings[0].Message);
        Assert.True(headings.Single(h => h.Index == 3).Disputed);
    }

    /// <summary>Số nhỏ lại nghĩa là đã sang mục cha khác, không phải nhảy cóc ngược.</summary>
    [Fact]
    public void Day_moi_bat_dau_lai_tu_1_khong_bi_coi_la_lo_hong()
    {
        var headings = new List<HeadingRecord>
        {
            H(10, 2, "1. Con của mục I"),
            H(11, 2, "2. Con của mục I"),
            H(20, 2, "1. Con của mục II"),
            H(21, 2, "2. Con của mục II"),
        };

        Assert.Empty(NumberingAudit.Run(headings));
    }

    [Fact]
    public void Khong_co_danh_so_thi_khong_lam_gi()
    {
        var headings = new List<HeadingRecord>
        {
            H(1, 1, "TÊN TÀI LIỆU"),
            H(2, 3, "Tiêu đề không đánh số"),
        };

        Assert.Empty(NumberingAudit.Run(headings));
        Assert.All(headings, h => Assert.False(h.Disputed));
    }

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
        var token = NumberingAudit.Parse(text);

        Assert.NotNull(token);
        Assert.Equal(NumberKind.Labelled, token!.Value.Kind);
        Assert.Equal(label, token.Value.Label);
        Assert.Equal(value, token.Value.Value);
        Assert.Equal($"Labelled({label}):1", token.Value.Signature);

        // Và chữ ký đó phải KHÁC chữ ký của số trần cùng giá trị.
        Assert.NotEqual(NumberingAudit.Parse("1. Khái niệm")!.Value.Signature, token.Value.Signature);
    }

    /// <summary>
    /// Hẹp hơn bên HeadingHeuristics theo đúng hợp đồng ghi ở đầu NumberingAudit: nhận nhầm thì hậu
    /// kiểm đi báo thiếu những mục không tồn tại. Chú thích bảng phải trượt.
    /// </summary>
    [Theory]
    [InlineData("Bảng 1.2 Đối chiếu thuật ngữ")]
    [InlineData("Trang 5")]
    [InlineData("Ngày 14 tháng 01 năm 2026")]
    public void Nhan_cong_so_khong_an_nham_chu_thich_va_cau_van(string text)
    {
        Assert.NotEqual(NumberKind.Labelled, NumberingAudit.Parse(text)?.Kind);
    }

    // ---- Bảng chữ cái tiếng Việt (Nghị định 30/2020) -------------------------------------
    // Điểm đánh bằng "chữ cái tiếng Việt theo thứ tự bảng chữ cái tiếng Việt": đ đứng ngay sau
    // d, và f j w z không tồn tại. Ba test dưới ghim ba tình huống mà một bảng chữ cái cố định
    // chắc chắn làm sai một trong hai.

    /// <summary>d) → đ) → e) là liên tục theo tiếng Việt. Bảng Latin sẽ báo "nhảy từ 4 sang 5".</summary>
    [Fact]
    public void Day_diem_tieng_Viet_co_chu_d_khong_bi_bao_dut_quang()
    {
        List<HeadingRecord> headings =
        [
            H(1, 1, "a) Cơ quan chủ trì"),
            H(2, 1, "b) Cơ quan phối hợp"),
            H(3, 1, "c) Thời hạn thực hiện"),
            H(4, 1, "d) Kinh phí bảo đảm"),
            H(5, 1, "đ) Tổ chức thực hiện"),
            H(6, 1, "e) Chế độ báo cáo"),
        ];

        Assert.Empty(NumberingAudit.Run(headings));
    }

    /// <summary>
    /// Chiều ngược lại phải giữ được: dãy Latin thuần a..f không được vì có bảng tiếng Việt mà
    /// bịa ra "thiếu đ)". Đây là test giết mutation "luôn dùng bảng tiếng Việt".
    /// </summary>
    [Fact]
    public void Day_chu_cai_Latin_khong_bi_bia_them_chu_d_thieu()
    {
        List<HeadingRecord> headings =
        [
            H(1, 1, "a) Overview"),
            H(2, 1, "b) Scope"),
            H(3, 1, "c) Method"),
            H(4, 1, "d) Results"),
            H(5, 1, "e) Discussion"),
            H(6, 1, "f) Conclusion"),
        ];

        Assert.Empty(NumberingAudit.Run(headings));
    }

    /// <summary>
    /// Chọn bảng chữ cái không được bịt miệng hậu kiểm: dãy thiếu mục thật vẫn phải báo dù chấm
    /// theo bảng nào. Không có test này thì "luôn trả về dãy hoàn hảo" cũng qua được hai test trên.
    /// </summary>
    [Fact]
    public void Day_chu_cai_thieu_muc_that_van_bi_bao()
    {
        List<HeadingRecord> headings =
        [
            H(1, 1, "a) Cơ quan chủ trì"),
            H(2, 1, "b) Cơ quan phối hợp"),
            H(3, 1, "m) Điều khoản thi hành"),
        ];

        var warnings = NumberingAudit.Run(headings);

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.Contains("nhảy từ 2 sang"));
    }

    /// <summary>Chữ có dấu phải lọt được qua regex; trước đây [A-Za-z] khiến đ) vô hình hoàn toàn.</summary>
    [Theory]
    [InlineData("đ) Kinh phí bảo đảm")]
    [InlineData("ă) Mục có dấu")]
    public void Parse_nhan_dien_chu_cai_tieng_Viet_co_dau(string text)
    {
        var t = NumberingAudit.Parse(text);

        Assert.NotNull(t);
        Assert.Equal(NumberKind.Letter, t!.Value.Kind);
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
        var t = NumberingAudit.Parse(text);

        Assert.NotNull(t);
        Assert.Equal(NumberKind.Labelled, t!.Value.Kind);
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
        Assert.NotEqual(NumberKind.Labelled, NumberingAudit.Parse(text)?.Kind);
    }

    /// <summary>Dạng có dấu ngắt phải giữ nguyên hành vi — nới không được làm hỏng đường cũ.</summary>
    [Theory]
    [InlineData("Chương 1. Tổng quan", "chương", 1)]
    [InlineData("Article 5. Rights of employees", "article", 5)]
    public void Nhan_co_dau_ngat_khong_doi(string text, string label, int value)
    {
        var t = NumberingAudit.Parse(text);

        Assert.NotNull(t);
        Assert.Equal(NumberKind.Labelled, t!.Value.Kind);
        Assert.Equal(label, t.Value.Label);
        Assert.Equal(value, t.Value.Value);
    }

    /// <summary>
    /// <b>Rủi ro do nới nhánh không-dấu-ngắt.</b> Chú thích hình/bảng có đúng hình dạng
    /// "nhãn + số + chữ hoa": <c>Bảng 3 Thống kê</c>, <c>Hình 2 Sơ đồ</c>. Trước khi nới, dấu ngắt
    /// bắt buộc đã loại chúng; sau khi nới thì không còn gì loại.
    /// <para>
    /// Quan trọng vì <see cref="StructuralRecovery"/> (§54) cứu mọi đoạn có token
    /// <see cref="NumberKind.Labelled"/> — chú thích lọt vào đó là dương tính giả trên đường có
    /// mô hình, nơi <c>bench --no-llm</c> KHÔNG đo tới.
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
        Assert.NotEqual(NumberKind.Labelled, NumberingAudit.Parse(text)?.Kind);
    }
}
