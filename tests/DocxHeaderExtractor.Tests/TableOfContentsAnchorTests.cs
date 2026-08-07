using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Mục lục của chính tài liệu là TUYÊN BỐ của tác giả về bố cục đáng lẽ phải có. Pipeline vốn loại
/// dòng mục lục (đúng — chúng không phải đề mục) rồi vứt luôn thông tin chúng mang.
/// <para>
/// ĐO ĐƯỢC trên khoá luận thật: 21 dòng mục lục khớp <b>21/21</b> đề mục trong đáp án đồng thuận,
/// phủ 23/110 mục. Nối vào: đúng cấp 35,6% → <b>45,8%</b>, P/R/F1 không đổi, bench giữ 10/10.
/// </para>
/// </summary>
public sealed class TableOfContentsAnchorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dhx-toc-anchor-{Guid.NewGuid():N}");

    private const string Body =
        "Phần thân bài trình bày phạm vi áp dụng và các bước thực hiện của quy trình này, kèm ví dụ " +
        "minh hoạ cho từng bước để người đọc đối chiếu khi triển khai thực tế tại đơn vị.";

    /// <summary>
    /// Cấp trong tài liệu bị gán SAI (mọi mục cùng Heading2), còn mục lục thì khai đúng độ sâu.
    /// Mục lục phải thắng.
    /// </summary>
    [Fact]
    public void Muc_luc_pin_lai_cap_khi_than_bai_gan_sai()
    {
        var doc = new BenchDoc("toc-anchor", "Mục lục khai đúng, thân bài gán sai cấp",
        [
            new("MỤC LỤC", 1, Style: "Heading1"),
            new("Chương 1. Tổng quan 3", TocLink: true),
            new("1.1. Phạm vi áp dụng 4", TocLink: true),
            new("1.2. Đối tượng điều chỉnh 6", TocLink: true),
            new("Chương 1. Tổng quan", 1, Style: "Heading2"),
            new(Body),
            new("1.1. Phạm vi áp dụng", 2, Style: "Heading2"),
            new(Body),
            new("1.2. Đối tượng điều chỉnh", 2, Style: "Heading2"),
            new(Body),
        ]);
        var path = BenchDocumentFactory.Write(doc, _dir);
        var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);

        // Thân bài: mọi mục đều mang Heading2 nên cấp khởi đầu sai hết về 2.
        var headings = slim.Paragraphs
            .Where(p => p.HasBuiltInHeadingStyle && !p.InTableOfContents && !p.Text.StartsWith("MỤC"))
            .Select(p => new HeadingRecord
            {
                Index = p.Index, StableId = p.StableId, Level = 2, Text = p.Text,
                Source = HeadingSource.Style, Confidence = 1.0,
            })
            .ToList();
        Assert.Equal(3, headings.Count);

        var pinned = TableOfContentsAnchor.Apply(headings, slim);

        Assert.Equal(1, pinned);                                     // chỉ "Chương 1." sai cấp
        Assert.Equal(1, headings.First(h => h.Text.StartsWith("Chương")).Level);
        Assert.All(headings.Where(h => h.Text.StartsWith("1.")), h => Assert.Equal(2, h.Level));
    }

    /// <summary>
    /// Chốt: mục lục KHÔNG được thêm hay xoá đề mục nào — nó chỉ nói về những mục nó nhắc tới.
    /// Đề mục không có trong mục lục phải giữ nguyên cấp đang có.
    /// </summary>
    [Fact]
    public void Muc_khong_co_trong_muc_luc_thi_giu_nguyen()
    {
        var doc = new BenchDoc("toc-anchor-2", "Mục lục chỉ nhắc một mục",
        [
            new("MỤC LỤC", 1, Style: "Heading1"),
            new("Chương 1. Tổng quan 3", TocLink: true),
            new("Chương 1. Tổng quan", 1, Style: "Heading2"),
            new(Body),
            new("Phụ lục kỹ thuật", 1, Style: "Heading2"),
            new(Body),
        ]);
        var path = BenchDocumentFactory.Write(doc, _dir);
        var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);

        var outside = new HeadingRecord
        {
            Index = slim.Paragraphs.First(p => p.Text.StartsWith("Phụ lục")).Index,
            Level = 7, Text = "Phụ lục kỹ thuật", Source = HeadingSource.Style, Confidence = 1.0,
        };
        var headings = new List<HeadingRecord> { outside };

        TableOfContentsAnchor.Apply(headings, slim);

        Assert.Equal(7, outside.Level);   // không bị đụng tới
        Assert.Single(headings);          // không thêm mục nào
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
