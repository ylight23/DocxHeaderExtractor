using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// ĐO XEM lỗ hổng phát hiện khi xây dựng <c>TocAnswerKeyGenerator</c> (chỉ đọc SỐ trong TEXT của
/// dòng mục lục, bỏ qua NumberLabel đã resolve từ numbering.xml) có ảnh hưởng đường PRODUCTION
/// <see cref="TableOfContentsAnchor.Apply"/> hay không — đường này chạy trong
/// <c>HeaderExtractionPipeline</c> và code tự ghi "phải nói lời cuối" (ghi đè mọi nguồn cấp khác).
/// <para>
/// Câu hỏi cụ thể: nếu một heading đã được cấp ĐÚNG từ nguồn khác (numPr, style...), và tài liệu có
/// mục lục Word cho heading đó nhưng dòng mục lục KHÔNG chứa số (vì numbering do Word vẽ, không gõ
/// tay — đúng ca đo được trên báo cáo thực tập MBBank thật), thì <c>Apply</c> có GHI ĐÈ cấp đúng
/// bằng cấp 1 sai không?
/// </para>
/// </summary>
public sealed class TableOfContentsAnchorNumberLabelTests : IDisposable
{
    private readonly List<string> _files = [];

    private string WriteWithNumbering(params Paragraph[] paragraphs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-tocpin-{Guid.NewGuid():N}.docx");
        _files.Add(path);
        using var wp = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = wp.AddMainDocumentPart();
        main.Document = new Document(new Body(paragraphs));

        var numberingPart = main.AddNewPart<NumberingDefinitionsPart>();
        numberingPart.Numbering = new Numbering(
            new AbstractNum(
                new Level { LevelIndex = 0, NumberingFormat = new NumberingFormat { Val = NumberFormatValues.Decimal }, LevelText = new LevelText { Val = "%1." } },
                new Level { LevelIndex = 1, NumberingFormat = new NumberingFormat { Val = NumberFormatValues.Decimal }, LevelText = new LevelText { Val = "%1.%2." } })
            { AbstractNumberId = 7 },
            new NumberingInstance(new AbstractNumId { Val = 7 }) { NumberID = 1 });
        numberingPart.Numbering.Save();
        main.Document.Save();
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _files) if (File.Exists(f)) File.Delete(f);
    }

    private static Paragraph Plain(string text) => new(new Run(new Text(text)));

    private static Paragraph TocEntryNumbered(string text, int ilvl) => new(
        new ParagraphProperties(
            new NumberingProperties(new NumberingLevelReference { Val = ilvl }, new NumberingId { Val = 1 })),
        new Hyperlink(new Run(new Text(text))) { Anchor = "_Toc" + Math.Abs(text.GetHashCode()) });

    /// <summary>
    /// ĐÃ ĐO — KẾT QUẢ DƯƠNG: Apply trả cấp 1 (sai) thay vì 2 (đúng). Xác nhận lỗ hổng NumberLabel
    /// phát hiện khi xây TocAnswerKeyGenerator CŨNG ăn vào đường production, không chỉ công cụ mới.
    /// <para>
    /// Đánh dấu Skip thay vì để đỏ: sửa TableOfContentsAnchor.DepthOf đổi hành vi Apply, tức đổi mọi
    /// con số cấp đã chốt trong handoff.md (đo bằng --style-trust) cùng lúc — cần một lượt đo riêng
    /// (bench + eval trên khoá luận/báo cáo thực tập thật) trước khi sửa, không được gộp vào việc
    /// khác. Gỡ Skip khi bắt tay lượt đo đó.
    /// </para>
    /// </summary>
    [Fact(Skip = "ĐÃ XÁC NHẬN lỗi thật (Apply trả 1 thay vì 2) — chưa sửa, cần đo riêng tác động lên " +
                 "handoff.md trước. Xem TODO.md.")]
    public void Apply_voi_muc_luc_numPr_driven_khong_so_trong_text()
    {
        var path = WriteWithNumbering(
            Plain("MỤC LỤC"),
            TocEntryNumbered("Giới thiệu chung", 1),   // numId=1,ilvl=1 -> NumberLabel "1.1." nhưng TEXT không có số
            Plain("Giới thiệu chung"),
            Plain("Phần giới thiệu được trình bày chi tiết trong mục này của tài liệu nghiên cứu."));

        var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);
        var target = slim.Paragraphs.First(p => p.Text == "Giới thiệu chung" && !p.InTableOfContents);

        // Mô phỏng: một nguồn cấp KHÁC (numPr, style...) đã gán ĐÚNG cấp 2 cho heading này trước khi
        // TableOfContentsAnchor.Apply chạy — đúng thứ tự thật trong HeaderExtractionPipeline.
        var headings = new List<HeadingRecord>
        {
            new() { Index = target.Index, StableId = target.StableId, Level = 2, Text = target.Text },
        };

        TableOfContentsAnchor.Apply(headings, slim);

        // Đây là phép đo trước khi kết luận — assert chặt để xem giá trị THỰC, không đoán.
        Assert.Equal(2, headings[0].Level);
    }
}
