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
}
