using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Item danh sách đa cấp ("a., b., c.") lấy cấp từ mục cha NGAY TRÊN nó, không từ tầng chữ ký.
/// <para>
/// Tầng chữ ký xếp hạng theo thứ tự xuất hiện lần đầu trong CẢ tài liệu — một con số toàn cục cho
/// một quan hệ cục bộ. Đo được ở §31: ba cụm "a./b./c." nằm dưới ba mục cha ở ba độ sâu khác nhau,
/// tầng chữ ký gán cả ba cấp 5 trong khi đáp án là 4, 4 và 3.
/// </para>
/// </summary>
public class LocalListDepthTests
{
    /// <summary>
    /// Cùng chữ ký "a." xuất hiện dưới hai mục cha ở hai độ sâu khác nhau. Đây là ca mà một con số
    /// toàn cục KHÔNG THỂ đúng cả hai chỗ.
    /// </summary>
    [Fact]
    public void Cung_mot_nhan_duoi_hai_cha_khac_do_sau_thi_nhan_hai_cap_khac_nhau()
    {
        var document = Doc(
            (0, "1. Chương một", null),
            (2, "1.1. Mục lớn", null),
            (4, "1.1.1. Mục con", null),
            (6, "Truyền hình", 20),        // "a." dưới mục cấp 3
            (8, "Nội dung", 20),           // "b." cùng danh sách ⇒ anh em
            (10, "2. Chương hai", null),
            (12, "2.1. Mục lớn khác", null),
            (14, "Vấn đề cạnh tranh", 15), // "a." dưới mục cấp 2
            (16, "Giải pháp nâng cao", 15));
        var headings = Headings(
            (0, 1), (2, 2), (4, 3), (6, 9), (8, 9), (10, 1), (12, 2), (14, 9), (16, 9));

        StructuralHierarchyResolver.Apply(headings, document);

        var byIndex = headings.ToDictionary(h => h.Index, h => h.Level);
        Assert.Equal(4, byIndex[6]);    // cha là cấp 3
        Assert.Equal(4, byIndex[8]);    // anh em của 6, không phải con
        Assert.Equal(3, byIndex[14]);   // cha là cấp 2 ⇒ cấp KHÁC dù cùng nhãn "a."
        Assert.Equal(3, byIndex[16]);
    }

    /// <summary>
    /// Đoạn KHÔNG thuộc danh sách nào thì luật không được đụng tới: nó không có "cùng danh sách" để
    /// loại trừ, nên neo sẽ bám vào chính anh em của nó và kéo cả dãy sâu dần.
    /// </summary>
    [Fact]
    public void Doan_khong_thuoc_danh_sach_thi_luat_khong_cham_vao()
    {
        var document = Doc(
            (0, "PHẦN I", null), (2, "Đề mục không đánh số", null), (4, "Đề mục kế tiếp", null));
        var headings = Headings((0, 1), (2, 2), (4, 2));

        StructuralHierarchyResolver.Apply(headings, document);

        var byIndex = headings.ToDictionary(h => h.Index, h => h.Level);
        Assert.Equal(2, byIndex[2]);
        Assert.Equal(2, byIndex[4]);   // KHÔNG bị đẩy thành 3
    }

    /// <summary>
    /// Danh sách mở đầu tài liệu, không có mục nào đứng trước: không neo được thì phải trả quyền lại
    /// cho tầng chữ ký chứ không được bịa ra cấp 1.
    /// </summary>
    [Fact]
    public void Khong_co_muc_dung_truoc_thi_khong_neo()
    {
        var document = Doc((0, "Mục đầu tiên", 7), (2, "Mục thứ hai", 7));
        var headings = Headings((0, 3), (2, 3));

        StructuralHierarchyResolver.Apply(headings, document);

        Assert.Equal(3, headings.Single(h => h.Index == 0).Level);
    }

    private static DocxHeaderExtractor.DocumentProcessing.Policy.DocxPolicyState Doc(
        params (int Index, string Text, int? ListId)[] items) =>
        NativePolicyStateFactory.Create(items.Select(x => (x.Index, x.Text, x.ListId, (int?)null)));

    private static List<HeadingRecord> Headings(params (int Index, int Level)[] items) =>
        [.. items.Select(x => new HeadingRecord { Index = x.Index, Level = x.Level, Text = "" })];
}
