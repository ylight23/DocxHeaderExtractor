using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Cấp suy từ THỨ TỰ LỒNG NHAU của style, không từ con số trong tên style.
/// <para>
/// Xuất phát từ §26: chấm lớp style-only của khoá luận bằng metric <i>parent finding</i> của HRDoc
/// cho ra 100% đúng cha nhưng chỉ 41,2% đúng cấp tuyệt đối — con số trong tên style sai, quan hệ
/// lồng nhau đúng. <see cref="StyleTrust.LevelTrusted"/> là nhị phân nên nó vứt cả tín hiệu, mất
/// luôn phần đúng.
/// </para>
/// </summary>
public class StyleNestingDepthTests
{
    /// <summary>
    /// Hình dạng của khoá luận thật: tác giả nhảy Heading1 → Heading3 → Heading4 trong một nhánh.
    /// Con số nói 1/3/4, độ sâu thật là 1/2/3. Luật phải đọc độ sâu, không đọc con số.
    /// </summary>
    [Fact]
    public void Cap_lay_theo_do_sau_long_nhau_chu_khong_theo_so_trong_ten_style()
    {
        var document = Doc(
            LevelTrusted: false,
            (0, "Chương 1", 1), (2, "1.1 Phạm vi", 3), (4, "1.1.1 Đối tượng", 4),
            (6, "1.2 Trình tự", 3), (8, "Chương 2", 1), (10, "2.1 Phân công", 3));
        // Mô hình đoán sai hết để thấy rõ luật nào đang ghi cấp.
        var headings = Headings((0, 5), (2, 5), (4, 5), (6, 5), (8, 5), (10, 5));

        StructuralHierarchyResolver.Apply(headings, document, respectStyleTrust: true);

        Assert.Equal([1, 2, 3, 2, 1, 2], headings.OrderBy(h => h.Index).Select(h => h.Level));
    }

    /// <summary>
    /// Ca đối kháng <c>10-cap-style-thoai-hoa</c>: mọi đề mục cùng một style. Ngăn xếp lồng nhau sập
    /// hết về cấp 1 và luật này TỆ HƠN cách cũ (đo được 44,4% → 33,3%), nên nó phải tự tắt.
    /// </summary>
    [Fact]
    public void Style_thoai_hoa_mot_cap_duy_nhat_thi_luat_tu_tat()
    {
        var document = Doc(
            LevelTrusted: false,
            (0, "Chương 1", 2), (2, "1.1 Phạm vi", 2), (4, "1.1.1 Đối tượng", 2),
            (6, "1.2 Trình tự", 2), (8, "Chương 2", 2), (10, "2.1 Phân công", 2),
            (12, "2.1.1 Kỹ thuật", 2), (14, "2.2 Kinh phí", 2), (16, "Chương 3", 2));
        var headings = Headings(
            (0, 1), (2, 2), (4, 3), (6, 2), (8, 1), (10, 2), (12, 3), (14, 2), (16, 1));

        StructuralHierarchyResolver.Apply(headings, document, respectStyleTrust: true);

        // Không mục nào bị kéo về 1: cấp ở đây phải đến từ độ sâu đánh số, không từ style.
        Assert.Equal([1, 2, 3, 2, 1, 2, 3, 2, 1], headings.OrderBy(h => h.Index).Select(h => h.Level));
    }

    /// <summary>
    /// Style CÓ bám độ sâu thì <c>LevelTrusted</c> đúng, cấp lấy thẳng từ style và luật này không
    /// được xen vào — nếu không thì nó ghi đè cả tài liệu soạn chuẩn.
    /// </summary>
    [Fact]
    public void Style_dang_tin_thi_luat_khong_xen_vao()
    {
        var document = Doc(
            LevelTrusted: true,
            (0, "Chương 1", 1), (2, "1.1 Phạm vi", 3), (4, "1.1.1 Đối tượng", 4));
        var headings = Headings((0, 1), (2, 3), (4, 4));

        StructuralHierarchyResolver.Apply(headings, document, respectStyleTrust: true);

        Assert.Equal([1, 3, 4], headings.OrderBy(h => h.Index).Select(h => h.Level));
    }

    /// <summary>Không bật <c>--style-trust</c> thì không luật nào của StyleTrust được đổi hành vi.</summary>
    [Fact]
    public void Khong_bat_co_style_trust_thi_luat_khong_chay()
    {
        var document = Doc(
            LevelTrusted: false,
            (0, "Chương 1", 1), (2, "1.1 Phạm vi", 3), (4, "1.1.1 Đối tượng", 4));
        var headings = Headings((0, 1), (2, 3), (4, 4));

        StructuralHierarchyResolver.Apply(headings, document, respectStyleTrust: false);

        Assert.Equal([1, 3, 4], headings.OrderBy(h => h.Index).Select(h => h.Level));
    }

    /// <summary>
    /// Mục KHÔNG mang style nằm xen giữa thì không đụng tới ngăn xếp style: cấp của nó đến từ độ sâu
    /// đánh số. Trộn hai thang vào một ngăn xếp là lấy cái sai của thang này đè lên cái đúng của thang kia.
    /// </summary>
    [Fact]
    public void Muc_khong_mang_style_khong_lam_lech_ngan_xep()
    {
        var document = Doc(
            LevelTrusted: false,
            (0, "Chương 1", 1), (2, "1.1 Phạm vi", 3), (4, "1.1.1 Đối tượng", null),
            (6, "1.2 Trình tự", 3));
        var headings = Headings((0, 5), (2, 5), (4, 5), (6, 5));

        StructuralHierarchyResolver.Apply(headings, document, respectStyleTrust: true);

        var byIndex = headings.ToDictionary(h => h.Index, h => h.Level);
        Assert.Equal(1, byIndex[0]);
        Assert.Equal(2, byIndex[2]);
        // "1.2" vẫn là cấp 2 — mục không style xen giữa không được đẩy nó sâu thêm.
        Assert.Equal(2, byIndex[6]);
    }

    /// <param name="items">
    /// <c>StyleLevel</c> null nghĩa là đoạn không mang style Heading built-in.
    /// </param>
    private static DocxHeaderExtractor.DocumentProcessing.Policy.DocxPolicyState Doc(
        bool LevelTrusted, params (int Index, string Text, int? StyleLevel)[] items)
    {
        // Ba vế của StyleTrust được đặt tay để test cô lập ĐÚNG vế đang kiểm. Vế NumberedDisagree là
        // đường duy nhất hạ LevelTrusted mà vẫn giữ DistinctLevels > 1 — đúng hình dạng khoá luận.
        var distinct = items.Where(x => x.StyleLevel is not null).Select(x => x.StyleLevel!.Value).Distinct().Count();
        var styled = items.Count(x => x.StyleLevel is not null);
        var trust = new StyleTrust(
            StyledCount: Math.Max(styled, StyleTrust.MinimumStyledSample),
            SuspectCount: 0,
            DistinctLevels: distinct,
            SkipsLevels: false,
            Density: 0.05,
            NumberedSample: LevelTrusted ? 0 : StyleTrust.MinimumNumberedSample,
            NumberedDisagree: LevelTrusted ? 0 : StyleTrust.MinimumNumberedSample);
        Assert.Equal(LevelTrusted, trust.LevelTrusted);

        return NativePolicyStateFactory.Create(
            items.Select(x => (x.Index, x.Text, (int?)null, x.StyleLevel)), trust);
    }

    private static List<HeadingRecord> Headings(params (int Index, int Level)[] items) =>
        items.Select(x => new HeadingRecord { Index = x.Index, Level = x.Level, Text = "" }).ToList();
}
