using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Đầu trang chảy vào thân bài — hình dạng thật của bản chuyển PDF→DOCX (§107). Nguyên văn lấy từ
/// <c>019_TT_200-2014</c>: 237/475 đoạn dài mở đầu bằng cùng một dòng công báo, chỉ khác số.
/// </summary>
public class RunningHeaderAuditTests
{
    private static List<SlimParagraph> Docs(IEnumerable<string> texts) =>
        texts.Select((t, i) => new SlimParagraph { Index = i, Text = t, FontSizePt = 13 }).ToList();

    /// <summary>Thân bài phải KHÁC nhau thật; che chữ số không làm hai câu khác nhau bằng nhau.</summary>
    private static string Than(int i) =>
        string.Join(' ', Enumerable.Range(0, 12)
            .Select(k => new string((char)('a' + (i * 31 + k * 7) % 26), 5)));

    private static List<string> CongBao(int n) =>
        Enumerable.Range(0, n).Select(i =>
            $"CÔNG BÁO/Số {281 + i} + {282 + i}/Ngày 28-02-2015 {i} Điều {i + 1}. " +
            Than(i)).ToList();

    /// <summary>Bóc xong thì mốc THẬT nằm sau đầu trang lộ ra ở đầu đoạn.</summary>
    [Fact]
    public void Boc_dau_trang_lo_ra_moc_that()
    {
        var ps = Docs(CongBao(20));

        Assert.Equal(20, RunningHeaderAudit.Strip(ps));
        Assert.All(ps, p => Assert.StartsWith("Điều ", p.Text, StringComparison.Ordinal));
    }

    /// <summary>
    /// Luật là DẠNG, không phải danh sách từ tiếng Việt: đầu trang tiếng Anh phải bóc y hệt mà
    /// không sửa gì. Test này giết đột biến "thay việc che chữ số bằng bảng từ khoá".
    /// </summary>
    [Fact]
    public void Chay_tren_tai_lieu_tieng_Anh_khong_can_sua_luat()
    {
        var ps = Docs(Enumerable.Range(0, 20).Select(i =>
            $"RFC 9111 HTTP Caching June 2022 Section {i + 1}. " +
            Than(i)));

        Assert.Equal(20, RunningHeaderAudit.Strip(ps));
        Assert.All(ps, p => Assert.StartsWith("Section ", p.Text, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Đây là cái bẫy §34.3.</b> Cấu trúc song song lặp đề mục CÓ CHỦ Ý — luật "lặp nhiều lần"
    /// thuần đã làm mất 4 đề mục thật vì chuyện đó. Đề mục là đoạn NGẮN nên không được lọt vào mẫu.
    /// </summary>
    [Fact]
    public void Khong_dung_den_de_muc_song_song_lap_lai()
    {
        var ps = Docs(Enumerable.Range(0, 20).SelectMany(i => new[]
        {
            "Về ngôn ngữ",
            $"Đoạn thân bài thứ {i} nói về một nội dung khác hẳn, đủ dài để tính là đoạn dài.",
        }));
        var truoc = ps.Select(p => p.Text).ToList();

        RunningHeaderAudit.Strip(ps);

        Assert.Equal(truoc, ps.Select(p => p.Text).ToList());
    }

    /// <summary>Tài liệu sạch không có đầu trang lặp thì không được đụng vào một ký tự nào.</summary>
    [Fact]
    public void Tai_lieu_sach_khong_bi_doi()
    {
        var ps = Docs(Enumerable.Range(0, 20).Select(i =>
            $"Điều {i + 1}. Một đề mục hoàn toàn khác nhau ở mọi đoạn, không chia sẻ tiền tố nào cả."));
        var truoc = ps.Select(p => p.Text).ToList();

        Assert.Equal(0, RunningHeaderAudit.Strip(ps));
        Assert.Equal(truoc, ps.Select(p => p.Text).ToList());
    }

    /// <summary>
    /// Không bao giờ bóc hết đoạn. Nếu tiền tố chung nuốt gần trọn nội dung thì đó không phải đầu
    /// trang, và bóc đi là mất dữ liệu — tầng ứng viên rộng nhưng KHÔNG được đánh rơi.
    /// </summary>
    [Fact]
    public void Khong_boc_het_doan()
    {
        var ps = Docs(Enumerable.Range(0, 20).Select(_ =>
            "CÔNG BÁO/Số 281 + 282/Ngày 28-02-2015 nội dung gần như giống hệt nhau ở mọi đoạn."));

        RunningHeaderAudit.Strip(ps);

        Assert.All(ps, p => Assert.True(p.Text.Length > RunningHeaderAudit.MinimumBodyLength / 2));
    }

    /// <summary>
    /// <b>Một tài liệu mang NHIỀU biến thể đầu trang cùng lúc.</b> Nguyên văn từ
    /// <c>019_TT_200-2014</c>: đoạn mở bằng <c>"CÔNG BÁO/…"</c> và đoạn mở bằng <c>"2 CÔNG BÁO/…"</c>
    /// rơi vào hai cụm khác nhau vì số trang dẫn đầu. Bản chỉ bóc cụm LỚN NHẤT bỏ sót nguyên nhóm
    /// thứ hai — đo được: 306 mục với 234 mục còn nguyên đầu trang.
    /// <para>Đây là test giết đột biến <c>MaximumPasses = 1</c>.</para>
    /// </summary>
    [Fact]
    public void Boc_duoc_nhieu_bien_the_dau_trang_trong_cung_tai_lieu()
    {
        var ps = Docs(CongBao(14).Concat(
            Enumerable.Range(0, 12).Select(i =>
                $"{i} CÔNG BÁO/Số {291 + i} + {292 + i}/Ngày 28-02-2015 Điều {i + 40}. " + Than(i + 50))));

        RunningHeaderAudit.Strip(ps);

        Assert.DoesNotContain(ps, p => p.Text.Contains("CÔNG BÁO", StringComparison.Ordinal));
    }

    /// <summary>
    /// Cửa sổ gom cụm cố tình THÔ (12 ký tự) để chịu được số trang đổi độ dài, nên nó gom nhầm là
    /// chuyện bình thường — cổng thật là TIỀN TỐ CHUNG. Đoạn cùng mở bằng một cụm từ nối quen thuộc
    /// rồi rẽ ngay sang nội dung khác hẳn KHÔNG phải đầu trang và không được đụng tới.
    /// <para>Đây là test giết đột biến <c>MinimumCommonPrefix = 1</c>.</para>
    /// </summary>
    [Fact]
    public void Cum_gom_nham_bi_tien_to_chung_loai_ra()
    {
        var ps = Docs(Enumerable.Range(0, 20).Select(i => "Trong trường hợp " + Than(i)));
        var truoc = ps.Select(p => p.Text).ToList();

        Assert.Equal(0, RunningHeaderAudit.Strip(ps));
        Assert.Equal(truoc, ps.Select(p => p.Text).ToList());
    }

    /// <summary>
    /// <b>Bất biến offset — lỗi ĐÃ XẢY RA do chính bộ kiểm này.</b> Bóc chữ khỏi <c>Text</c> mà
    /// không dời <see cref="SlimParagraph.TextSpans"/> thì span trỏ RA NGOÀI chuỗi; đo được trên
    /// <c>092_RFC9111</c>. Bốn nơi đọc span theo offset, trong đó có
    /// <c>NeutralDocumentViewSerializer</c> — thứ mô hình nhìn thấy — nên lỗi nằm trên đường sản
    /// xuất, không riêng nhánh <c>--no-llm</c>.
    /// <para>Đây là test giết đột biến "bỏ lời gọi DoiSpan".</para>
    /// </summary>
    [Fact]
    public void Boc_xong_khong_span_nao_vuot_qua_chuoi()
    {
        var ps = Docs(CongBao(20));
        foreach (var p in ps)
            p.TextSpans = [new SlimTextSpan(0, p.Text.Length, Bold: true, false, false, 13)];

        RunningHeaderAudit.Strip(ps);

        Assert.All(ps, p => Assert.All(p.TextSpans, s =>
        {
            Assert.True(s.Start >= 0, $"Start {s.Start} âm");
            Assert.True(s.End <= p.Text.Length, $"End {s.End} vượt quá độ dài {p.Text.Length}");
        }));
        Assert.Contains(ps, p => p.TextSpans.Count > 0);
    }

    /// <summary>Mẫu quá nhỏ thì "lặp" không có nghĩa thống kê.</summary>
    [Fact]
    public void Mau_qua_nho_khong_ket_luan()
    {
        Assert.Equal(0, RunningHeaderAudit.Strip(Docs(CongBao(3))));
    }
}
