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

    [Fact]
    public void BuildFromOutlineLevel_ghep_custom_table_style_duoi_anchor_nhung_bo_style_noi_dung()
    {
        var doc = new SlimDocument
        {
            FileName = "outline-table-form.docx",
            SourcePath = "outline-table-form.docx",
            Paragraphs =
            [
                P(0, "Section I. Instructions to Bidders", outline: 0),
                P(1, "Scope of Bid", style: "Sec1-ClausesAfter10pt1", role: ParagraphRole.Normal, tableDepth: 1, bold: true),
                P(2, "Source of Funds", style: "Sec1-ClausesAfter10pt1", role: ParagraphRole.Normal, tableDepth: 1, bold: true),
                P(3, "Eligible Bidders", style: "Sec1-ClausesAfter10pt1", role: ParagraphRole.Normal, tableDepth: 1, bold: true),
                P(4, "1. List of Goods and Delivery Schedule", style: "SectionVIHeader", role: ParagraphRole.Normal, tableDepth: 1, bold: true, alignment: "center"),
                P(5, "2. List of Related Services and Completion Schedule", style: "SectionVIHeader", role: ParagraphRole.Normal, tableDepth: 1, bold: true, alignment: "center"),
                P(6, "A. General", style: "Normal", role: ParagraphRole.Normal, tableDepth: 1, bold: true, alignment: "center"),
                P(7, "B. Contents of Request for Bids Document", style: "BodyText2", role: ParagraphRole.Normal, tableDepth: 1, bold: true, alignment: "center"),
                P(8, "Section IX - Special Conditions of Contract (SCC)", style: "Normal", role: ParagraphRole.Normal, tableDepth: 1),
                P(9, "Format and Signing of Bid", style: "Head22", role: ParagraphRole.Normal, tableDepth: 1, bold: true),
                P(10, "• Section IX - Special Conditions of Contract (SCC)", style: "Normal", role: ParagraphRole.Normal, tableDepth: 1),
                P(11, "Evaluation (ITB 35.2(f))", style: "HeaderEvaCriteria", role: ParagraphRole.HeadingCandidate, bold: true),
                P(12, "Qualification", style: "HeaderEvaCriteria", role: ParagraphRole.HeadingCandidate, bold: true),
                P(13, "Supplementary Information", style: "Sec7Heading", role: ParagraphRole.Normal, alignment: "center"),
                P(14, "Beneficial Ownership Disclosure Form", style: "SectionIXHeader", role: ParagraphRole.Normal, alignment: "center"),
                P(15, "Evaluation of Technical Part (ITP 43)", style: "Normal", role: ParagraphRole.HeadingCandidate, numberingId: 28, numberingLevel: 7, numberLabel: "1."),
                P(16, "7. Confidentiality", style: "Normal", role: ParagraphRole.Normal, tableDepth: 1, bold: true),
                P(17, "A table prose line", style: "Normal", role: ParagraphRole.Normal, tableDepth: 1),
                P(18, "Another table prose line", style: "Sub-ClauseText", role: ParagraphRole.Normal, tableDepth: 1),
                P(19, "Yet another table prose line", style: "Sub-ClauseText", role: ParagraphRole.Normal, tableDepth: 1),
                P(20, "Final table prose line", style: "Sub-ClauseText", role: ParagraphRole.Normal, tableDepth: 1),
            ],
        }.Build();

        var headings = StyleDeclaredOutline.BuildFromOutlineLevel(doc);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], headings.Select(h => h.Index));
        Assert.Equal([1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2], headings.Select(h => h.Level));
        Assert.Equal("outline_anchor_table_custom_style", headings[1].ConfidenceBasis);
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
        int? outline = null,
        int tableDepth = 0,
        bool bold = false,
        string? alignment = null,
        int? numberingId = null,
        int? numberingLevel = null,
        string? numberLabel = null) => new()
    {
        Index = index,
        StableId = $"p[{index}]",
        Text = text,
        StyleId = style,
        OutlineLevel = outline,
        Role = role,
        TableDepth = tableDepth,
        Bold = bold,
        Alignment = alignment,
        NumberingId = numberingId,
        NumberingLevel = numberingLevel,
        NumberLabel = numberLabel,
        FontSizePt = 12,
    };
}
