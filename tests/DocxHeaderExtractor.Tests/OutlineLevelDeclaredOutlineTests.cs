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

    [Fact]
    public void BuildFromOutlineLevel_ghep_heading_candidate_style_tu_dat_duoi_anchor()
    {
        var doc = new SlimDocument
        {
            FileName = "outline-form.docx",
            SourcePath = "outline-form.docx",
            Paragraphs =
            [
                P(0, "Section IV. Bidding Forms", outline: 0),
                P(1, "Proposal Forms", style: "SPDForms1", role: ParagraphRole.HeadingCandidate),
                P(2, "Qualification Forms", style: "SPDForms1", role: ParagraphRole.HeadingCandidate),
                P(3, "Advance Payment Security", style: "SPDForms1", role: ParagraphRole.HeadingCandidate),
                P(4, "Not a repeated custom heading", style: "OneOff", role: ParagraphRole.HeadingCandidate),
            ],
        }.Build();

        var headings = StyleDeclaredOutline.BuildFromOutlineLevel(doc);

        Assert.Equal([0, 1, 2, 3], headings.Select(h => h.Index));
        Assert.Equal([1, 2, 2, 2], headings.Select(h => h.Level));
        Assert.Equal("outline_anchor_custom_style", headings[1].ConfidenceBasis);
    }

    private static SlimParagraph P(int index, string text, int? outline = null, bool toc = false) => new()
    {
        Index = index,
        Text = text,
        OutlineLevel = outline,
        InTableOfContents = toc,
        FontSizePt = 12,
    };

    private static SlimParagraph P(
        int index,
        string text,
        string style,
        ParagraphRole role,
        int? outline = null) => new()
    {
        Index = index,
        StableId = $"p[{index}]",
        Text = text,
        StyleId = style,
        OutlineLevel = outline,
        Role = role,
        FontSizePt = 12,
    };
}
