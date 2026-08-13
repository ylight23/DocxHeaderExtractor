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
        var mode = Measure(P(Body), P(Body), P(Body), P(Body), P(Body), P(Body));

        Assert.Equal(DocumentMode.SemanticOnly, mode.Mode);
        Assert.Equal(DocumentStatus.Normal, mode.Status);
    }

    [Fact]
    public void File_qua_it_doan_va_khong_co_moc_la_conversion_failure_khong_phai_semantic_thuan()
    {
        var mode = Measure(P(Body), P(Body), P(Body));

        Assert.Equal(DocumentMode.SemanticOnly, mode.Mode);
        Assert.Equal(DocumentStatus.ConversionFailure, mode.Status);
    }

    [Fact]
    public void So_don_cap_khong_duoc_tu_chon_che_do_hanh_chinh()
    {
        var mode = Measure(
            P("1. Scope of application"), P("2. Definitions"), P("3. Rights and obligations"),
            P("4. Implementation"), P("5. Transitional provisions"), P("6. Effectiveness"),
            P(Body), P(Body));

        Assert.NotEqual(DocumentMode.VietnameseAdministrative, mode.Mode);
        Assert.Equal(0, mode.VietnameseAdminRatio);
    }

    [Fact]
    public void Article_tieng_Anh_la_cung_he_legal_structured()
    {
        var mode = Measure(
            P("Chapter I GENERAL PROVISIONS"),
            P("Article 1. Scope of regulation"),
            P("Article 2. Regulated entities"),
            P("Article 3. Interpretation of terms"),
            P(Body), P(Body));

        Assert.Equal(DocumentMode.VietnameseLegal, mode.Mode);
        Assert.True(mode.LegalMarkerRatio >= DocumentModeClassifier.LegalThreshold);
    }

    /// <summary>
    /// Văn bản quy phạm pháp luật dùng hệ <c>Chương / Điều</c> — phải tách khỏi hành chính, vì
    /// <c>Điều 5.</c> cũng khớp mẫu <c>\d+\.</c> của lớp hành chính nên bị bắt nhầm nếu không kiểm
    /// trước. Đo trên corpus: 3/3 tài liệu bản Python gán <c>vn-legal</c> nay khớp đúng.
    /// </summary>
    [Fact]
    public void Van_ban_phap_luat_tach_khoi_hanh_chinh()
    {
        var mode = Measure(
            P("Chương I"), P("Điều 1. Phạm vi điều chỉnh"), P("Điều 2. Đối tượng áp dụng"),
            P("Điều 3. Giải thích từ ngữ"), P(Body), P(Body));

        Assert.Equal(DocumentMode.VietnameseLegal, mode.Mode);
    }

    /// <summary>
    /// §48: mọi luật nhận dạng chế độ đều neo <c>^</c>, còn bản chuyển PDF gộp cả trang vào một
    /// đoạn. Đo trên 55 tài liệu không phân loại được: 1.596 mốc ở đầu đoạn, 24.220 mốc bên trong
    /// — 94% cấu trúc vô hình. Đây là test ghim hành vi đó, trước đây KHÔNG có.
    /// </summary>
    [Fact]
    public void Doan_gop_van_nhan_ra_che_do_phap_luat()
    {
        var mode = Measure(
            P("Chương I QUY ĐỊNH CHUNG Điều 1. Phạm vi điều chỉnh1. Luật này quy định về quản lý " +
              "và sử dụng.2. Đối tượng áp dụng gồm cơ quan, tổ chức.Điều 2. Giải thích từ ngữ" +
              "1. Trong Luật này, các từ ngữ dưới đây được hiểu như sau.Điều 3. Nguyên tắc chung" +
              "1. Bảo đảm công khai, minh bạch trong mọi hoạt động."),
            P(Body), P(Body));

        Assert.Equal(DocumentMode.VietnameseLegal, mode.Mode);
    }

    /// <summary>
    /// Test giết đột biến "đo trên đoạn thay vì trên lát cắt": cùng nội dung trên, nếu đo theo
    /// đoạn thì tử số bằng 0 và tài liệu rơi xuống nhánh dự phòng.
    /// </summary>
    [Fact]
    public void Cung_noi_dung_tach_san_thanh_doan_cho_cung_ket_qua()
    {
        var tach = Measure(
            P("Chương I QUY ĐỊNH CHUNG"),
            P("Điều 1. Phạm vi điều chỉnh"), P("1. Luật này quy định về quản lý và sử dụng."),
            P("Điều 2. Giải thích từ ngữ"), P("1. Trong Luật này, các từ ngữ được hiểu như sau."),
            P("Điều 3. Nguyên tắc chung"), P("1. Bảo đảm công khai, minh bạch."),
            P(Body), P(Body));

        Assert.Equal(DocumentMode.VietnameseLegal, tach.Mode);
    }

    /// <summary>
    /// Tài liệu thuần số gõ tay nhiều cấp (<c>N.1</c>/<c>N.2</c>, KHÔNG style Heading) phải vào
    /// <see cref="DocumentMode.TypedNumbering"/> trước khi nhánh hành chính kịp nuốt cùng chuỗi
    /// <c>1.1</c>. Đây là TODO mục 11 trong handoff.
    /// </summary>
    [Fact]
    public void So_go_tay_thuan_duoc_nhan_la_typed_numbering()
    {
        var ps = new List<SlimParagraph>();
        for (var i = 1; i <= 10; i++)
        {
            ps.Add(P($"{i}.1 Muc con thu nhat cua chuong {i}"));
            ps.Add(P($"{i}.2 Muc con thu hai cua chuong {i}"));
        }
        ps.Add(P(Body));

        var mode = Measure([.. ps]);

        Assert.Equal(DocumentMode.TypedNumbering, mode.Mode);
        Assert.Equal(0, mode.StyledHeadings);
    }

    /// <summary>
    /// Mặt còn lại của cùng khuyết tật: tài liệu dùng La Mã và chữ cái — ký hiệu RIÊNG của hành
    /// chính — vẫn ra đúng. Nếu ai sửa mục 11 bằng cách đảo thứ tự nhánh thay vì tách tín hiệu,
    /// test này sẽ đỏ và chỉ ra rằng sai lầm chỉ bị lật sang chiều kia (§49.2).
    /// </summary>
    [Fact]
    public void Ky_hieu_rieng_cua_hanh_chinh_khong_duoc_mat_khi_sua_muc_11()
    {
        var mode = Measure(
            P("I. Quy định chung"), P("II. Tổ chức thực hiện"), P("III. Điều khoản thi hành"),
            P("a) Cơ quan chủ trì"), P("b) Cơ quan phối hợp"), P("c) Thời hạn"),
            P(Body), P(Body));

        Assert.Equal(DocumentMode.VietnameseAdministrative, mode.Mode);
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
