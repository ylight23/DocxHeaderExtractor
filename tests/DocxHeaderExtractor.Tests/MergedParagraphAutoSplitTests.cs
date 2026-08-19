using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Bật tách đoạn gộp theo TỪNG TÀI LIỆU. Bật đại trà đã bị §113 bác (nổ rác 54/89 file) và đo lại
/// trên bộ đáp án: <c>ev-human</c> Nav lên 98,8% nhưng "tuyệt đối" tụt 1/5 → 0/5 vì
/// <c>056_OpenStax</c> vốn không gộp cũng bị tách.
/// </summary>
public class MergedParagraphAutoSplitTests
{
    private static SlimDocument Doc(IEnumerable<string> texts)
    {
        var ps = texts.Select((t, i) => new SlimParagraph { Index = i, Text = t, FontSizePt = 13 }).ToList();
        return new SlimDocument { FileName = "t.docx", SourcePath = "t.docx", Paragraphs = ps }.Build();
    }

    /// <summary>Đoạn gộp kiểu bản chuyển PDF: cả trang một <c>w:p</c>, mốc nằm trong thân đoạn.</summary>
    private static SlimDocument Gop(int trang) => Doc(Enumerable.Range(0, trang).Select(i =>
        string.Join(" ", Enumerable.Range(0, 4).Select(k =>
            $"Điều {i * 4 + k + 1}. Tiêu đề mục {i * 4 + k + 1} " +
            "Nội dung khoản này dài và trải qua nhiều câu như văn bản thật."))));

    /// <summary>
    /// Tài liệu gộp: rất nhiều mốc trong thân đoạn mà tầng ứng viên gần như không thấy gì.
    /// Nguyên hình dạng của <c>010_Luat_An_ninh_mang</c> — 2 ứng viên trên 73 mốc.
    /// </summary>
    [Fact]
    public void Tai_lieu_gop_thi_bat_tach()
    {
        var doc = Gop(20);

        Assert.True(MergedParagraphAutoSplit.ShouldSplit(doc, candidateCount: 2, out var moc));
        Assert.True(moc >= MergedParagraphAutoSplit.MinimumMarkers);
    }

    /// <summary>
    /// Mặt còn lại, và là mặt ĐẮT: tài liệu KHÔNG gộp thì không được đụng tới. Tách nhầm
    /// <c>056_OpenStax</c> làm nó mất quy chiếu chỉ số tuyệt đối, từ 100% xuống 0.
    /// </summary>
    [Fact]
    public void Tai_lieu_khong_gop_thi_khong_tach()
    {
        var doc = Gop(20);

        Assert.False(MergedParagraphAutoSplit.ShouldSplit(doc, candidateCount: 60, out _));
    }

    /// <summary>
    /// Luật là DẠNG chứ không phải danh sách từ tiếng Việt — phải nhận đúng trên tài liệu tiếng
    /// Anh mà không sửa gì. Giết đột biến "thay regex mốc bằng bảng từ khoá".
    /// </summary>
    [Fact]
    public void Nhan_moc_tren_tai_lieu_tieng_Anh()
    {
        var doc = Doc(Enumerable.Range(0, 20).Select(i =>
            $"Section {i + 1}. Heading text here. Body prose that continues for a while. " +
            $"Article {i + 40}. Another heading. More body prose following it."));

        Assert.True(MergedParagraphAutoSplit.ShouldSplit(doc, candidateCount: 1, out var moc));
        Assert.True(moc >= 20);
    }

    /// <summary>
    /// Mẫu quá nhỏ thì không kết luận. Không có chốt này, tài liệu ngắn hợp lệ (biên bản họp vài
    /// mục) sẽ bị tách oan chỉ vì ứng viên ít.
    /// </summary>
    [Fact]
    public void Mau_qua_nho_khong_ket_luan()
    {
        var doc = Doc(["Điều 1. Một mục", "Điều 2. Mục nữa", "Đoạn thân bài bình thường."]);

        Assert.False(MergedParagraphAutoSplit.ShouldSplit(doc, candidateCount: 0, out var moc));
        Assert.True(moc < MergedParagraphAutoSplit.MinimumMarkers);
    }

    /// <summary>
    /// <b>Hậu kiểm cho chính quyết định tách.</b> Đo được cái giá khi thiếu nó: <c>019_TT_200</c>
    /// nổ 165 → <b>3.563</b> mục trên 399 mốc, <c>020_TT_133</c> → <b>1.390</b> trên 607, vì bộ
    /// tách nhặt cả số trần giữa văn xuôi kế toán.
    /// </summary>
    [Theory]
    [InlineData(3563, 399, true)]   // 019_TT_200 — quá tay
    [InlineData(1390, 607, true)]   // 020_TT_133 — quá tay
    [InlineData(289, 155, false)]   // 092_RFC9111 — 1,9×, Nav 95,3%, phải GIỮ
    [InlineData(50, 73, false)]     // 010_Luat_An_ninh — khớp trọn đáp án
    [InlineData(71, 81, false)]     // 025_ND_47 — khớp trọn đáp án
    public void Hau_kiem_bat_dung_ca_tach_qua_tay(int soMuc, int soMoc, bool quaTay)
    {
        Assert.Equal(quaTay, MergedParagraphAutoSplit.QuaTay(soMuc, soMoc));
    }

    /// <summary>Không có mốc nào thì không có bằng chứng để nói quá tay — không được chặn bừa.</summary>
    [Fact]
    public void Khong_co_moc_thi_khong_ket_luan_qua_tay()
    {
        Assert.False(MergedParagraphAutoSplit.QuaTay(100, 0));
    }

    /// <summary>Mốc TRÙNG trong cùng đoạn chỉ tính một lần, nếu không đầu trang lặp sẽ thổi mẫu số.</summary>
    [Fact]
    public void Moc_trung_trong_mot_doan_chi_tinh_mot_lan()
    {
        var mot = MergedParagraphAutoSplit.CountMarkers(Doc(["Điều 1. A Điều 1. A Điều 1. A"]));

        Assert.Equal(1, mot);
    }
}
