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

    /// <summary>
    /// Mục lục GÕ TAY không có neo <c>_Toc</c> lẫn style TOC — nhận theo hình dạng của DÃY.
    /// <para>
    /// ĐO ĐƯỢC: gỡ ba neo khỏi <c>04-bia-muc-luc-chu-thich</c> và không đổi gì khác thì tầng OpenXML
    /// đi từ 3 thừa lên 6 thừa (P 57,1% → 40%), qua mô hình thì P 100% → 66,7% và R 100% → 50%.
    /// Sau luật này, cả hai tầng trở lại đúng bằng bản có neo.
    /// </para>
    /// </summary>
    [Fact]
    public void Muc_luc_go_tay_khong_co_neo_van_bi_nhan_ra_theo_hinh_dang()
    {
        var path = Write(
            Plain("MỤC LỤC"),
            Plain("Chương 1. Mở đầu 1"),
            Plain("1.1. Lý do chọn đề tài 2"),
            Plain("Chương 2. Nội dung 5"),
            Plain("Phần thân bài trình bày bối cảnh nghiên cứu và các mục tiêu chính của đề tài này."));

        var doc = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);

        Assert.All(
            doc.Paragraphs.Where(p => p.Text.EndsWith(" 1") || p.Text.EndsWith(" 2") || p.Text.EndsWith(" 5")),
            entry => Assert.True(entry.InTableOfContents, $"\"{entry.Text}\" phải bị nhận là dòng mục lục"));

        // Và vẫn giữ được vế kia: dòng đứng trước dãy đó là TIÊU ĐỀ của mục lục.
        Assert.True(doc.Paragraphs.First(p => p.Text == "MỤC LỤC").PrecedesTableOfContents);
    }

    /// <summary>
    /// Mục lục thật hay TỤT số trang đúng một lần: phần đầu (mục lục, danh mục bảng/hình) đánh số
    /// trang riêng rồi phần thân quay về 1. Khoá luận thật trong holdout có dãy
    /// <c>5, 6, 6, 7, 1, 16, 16, 37…</c> — bản đầu của luật đòi cả dãy không giảm nên loại sạch cả
    /// 21 dòng, tức luật chạy mà không bắt được gì. Cắt tại chỗ tụt thì cả hai đoạn con đều hợp lệ.
    /// </summary>
    [Fact]
    public void Muc_luc_tut_so_trang_giua_chung_van_duoc_nhan_ca_hai_doan()
    {
        // Nội dung trung tính: test khoá HÌNH DẠNG của dãy (số trang cuối dòng, một lần tụt), không
        // khoá chữ nghĩa — nên không chép tên đề mục từ tài liệu người dùng vào repo (§7.6).
        var path = Write(
            Plain("Danh sách nội dung"),
            Plain("Bảng ký hiệu viết tắt 5"),
            Plain("Bảng đơn vị đo lường 6"),
            Plain("Bảng tra cứu nhanh 7"),
            Plain("Phần thứ nhất 1"),                // ← tụt: phần thân đánh số lại từ đầu
            Plain("Quy trình vận hành 16"),
            Plain("Quy trình bảo trì 67"),
            Plain("Phần thân bài trình bày phạm vi áp dụng và các bước thực hiện của quy trình này."));

        var doc = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);
        var toc = doc.Paragraphs.Where(p => p.InTableOfContents).Select(p => p.Text).ToList();

        Assert.Equal(6, toc.Count);                                      // cả hai đoạn con, 3 + 3
        Assert.Contains(toc, t => t.EndsWith("viết tắt 5"));              // đoạn trước chỗ tụt
        Assert.Contains(toc, t => t.EndsWith("thứ nhất 1"));              // đúng dòng gây tụt
        Assert.Contains(toc, t => t.EndsWith("bảo trì 67"));              // đoạn sau chỗ tụt
        Assert.DoesNotContain(toc, t => t.StartsWith("Danh sách"));       // tiêu đề mục lục thì không
    }

    /// <summary>
    /// Chốt chống ăn nhầm: "PHỤ LỤC 1/2/3" cũng kết thúc bằng số và cũng tăng dần, nhưng chúng nằm
    /// rải rác khắp tài liệu chứ không liền nhau. Thiếu vế "liền nhau" thì luật hình dạng sẽ nuốt
    /// đúng nhóm đề mục này — chính tài liệu thật đã gặp (i=1294, 1335, 1446).
    /// </summary>
    [Fact]
    public void De_muc_ket_thuc_bang_so_nhung_khong_lien_tiep_thi_khong_phai_muc_luc()
    {
        var path = Write(
            Plain("PHỤ LỤC 1"),
            Plain("Nội dung phụ lục thứ nhất gồm phiếu khảo sát ý kiến và phần hướng dẫn trả lời."),
            Plain("PHỤ LỤC 2"),
            Plain("Nội dung phụ lục thứ hai gồm kết quả khảo sát đã tổng hợp theo từng câu hỏi."),
            Plain("PHỤ LỤC 3"),
            Plain("Nội dung phụ lục thứ ba gồm các biên bản phỏng vấn sâu với những người tham gia."));

        var doc = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);

        Assert.All(
            doc.Paragraphs.Where(p => p.Text.StartsWith("PHỤ LỤC")),
            p => Assert.False(p.InTableOfContents, $"\"{p.Text}\" là đề mục, không phải dòng mục lục"));
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
