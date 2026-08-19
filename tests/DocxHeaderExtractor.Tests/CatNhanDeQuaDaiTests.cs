using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Lối cắt dự phòng khi đơn vị QUÁ DÀI. Luật chính của
/// <see cref="AdministrativeOutline.SplitHeadingBody"/> chỉ cắt ở <c>:</c>/<c>;</c> theo sau là số
/// liệu — đúng cho văn bản hành chính nhưng mù với bản chuyển PDF của báo cáo tài chính:
/// <c>041_IBRD</c> có nhan đề dài <b>4.571 ký tự</b> vì cả đoạn gộp thành một mục, và 25% số mục
/// toàn corpus dài quá 200 ký tự (§124).
/// </summary>
public class CatNhanDeQuaDaiTests
{
    private static string Dai(string dau, string than) =>
        dau + " " + string.Join(" ", Enumerable.Repeat(than, 12));

    /// <summary>Đơn vị quá dài thì cắt ở kết câu đầu tiên — nhan đề không chứa dấu kết câu.</summary>
    [Fact]
    public void Don_vi_qua_dai_cat_o_ket_cau()
    {
        var text = Dai("Section I: Overview.",
            "The Bank offers borrowers loans with long maturities and flexible terms.");

        var (heading, body) = AdministrativeOutline.SplitHeadingBody(text);

        Assert.Equal("Section I: Overview.", heading);
        Assert.NotNull(body);
    }

    /// <summary>
    /// <b>Chốt quan trọng nhất.</b> Mục có độ dài BÌNH THƯỜNG không được đụng tới, kể cả khi bên
    /// trong có dấu kết câu — nếu không, luật này sẽ băm nhỏ mọi đề mục đang đúng của bốn bộ đáp án.
    /// Đây là test giết đột biến "bỏ chốt độ dài".
    /// </summary>
    [Fact]
    public void Muc_do_dai_binh_thuong_khong_bi_cat()
    {
        const string text = "Điều 4. Áp dụng Bộ luật dân sự. Quy định chung.";

        var (heading, body) = AdministrativeOutline.SplitHeadingBody(text);

        Assert.Equal(text, heading);
        Assert.Null(body);
    }

    /// <summary>
    /// Luật CŨ phải thắng trước: dấu ngắt đầu tiên theo sau là SỐ LIỆU. Ghim lại §57.3 — lấy dấu
    /// ngắt sai làm nhan đề nuốt trọn số liệu.
    /// </summary>
    [Fact]
    public void Luat_so_lieu_van_thang_truoc()
    {
        var text = Dai("Báo cáo quân khu: 01, QK5: 05; QK9: 04",
            "Số liệu chi tiết theo từng đơn vị trực thuộc trong kỳ báo cáo.");

        var (heading, _) = AdministrativeOutline.SplitHeadingBody(text);

        Assert.Equal("Báo cáo quân khu", heading);
    }

    /// <summary>
    /// Chấm KHÔNG theo sau bởi chữ hoa không phải kết câu — <c>No. 5</c>, <c>v.v.</c>. Cắt ở đó thì
    /// nhan đề bị chặt giữa chừng.
    /// </summary>
    [Fact]
    public void Cham_khong_theo_sau_chu_hoa_khong_phai_ket_cau()
    {
        var text = Dai("Circular No. 5 of the ministry",
            "quy định chi tiết việc áp dụng cho từng nhóm đối tượng cụ thể trong kỳ.");

        var (heading, _) = AdministrativeOutline.SplitHeadingBody(text);

        Assert.StartsWith("Circular No. 5", heading, StringComparison.Ordinal);
    }

    /// <summary>Nhan đề cắt ra phải có chữ thật, không được là mẩu dấu câu.</summary>
    [Fact]
    public void Nhan_de_cat_ra_phai_co_chu()
    {
        var text = Dai(". A", "Phần thân bài dài phía sau dùng để vượt ngưỡng độ dài của luật.");

        var (heading, _) = AdministrativeOutline.SplitHeadingBody(text);

        Assert.True(heading.Count(char.IsLetter) >= 2);
    }
}
