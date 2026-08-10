using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Nhãn lặp (spec §6.3c) — và đây là ca mà luật "chỉ dựa vào lặp" ĐÃ ĐO LÀ HỎNG ở §34.3: nó loại
/// được 2 dương tính giả nhưng làm mất 4 đề mục thật, vì khoá luận dùng cấu trúc song song.
/// Hai điều kiện bổ sung của spec phải phân biệt được đúng hai nhóm đó.
/// </summary>
public class RepeatedLabelTests
{
    /// <summary>
    /// `Nguồn: Facebook` lặp ba lần, không đánh số, mỗi lần đứng một mình dưới một cha khác nhau.
    /// </summary>
    [Fact]
    public void Nhan_lap_khong_danh_so_va_khong_anh_em_thi_bi_danh_dau()
    {
        var doc = Doc((0, "1. Mục một", 20), (2, "Nguồn: Facebook", null),
                      (4, "2. Mục hai", 20), (6, "Nguồn: Facebook", null),
                      (8, "3. Mục ba", 20), (10, "Nguồn: Facebook", null));
        var headings = H((0, 2), (2, 3), (4, 2), (6, 3), (8, 2), (10, 3));

        var marked = RepeatedLabelAudit.Apply(headings, doc);

        Assert.Equal(3, marked);
        Assert.All(headings.Where(x => x.Index % 4 == 2), h => Assert.True(h.Disputed));
        Assert.All(headings.Where(x => x.Index % 4 == 0), h => Assert.False(h.Disputed));
    }

    /// <summary>
    /// Vế cứu `Về ngôn ngữ` — đề mục THẬT lặp ba lần nhưng có anh em liền kề cùng cấp. §34.3 đo
    /// được luật chỉ-dựa-vào-lặp giết mất đúng bốn mục kiểu này.
    /// </summary>
    [Fact]
    public void De_muc_song_song_co_anh_em_lien_ke_thi_khong_bi_danh_dau()
    {
        var doc = Doc((0, "Về ngôn ngữ", null), (2, "Về kết cấu", null),
                      (4, "Về ngôn ngữ", null), (6, "Về định dạng", null),
                      (8, "Về ngôn ngữ", null), (10, "Về tương tác", null));
        var headings = H((0, 3), (2, 3), (4, 3), (6, 3), (8, 3), (10, 3));

        var marked = RepeatedLabelAudit.Apply(headings, doc);

        Assert.Equal(0, marked);
        Assert.All(headings, h => Assert.False(h.Disputed));
    }

    /// <summary>Mục lặp NHƯNG có đánh số thì là đề mục song song, không phải ô lặp.</summary>
    [Fact]
    public void Muc_lap_ma_co_danh_so_thi_khong_bi_danh_dau()
    {
        var doc = Doc((0, "Tiểu kết", 7), (4, "Tiểu kết", 7), (8, "Tiểu kết", 7));
        var headings = H((0, 2), (4, 2), (8, 2));

        Assert.Equal(0, RepeatedLabelAudit.Apply(headings, doc));
    }

    /// <summary>Lặp hai lần chưa đủ — ngưỡng là ba.</summary>
    [Fact]
    public void Lap_hai_lan_chua_du_de_coi_la_nhan_cau_truc()
    {
        var doc = Doc((0, "Nhận xét:", null), (4, "Nhận xét:", null), (8, "Mục khác", null));
        var headings = H((0, 3), (4, 3), (8, 1));

        Assert.Equal(0, RepeatedLabelAudit.Apply(headings, doc));
    }

    private static SlimDocument Doc(params (int Index, string Text, int? ListId)[] items) =>
        new SlimDocument
        {
            FileName = "x.docx", SourcePath = "x.docx",
            Paragraphs = [.. items.Select(x => new SlimParagraph
            {
                Index = x.Index, Text = x.Text, NumberingId = x.ListId,
            })],
        }.Build();

    private static List<HeadingRecord> H(params (int Index, int Level)[] items) =>
        [.. items.Select(x => new HeadingRecord { Index = x.Index, Level = x.Level, Text = "" })];
}
