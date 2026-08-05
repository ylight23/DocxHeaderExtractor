using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Danh sách đa cấp gắn với style Heading (<c>w:lvl/w:pStyle</c>) là tuyên bố cấu trúc mạnh nhất
/// trong OOXML: người soạn cấu hình một lần qua hộp thoại "Define New Multilevel List" rồi mọi
/// đoạn bám theo. Khác w:outlineLvl vốn đi theo từng thao tác định dạng lẻ nên hay bị gán nhầm.
/// <para>
/// Đoạn trong các test này KHÔNG mang style Heading trực tiếp và KHÔNG in đậm — chỉ có numbering
/// nói chúng là heading. Nếu tầng đọc bỏ sót thì chúng thành văn bản thường.
/// </para>
/// </summary>
public sealed class MultilevelListHeadingTests : IDisposable
{
    private readonly List<string> _files = [];

    private string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-numbering-{Guid.NewGuid():N}.docx");
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _files) if (File.Exists(f)) File.Delete(f);
    }

    private static Level HeadingLevel(int ilvl, string styleId, string lvlText) => new()
    {
        LevelIndex = ilvl,
        StartNumberingValue = new StartNumberingValue { Val = 1 },
        NumberingFormat = new NumberingFormat { Val = NumberFormatValues.Decimal },
        LevelText = new LevelText { Val = lvlText },
        ParagraphStyleIdInLevel = new ParagraphStyleIdInLevel { Val = styleId },
    };

    private static Paragraph Numbered(string text, int numId, int ilvl) => new(
        new ParagraphProperties(
            new ParagraphStyleId { Val = "Normal" },
            new NumberingProperties(
                new NumberingLevelReference { Val = ilvl },
                new NumberingId { Val = numId })),
        new Run(new Text(text)));

    private static SlimDocument Extract(string path) =>
        new DocxSlimExtractor(new ExtractionOptions()).Extract(path);

    [Fact]
    public void Cap_cua_danh_sach_da_cap_gan_style_heading_tro_thanh_cap_heading()
    {
        var path = NewPath();
        using (var wp = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = wp.AddMainDocumentPart();
            main.Document = new Document(new Body(
                Numbered("Quy định chung", 1, 0),
                new Paragraph(new Run(new Text(
                    "Nội dung của phần này mô tả phạm vi điều chỉnh và đối tượng áp dụng của quy định."))),
                Numbered("Phạm vi điều chỉnh", 1, 1),
                new Paragraph(new Run(new Text(
                    "Phần này nêu rõ các trường hợp được áp dụng và các trường hợp loại trừ.")))));

            var numberingPart = main.AddNewPart<NumberingDefinitionsPart>();
            numberingPart.Numbering = new Numbering(
                new AbstractNum(
                    HeadingLevel(0, "Heading1", "%1."),
                    HeadingLevel(1, "Heading2", "%1.%2."))
                { AbstractNumberId = 7 },
                new NumberingInstance(new AbstractNumId { Val = 7 }) { NumberID = 1 });
            numberingPart.Numbering.Save();
            main.Document.Save();
        }

        var doc = Extract(path);
        var chuong = doc.Paragraphs.First(p => p.Text == "Quy định chung");
        var muc = doc.Paragraphs.First(p => p.Text == "Phạm vi điều chỉnh");

        Assert.Equal(1, chuong.NumberingStyleLevel);
        Assert.Equal(2, muc.NumberingStyleLevel);
        // Và tầng lọc phải coi đó là heading chắc chắn, không phải ứng viên yếu.
        Assert.Equal(ParagraphRole.StyledHeading, chuong.Role);
        Assert.Equal(ParagraphRole.StyledHeading, muc.Role);
        Assert.Equal(1, chuong.GuessedLevel);
        Assert.Equal(2, muc.GuessedLevel);
    }

    [Fact]
    public void Danh_sach_khong_gan_style_heading_thi_khong_thanh_heading()
    {
        var path = NewPath();
        using (var wp = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = wp.AddMainDocumentPart();
            main.Document = new Document(new Body(
                Numbered("Thiết bị đo lường đã được kiểm định", 1, 0),
                Numbered("Hồ sơ nghiệm thu từng hạng mục", 1, 0)));

            var numberingPart = main.AddNewPart<NumberingDefinitionsPart>();
            // Cùng cấu trúc nhưng KHÔNG có pStyle: đây là danh sách liệt kê thường.
            numberingPart.Numbering = new Numbering(
                new AbstractNum(new Level
                {
                    LevelIndex = 0,
                    StartNumberingValue = new StartNumberingValue { Val = 1 },
                    NumberingFormat = new NumberingFormat { Val = NumberFormatValues.Decimal },
                    LevelText = new LevelText { Val = "%1." },
                })
                { AbstractNumberId = 3 },
                new NumberingInstance(new AbstractNumId { Val = 3 }) { NumberID = 1 });
            numberingPart.Numbering.Save();
            main.Document.Save();
        }

        var doc = Extract(path);
        Assert.All(doc.Paragraphs.Where(p => p.Role != ParagraphRole.Empty),
            p => Assert.Null(p.NumberingStyleLevel));
    }

    [Fact]
    public void Numbering_tro_qua_list_style_van_dung_duoc_dinh_nghia_that()
    {
        var path = NewPath();
        using (var wp = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = wp.AddMainDocumentPart();
            main.Document = new Document(new Body(Numbered("Tổ chức thực hiện", 1, 0)));

            // Style giữ numbering thật (numId = 2).
            var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(new Style(
                new StyleName { Val = "Danh muc dung chung" },
                new StyleParagraphProperties(new NumberingProperties(new NumberingId { Val = 2 })))
            {
                Type = StyleValues.Paragraph,
                StyleId = "DanhMucDungChung",
            });
            stylesPart.Styles.Save();

            var numberingPart = main.AddNewPart<NumberingDefinitionsPart>();
            numberingPart.Numbering = new Numbering(
                // abstractNum 10: bản TRỎ, không chứa định nghĩa nào.
                new AbstractNum(new NumberingStyleLink { Val = "DanhMucDungChung" }) { AbstractNumberId = 10 },
                // abstractNum 11: bản định nghĩa thật, gắn style Heading.
                new AbstractNum(HeadingLevel(0, "Heading1", "%1.")) { AbstractNumberId = 11 },
                new NumberingInstance(new AbstractNumId { Val = 10 }) { NumberID = 1 },
                new NumberingInstance(new AbstractNumId { Val = 11 }) { NumberID = 2 });
            numberingPart.Numbering.Save();
            main.Document.Save();
        }

        var doc = Extract(path);
        var heading = doc.Paragraphs.First(p => p.Text == "Tổ chức thực hiện");

        // Đoạn dùng numId=1 (bản trỏ). Không lần theo numStyleLink thì numbering ra rỗng.
        Assert.Equal(1, heading.NumberingStyleLevel);
        Assert.Equal(ParagraphRole.StyledHeading, heading.Role);
    }
}
