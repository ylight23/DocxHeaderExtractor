using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Tiêu đề của mục lục ("MỤC LỤC", "Contents", "Danh mục hình ảnh") không đánh số, không nhất
/// thiết có style, và nếu dựa vào từ khoá thì mãi mãi chỉ nhận được những cách gọi ai đó liệt kê
/// sẵn. Quan hệ vị trí thì tổng quát: đoạn đứng NGAY TRƯỚC các dòng mục của mục lục chính là tiêu
/// đề của mục lục ấy — và dòng mục lục do Word đánh dấu bằng hyperlink neo _Toc, không phải đoán.
/// </summary>
public sealed class TocHeadingTests : IDisposable
{
    private readonly List<string> _files = [];

    private string Write(params Paragraph[] paragraphs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-toc-{Guid.NewGuid():N}.docx");
        _files.Add(path);
        using var wp = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = wp.AddMainDocumentPart();
        main.Document = new Document(new Body(paragraphs));
        main.Document.Save();
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _files) if (File.Exists(f)) File.Delete(f);
    }

    private static Paragraph Plain(string text) => new(new Run(new Text(text)));

    private static Paragraph TocEntry(string text) => new(
        new Hyperlink(new Run(new Text(text))) { Anchor = "_Toc" + Math.Abs(text.GetHashCode()) });

    [Theory]
    [InlineData("MỤC LỤC")]
    [InlineData("Danh mục hình ảnh")]
    [InlineData("Inhaltsverzeichnis")]
    public void Dong_dung_truoc_cac_muc_cua_muc_luc_la_tieu_de_cua_muc_luc(string title)
    {
        var path = Write(
            Plain(title),
            TocEntry("Chương 1. Mở đầu\t1"),
            TocEntry("1.1. Lý do chọn đề tài\t2"),
            Plain("Nội dung mở đầu trình bày bối cảnh nghiên cứu và các mục tiêu chính của đề tài."));

        var doc = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);
        var heading = doc.Paragraphs.First(p => p.Text == title);

        Assert.True(heading.PrecedesTableOfContents);
        Assert.Equal(ParagraphRole.HeadingCandidate, heading.Role);
        Assert.True(heading.Score >= 0.80, $"điểm {heading.Score} — chưa đủ mạnh để coi là bằng chứng cấu trúc");

        // Bản vá đầu tiên thiếu vế này: một DÒNG MỤC cũng đứng ngay trước dòng mục kế tiếp, nên cả
        // danh sách mục lục thành heading. Đo được trên bench trước khi sửa: thừa đúng hai dòng mục.
        Assert.All(
            doc.Paragraphs.Where(p => p.InTableOfContents),
            entry => Assert.False(entry.PrecedesTableOfContents,
                $"dòng mục \"{entry.Text}\" không được coi là tiêu đề của mục lục"));
    }

    [Fact]
    public void Dong_thuong_dung_truoc_van_ban_thuong_khong_bi_gan_nham()
    {
        var path = Write(
            Plain("Danh mục hình ảnh"),
            Plain("Nội dung tiếp theo là một đoạn văn bình thường, không phải dòng mục lục nào cả."));

        var doc = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);
        var line = doc.Paragraphs.First(p => p.Text == "Danh mục hình ảnh");

        Assert.False(line.PrecedesTableOfContents);
    }
}
