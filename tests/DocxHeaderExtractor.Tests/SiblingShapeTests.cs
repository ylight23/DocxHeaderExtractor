using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Anh em cùng cha phải tương đồng hình dạng. <c>KQ Mỹ</c> và <c>KQ Philippin</c> là anh em; nếu
/// luật tách heading/body cho ra một mục dài gấp nhiều lần các mục còn lại thì chính sự bất đối
/// xứng đó là dấu hiệu tách sai — bắt được mà không cần LLM.
/// </summary>
public class SiblingShapeTests
{
    [Fact]
    public void Muc_dai_bat_thuong_so_voi_anh_em_thi_bi_danh_dau()
    {
        var doc = Doc((0, "Chương 1"), (2, "KQ Mỹ"), (4, "KQ Philippin"),
                      (6, "KQ Thái Lan 0/0 (0/0) và toàn bộ phần nội dung bị dính vào tiêu đề này"));
        var headings = H((0, 1), (2, 2), (4, 2), (6, 2));

        var marked = SiblingShapeAudit.Apply(headings, doc);

        Assert.Equal(1, marked);
        Assert.True(headings.Single(h => h.Index == 6).Disputed);
        Assert.All(headings.Where(h => h.Index != 6), h => Assert.False(h.Disputed));
    }

    [Fact]
    public void Anh_em_tuong_dong_thi_khong_bi_dung_toi()
    {
        var doc = Doc((0, "Chương 1"), (2, "KQ Mỹ"), (4, "KQ Philippin"), (6, "KQ Thái Lan"));
        var headings = H((0, 1), (2, 2), (4, 2), (6, 2));

        Assert.Equal(0, SiblingShapeAudit.Apply(headings, doc));
    }

    /// <summary>Dưới ba anh em thì "trung vị" không có nghĩa — không được kết luận.</summary>
    [Fact]
    public void Duoi_ba_anh_em_thi_khong_ket_luan()
    {
        var doc = Doc((0, "Chương 1"), (2, "Ngắn"),
                      (4, "Một tiêu đề rất dài bị dính cả phần nội dung phía sau vào cùng một dòng"));
        var headings = H((0, 1), (2, 2), (4, 2));

        Assert.Equal(0, SiblingShapeAudit.Apply(headings, doc));
    }

    private static SourceDocument Doc(params (int Index, string Text)[] items) =>
        NativePolicyStateFactory.Create(items.Select(x => (x.Index, x.Text, (int?)null, (int?)null))).Source;

    private static List<HeadingRecord> H(params (int Index, int Level)[] items) =>
        [.. items.Select(x => new HeadingRecord { Index = x.Index, Level = x.Level, Text = "" })];
}
