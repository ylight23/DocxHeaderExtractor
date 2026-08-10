using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Phân loại bảng (spec §5.5) — chỗ spec gọi là "mất dữ liệu lớn nhất nếu làm sai": tài liệu D có
/// 87% block nằm trong bảng, loại vô điều kiện làm mất 40 heading thật.
/// </summary>
public class TableRoleTests
{
    [Fact]
    public void Bang_so_lieu_o_ngan_thi_la_data()
    {
        var ps = Doc(Enumerable.Range(0, 12).Select(i => i % 3 == 0 ? "Chỉ tiêu" : $"{i * 137},5"));
        TableRoleClassifier.Apply(ps);

        Assert.All(ps.Where(p => p.TableDepth > 0), p => Assert.Equal(TableRole.Data, p.TableRole));
    }

    /// <summary>
    /// Bảng trình bày quy trình nghiệp vụ — mỗi ô là một bước có đánh số. Đây đúng là nhóm bị bỏ sót
    /// ở bản trước của spec.
    /// </summary>
    [Fact]
    public void Bang_trinh_bay_quy_trinh_thi_la_content()
    {
        var ps = Doc(Enumerable.Range(0, 8).Select(i =>
            $"{i + 1}. Văn bản đề nghị giao đất kèm theo hồ sơ và tài liệu chứng minh đủ điều kiện;"));
        TableRoleClassifier.Apply(ps);

        Assert.All(ps.Where(p => p.TableDepth > 0), p => Assert.Equal(TableRole.Content, p.TableRole));
    }

    /// <summary>Bảng ≤ 2 đoạn là khung dàn trang, không phải dữ liệu.</summary>
    [Fact]
    public void Bang_hai_dong_la_layout()
    {
        var ps = Doc(["BỘ TỔNG THAM MƯU", "PHÂN HỆ QUẢN LÝ"]);
        TableRoleClassifier.Apply(ps);

        Assert.All(ps.Where(p => p.TableDepth > 0), p => Assert.Equal(TableRole.Layout, p.TableRole));
    }

    [Fact]
    public void Doan_ngoai_bang_khong_bi_gan_vai_tro()
    {
        var ps = Doc([]);
        TableRoleClassifier.Apply(ps);

        Assert.All(ps, p => Assert.Equal(TableRole.None, p.TableRole));
    }

    /// <summary>Đoạn ngoài bảng đệm ở đầu để bảng không rơi vào 15% đầu tài liệu (khung trang bìa).</summary>
    private static List<SlimParagraph> Doc(IEnumerable<string> cells)
    {
        var list = new List<SlimParagraph>();
        for (var i = 0; i < 40; i++)
            list.Add(new SlimParagraph { Index = list.Count, Text = "Đoạn thân bài đứng ngoài bảng." });
        foreach (var text in cells)
            list.Add(new SlimParagraph { Index = list.Count, Text = text, TableDepth = 1 });
        return list;
    }
}
