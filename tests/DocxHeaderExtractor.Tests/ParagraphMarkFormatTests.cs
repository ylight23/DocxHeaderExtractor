using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Sources.Docx.OpenXmlParser;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The paragraph mark's run properties (w:pPr/w:rPr) format only the pilcrow. A line whose text is plain is not bold because its mark is
/// (08-2026 report: a dash item with a bold mark was read as bold and selected as a heading).
/// </summary>
public sealed class ParagraphMarkFormatTests
{
    const string BoldMark = "<w:pPr><w:rPr><w:b/><w:i/><w:caps/></w:rPr></w:pPr>";

    static SourceStyleFacts Read(string body, string? styles = null)
    {
        var path = RawDocxFixtureTests.Build(body, styles);
        try { return new OpenXmlDocumentSource(new ExtractionOptions()).Read(path).Paragraphs.Single().Style; }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Plain_text_with_a_bold_mark_is_not_bold()
    {
        var p = Read($"<w:p>{BoldMark}<w:r><w:t>Truyền thông việc mở rộng mạng lưới</w:t></w:r></w:p>");
        Assert.False(p.Bold);
        Assert.False(p.Italic);
        Assert.False(p.AllCaps);
    }

    [Fact]
    public void Text_that_is_bold_only_in_part_is_not_a_bold_line()
    {
        var p = Read($"<w:p>{BoldMark}<w:r><w:rPr><w:b/></w:rPr><w:t xml:space=\"preserve\">Quỹ: </w:t></w:r><w:r><w:t>hoàn thành thủ tục.</w:t></w:r></w:p>");
        Assert.False(p.Bold);
    }

    [Fact]
    public void Bold_text_is_bold_and_a_run_without_its_own_value_takes_the_paragraph_style()
    {
        Assert.True(Read("<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Công tác xã hội:</w:t></w:r></w:p>").Bold);
        const string styles = "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Strong1\"><w:name w:val=\"Strong Para\"/><w:rPr><w:b/></w:rPr></w:style></w:styles>";
        Assert.True(Read("<w:p><w:pPr><w:pStyle w:val=\"Strong1\"/></w:pPr><w:r><w:t>Mục in đậm theo style</w:t></w:r></w:p>", styles).Bold);
    }

    [Fact]
    public void A_line_without_text_still_reads_its_mark()
    {
        Assert.True(Read($"<w:p>{BoldMark}</w:p>").Bold);
    }
}
