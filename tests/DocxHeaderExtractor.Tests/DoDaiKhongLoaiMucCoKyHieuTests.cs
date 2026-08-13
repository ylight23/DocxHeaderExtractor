using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Ca thật người dùng báo: outline nhảy <c>IV → VI</c> và <c>4 → 6</c>. Hậu kiểm báo đúng
/// "thiếu mục 5" nhưng pipeline không cứu được, vì trần độ dài loại đoạn TRƯỚC khi nhìn ký hiệu.
/// <para>
/// Trong văn bản hành chính Việt Nam phần lớn mục cấp 2 viết kiểu <c>N. Tiêu đề: nội dung…</c> —
/// heading và body chung một paragraph — nên trần độ dài loại đúng nhóm cần xử lý nhất.
/// </para>
/// </summary>
public class DoDaiKhongLoaiMucCoKyHieuTests
{
    private const string Dai =
        "Thông tin liên quan đến hoạt động trên không gian mạng trong ngày, bao gồm các vụ việc " +
        "đã được ghi nhận và xử lý theo quy định hiện hành của các cơ quan chức năng có thẩm quyền " +
        "và các đơn vị phối hợp trong toàn quốc.";

    private static ParagraphRole Role(string text, int max = 200)
    {
        var p = new SlimParagraph { Index = 0, Text = text, FontSizePt = 13 };
        HeadingHeuristics.Classify(p, new ExtractionOptions { MaxCandidateTextLength = max });
        return p.Role;
    }

    /// <summary>Ký hiệu do NGƯỜI SOẠN gõ ra, mạnh hơn một ngưỡng độ dài do ta chọn.</summary>
    [Theory]
    [InlineData("V. KHÔNG GIAN MẠNG: ")]
    [InlineData("5.1. Tàu cá ngư dân ta: ")]
    [InlineData("1.2. Phạm vi áp dụng: ")]
    public void Doan_dai_MA_CO_ky_hieu_van_la_ung_vien(string prefix)
    {
        var text = prefix + Dai;

        Assert.True(text.Length > 200, "chuỗi test phải vượt trần thì mới kiểm được điều cần kiểm");
        Assert.NotEqual(ParagraphRole.Normal, Role(text));
    }

    /// <summary>
    /// Chiều ngược lại phải giữ: đoạn dài KHÔNG có ký hiệu vẫn bị loại.
    /// <para>
    /// Văn xuôi THUẦN không đủ để kiểm điều này — nó được 0 điểm nên bị loại dù có trần hay không,
    /// và một mutation "bỏ hẳn trần" đã SỐNG SÓT vì lý do đó. Trần tồn tại để chặn đoạn dài mà
    /// CÓ điểm: đậm, canh giữa, cỡ chữ lớn — tức thân bài được nhấn mạnh. Đó mới là ca cần ghim.
    /// </para>
    /// </summary>
    [Fact]
    public void Doan_dai_KHONG_ky_hieu_van_bi_loai()
    {
        Assert.Equal(ParagraphRole.Normal, Role(Dai + Dai));

        var dam = new SlimParagraph
        {
            Index = 0,
            Text = Dai + Dai,
            FontSizePt = 16,
            Bold = true,
            Alignment = "center",
        };
        HeadingHeuristics.Classify(dam, new ExtractionOptions());

        Assert.Equal(ParagraphRole.Normal, dam.Role);
    }

    /// <summary>
    /// Điểm của bản dính body phải BẰNG bản ngắn, không thấp hơn: nhan đề y hệt nhau, khác biệt
    /// nằm ở chỗ có thân đi kèm hay không — việc của <c>InlineHeadingSplitter</c>, không phải
    /// việc của bộ chấm điểm.
    /// <para>
    /// Đo được vì sao phải vậy: bản cũ chấm theo độ dài CẢ ĐOẠN nên "V. KHÔNG GIAN MẠNG" được
    /// 0,50 còn bản dính body chỉ 0,35 — dưới ngưỡng 0,45 nên biến mất. Đó chính là mục V mà
    /// người dùng báo thiếu.
    /// </para>
    /// </summary>
    [Fact]
    public void Diem_bang_dung_ban_ngan_vi_nhan_de_y_het()
    {
        var ngan = new SlimParagraph { Index = 0, Text = "V. KHÔNG GIAN MẠNG", FontSizePt = 13 };
        var dai = new SlimParagraph { Index = 1, Text = "V. KHÔNG GIAN MẠNG: " + Dai, FontSizePt = 13 };
        HeadingHeuristics.Classify(ngan, new ExtractionOptions());
        HeadingHeuristics.Classify(dai, new ExtractionOptions());

        Assert.Equal(ngan.Score, dai.Score, 3);
        Assert.NotEqual(ParagraphRole.Normal, dai.Role);
    }

    /// <summary>
    /// <b>KHUYẾT TẬT ĐÃ BIẾT.</b> Điểm <c>a)</c> chữ THƯỜNG không được <c>LetterPrefixRx</c> nhận
    /// (nó chỉ khớp <c>\p{Lu}</c> — chủ ý có sẵn, xem ghi chú đầu <c>NumberingAudit</c>), nên đoạn
    /// dài mở đầu bằng <c>a)</c> vẫn bị trần độ dài loại.
    /// <para>
    /// Chưa sửa: nới sang chữ thường làm mọi đoạn văn xuôi mở đầu bằng một chữ cái đơn thành ứng
    /// viên. Cần đo trên tài liệu có đáp án thuộc thể loại hành chính — TODO mục 4.
    /// </para>
    /// Khi sửa xong, test này SẼ ĐỎ. Đó là mục đích.
    /// </summary>
    [Fact]
    public void Diem_chu_thuong_dai_van_bi_loai_KHUYET_TAT_DA_BIET()
    {
        Assert.Equal(ParagraphRole.Normal, Role("a) Trong dự báo: " + Dai));
    }
}
