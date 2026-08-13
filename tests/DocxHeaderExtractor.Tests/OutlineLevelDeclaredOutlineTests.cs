using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class OutlineLevelDeclaredOutlineTests
{
    [Fact]
    public void BuildFromOutlineLevel_doc_w_outlineLvl_ke_ca_khong_phai_builtin_heading_style()
    {
        var doc = new SlimDocument
        {
            FileName = "outline.docx",
            SourcePath = "outline.docx",
            Paragraphs =
            [
                P(0, "Mục lục lặp", outline: 0, toc: true),
                P(1, "Bảng 1.1 Danh sách", outline: 1),
                P(2, "Section I. Instructions to Bidders", outline: 0),
                P(3, "1.1 Eligible Bidders", outline: 1),
                P(4, "Thân bài dài không có outline level."),
            ],
        }.Build();

        var headings = StyleDeclaredOutline.BuildFromOutlineLevel(doc);

        Assert.Equal([2, 3], headings.Select(h => h.Index));
        Assert.Equal([1, 2], headings.Select(h => h.Level));
        Assert.All(headings, h => Assert.Equal("outline_level_declared", h.ConfidenceBasis));
    }

    private static SlimParagraph P(int index, string text, int? outline = null, bool toc = false) => new()
    {
        Index = index,
        Text = text,
        OutlineLevel = outline,
        InTableOfContents = toc,
        FontSizePt = 12,
    };
}
