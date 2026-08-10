using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Tầng 1 của <c>spec-heading-outline-v2.md</c> — phân loại chế độ tài liệu. Hiện là CHẨN ĐOÁN:
/// đo và báo cáo, chưa đổi hành vi luật nào.
/// </summary>
public class DocumentModeTests
{
    /// <summary><c>w:outlineLvl</c> thắng mọi tín hiệu khác — spec §4.2 xếp nó thẩm quyền cao nhất.</summary>
    [Fact]
    public void OutlineLevel_thang_moi_tin_hieu_khac()
    {
        var mode = Measure(
            P("Chương 1. Tổng quan", outline: 0, styled: true),
            P("1.1. Phạm vi", outline: 1),
            P(Body));

        Assert.Equal(DocumentMode.OutlineLevelDriven, mode.Mode);
    }

    /// <summary>
    /// Ký hiệu hành chính KHÔNG in đậm vẫn phải nhận ra. Đo trên corpus 95 tài liệu: 18 tài liệu
    /// hành chính có 34% đoạn khớp ký hiệu nhưng chỉ 0% in đậm — 83/95 file là PDF trích text nên
    /// in đậm không sống sót. Lọc theo in đậm như spec §4.1 làm mẫu số rỗng.
    /// </summary>
    [Fact]
    public void Ky_hieu_hanh_chinh_khong_in_dam_van_nhan_ra()
    {
        var mode = Measure(
            P("I. Quy định chung"), P("1. Phạm vi điều chỉnh"), P("2. Đối tượng áp dụng"),
            P("a) Cơ quan nhà nước"), P("b) Tổ chức kinh tế"), P(Body), P(Body));

        Assert.Equal(DocumentMode.VietnameseAdministrative, mode.Mode);
        Assert.True(mode.VietnameseAdminRatio >= DocumentModeClassifier.AdministrativeThreshold);
    }

    /// <summary>Tài liệu chỉ có văn xuôi thì không được nhận nhầm là hành chính.</summary>
    [Fact]
    public void Van_xuoi_thuan_khong_bi_nhan_nham()
    {
        var mode = Measure(P(Body), P(Body), P(Body), P("Một đoạn ngắn"));

        Assert.NotEqual(DocumentMode.VietnameseAdministrative, mode.Mode);
    }

    /// <summary>Không tín hiệu nào và định dạng cũng không lệch ⇒ semantic-only, kỳ vọng thấp nhất.</summary>
    [Fact]
    public void Khong_tin_hieu_va_khong_lech_dinh_dang_thi_semantic_only()
    {
        var mode = Measure(P(Body), P(Body), P(Body));

        Assert.Equal(DocumentMode.SemanticOnly, mode.Mode);
    }

    private const string Body =
        "Phần thân bài trình bày phạm vi áp dụng và các bước thực hiện của quy trình này, kèm ví dụ " +
        "minh hoạ cho từng bước để người đọc đối chiếu khi triển khai thực tế, và nêu rõ trách nhiệm " +
        "của từng bộ phận trong quá trình phối hợp giữa các đơn vị có liên quan tới nhiệm vụ này.";

    private static DocumentModeReport Measure(params SlimParagraph[] ps) =>
        DocumentModeClassifier.Measure(ps);

    private static SlimParagraph P(string text, int? outline = null, bool styled = false) => new()
    {
        Index = 0,
        Text = text,
        OutlineLevel = outline,
        HasBuiltInHeadingStyle = styled,
        FontSizePt = 13,
    };
}
