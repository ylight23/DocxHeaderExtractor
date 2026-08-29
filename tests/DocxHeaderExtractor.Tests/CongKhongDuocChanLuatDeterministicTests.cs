using System.Reflection;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Cổng precision là cổng chống ẢO GIÁC của mô hình. Nó không được chặn mục do luật deterministic
/// dựng ra — những mục đó có bằng chứng cấu trúc trong chính OOXML, không phải phỏng đoán ngữ nghĩa.
/// <para>
/// <b>Lỗi đã xảy ra (§109).</b> <see cref="PartSectionOutline"/> và <see cref="PdfBoldLabelOutline"/>
/// thêm sau khi danh sách trắng của cổng được viết, và không ai cập nhật danh sách. Cả hai TỰ đặt
/// <see cref="HeadingDecisionStatus.AutoAcceptedEvidence"/> lúc dựng, rồi bị cổng ghi đè xuống
/// <see cref="HeadingDecisionStatus.RequiresReview"/> — mã tự mâu thuẫn với chính nó mà không test
/// nào nói gì. Đo trên corpus: tài liệu bị chặn TOÀN BỘ hoặc không gì (063: 25/25, 030: 12/12,
/// 020: 48/48), vì một tài liệu đi trọn một nhánh.
/// </para>
/// </summary>
public class CongKhongDuocChanLuatDeterministicTests
{
    /// <summary>Mọi hằng <c>Basis</c> khai báo trong Core là chữ ký của một bộ dựng deterministic.</summary>
    public static TheoryData<string, string> MoiBasis()
    {
        var data = new TheoryData<string, string>();
        foreach (var type in typeof(PrecisionAcceptanceGate).Assembly.GetTypes())
        {
            var field = type.GetField("Basis",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

            if (field is { IsLiteral: true } && field.GetRawConstantValue() is string value)
                data.Add(type.Name, value);
        }

        return data;
    }

    /// <summary>
    /// <b>Lưới chống trôi.</b> Thêm một bộ dựng deterministic mới mà quên đăng ký chữ ký của nó vào
    /// cổng thì test này đỏ ngay, thay vì mọi mục của bộ dựng đó âm thầm bị đẩy sang người duyệt.
    /// </summary>
    [Theory]
    [MemberData(nameof(MoiBasis))]
    public void Moi_bo_dung_deterministic_deu_duoc_cong_dang_ky(string type, string basis)
    {
        Assert.True(
            PrecisionAcceptanceGate.IsDeterministicDeclaredBasis(basis),
            $"{type}.Basis = \"{basis}\" chưa được đăng ký ở PrecisionAcceptanceGate. " +
            "Mọi mục của bộ dựng này sẽ bị hạ khỏi tự nhận dù có bằng chứng cấu trúc.");
    }

    /// <summary>Phản chiếu phải THẤY được thứ gì đó, nếu không lưới rỗng và luôn xanh giả.</summary>
    [Fact]
    public void Luoi_khong_duoc_rong()
    {
        Assert.True(MoiBasis().Count >= 3, "Phản chiếu không tìm thấy hằng Basis nào — lưới vô hiệu.");
    }

    /// <summary>Cổng vẫn phải CHẶN chữ ký không có bằng chứng; nới hết thì nó hết là cổng.</summary>
    [Theory]
    [InlineData("evidence_not_calibrated")]
    [InlineData("holdout_bucket_missing")]
    [InlineData("calibration_profile_mismatch")]
    public void Cong_van_chan_chu_ky_khong_co_bang_chung(string basis)
    {
        Assert.False(PrecisionAcceptanceGate.IsDeterministicDeclaredBasis(basis));
    }
}
