using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PartSectionOutlineTests
{
    [Fact]
    public void Dung_part_section_lam_muc_luc_high_level_va_bo_clause_ben_trong()
    {
        var doc = new SlimDocument
        {
            FileName = "wb.docx",
            SourcePath = "wb.docx",
            Paragraphs =
            [
                P(0, "Standard Procurement Document Table of Contents Section I - Instructions to Bidders ........ 6 Section II - Bid Data Sheet ........ 31"),
                P(1, "PART 1 - Bidding Procedures"),
                P(2, "Section I - Instructions to Bidders (ITB) 4 Section I - Instructions to Bidders Contents 1. Scope of Bid ........................................6 2. Source of Funds ........................................6"),
                P(3, "1.1 Definitions ......................................................................................................148"),
                P(4, "Section II - Bid Data Sheet (BDS)"),
                P(5, "Section III - Evaluation and Qualification Criteria"),
                P(6, "Section IV - Bidding Forms"),
                P(7, "Section V - Eligible Countries"),
            ],
        }.Build();

        var headings = PartSectionOutline.Build(doc);

        Assert.Equal([1, 2, 4, 5, 6, 7], headings.Select(h => h.Index));
        Assert.Equal([1, 2, 2, 2, 2, 2], headings.Select(h => h.Level));
        Assert.DoesNotContain(headings, h => h.Text.StartsWith("1.1", StringComparison.Ordinal));
        Assert.All(headings, h => Assert.Equal("part_section_declared", h.ConfidenceBasis));
        Assert.True(PartSectionOutline.HasStrongSignal(doc));
    }

    [Fact]
    public void Khong_kich_hoat_cho_typed_numbering_thuan()
    {
        var doc = new SlimDocument
        {
            FileName = "rfc.docx",
            SourcePath = "rfc.docx",
            Paragraphs =
            [
                P(0, "1. Introduction"),
                P(1, "1.1. Requirements Notation"),
                P(2, "2. Overview of Cache Operation"),
                P(3, "Section 4.e of the Trust Legal Provisions and are provided without warranty."),
            ],
        }.Build();

        Assert.False(PartSectionOutline.HasStrongSignal(doc));
    }

    [Fact]
    public void Nhan_section_heading_dung_cap_du_dash_bi_mojibake()
    {
        Assert.Equal(2, PartSectionOutline.LevelForHeading("Section II \u00E2\u20AC\u201C Bid Data Sheet (BDS) 31"));
        Assert.Equal(2, PartSectionOutline.LevelForHeading("Section VII - Works' Requirements Contents"));
    }

    [Fact]
    public void Khong_coi_section_clause_cham_chu_thuong_la_heading()
    {
        Assert.Null(PartSectionOutline.LevelForHeading("Section 4.e of the Trust Legal Provisions and are provided without warranty."));
    }

    [Fact]
    public void Cat_page_header_section_khong_de_than_bai_thanh_key_rieng()
    {
        var doc = new SlimDocument
        {
            FileName = "wb.docx",
            SourcePath = "wb.docx",
            Paragraphs =
            [
                P(0, "PART 1 - Bidding Procedures"),
                P(1, "Section I - Instructions to Bidders (ITB) 4 Section I - Instructions to Bidders Contents 1. Scope of Bid .......................................................6"),
                P(2, "Section I - Instructions to Bidders (ITB) 7 (or other financing) Agreement or have any claim to the proceeds of the Loan."),
                P(3, "Section II - Bid Data Sheet (BDS)"),
                P(4, "Section III - Evaluation and Qualification Criteria"),
                P(5, "Section IV - Bidding Forms"),
                P(6, "Section V - Eligible Countries"),
            ],
        }.Build();

        var headings = PartSectionOutline.Build(doc);

        Assert.Contains(headings, h => h.Index == 1 && h.Text == "Section I - Instructions to Bidders (ITB)");
        Assert.DoesNotContain(headings, h => h.Index == 2);
    }

    [Fact]
    public void Dung_toc_text_lam_nguon_title_va_body_lam_neo_khi_mat_xml_signal()
    {
        var doc = new SlimDocument
        {
            FileName = "wb-030-like.docx",
            SourcePath = "wb-030-like.docx",
            Paragraphs =
            [
                P(0, "TABLE OF CONTENT PART I .......... 5 Section 1. Alpha .......... 5 " +
                     "Section 2. Beta .......... 6 Section 3. Gamma .......... 7 " +
                     "Section 4. Delta .......... 8 Section 5. Epsilon .......... 9"),
                // Sub-TOC nội bộ của Section 2: cùng có "TABLE OF CONTENT" + nhiều dot-leader,
                // nhưng running header phía trước dot-leader đầu tiên khớp giả một entry "Section
                // 2." — phải bị lọc vì cả đoạn chỉ cho ra ĐÚNG MỘT entry PART/Section.
                P(1, "Section 2. Beta 999 TABLE OF CONTENT A. One .......... 1 B. Two .......... 2 " +
                     "C. Three .......... 3 D. Four .......... 4 E. Five .......... 5 F. Six .......... 6"),
                P(2, "Header 5 PART I Section 1. Alpha"),
                P(3, "Header 6 Section 2. Beta"),
                P(4, "Header 7 Section 3. Gamma"),
                P(5, "Header 8 Section 4. Delta"),
                P(6, "Header 9 Section 5. Epsilon"),
            ],
        }.Build();

        Assert.True(PartSectionOutline.HasTextTocSignal(doc));

        var headings = PartSectionOutline.BuildFromTextToc(doc);

        Assert.Equal([2, 2, 3, 4, 5, 6], headings.Select(h => h.Index));
        Assert.Contains(headings, h => h.Index == 2 && h.Level == 1 && h.Text == "PART I");
        Assert.Contains(headings, h => h.Index == 2 && h.Level == 2 && h.Text == "Section 1. Alpha");
        Assert.DoesNotContain(headings, h => h.Text.StartsWith("Section 2. Beta 999", StringComparison.Ordinal));
        Assert.All(headings, h => Assert.Equal("part_section_toc_text", h.ConfidenceBasis));
    }

    private static SlimParagraph P(int index, string text) => new()
    {
        Index = index,
        StableId = $"body[1]/p[{index + 1}]",
        Text = text,
        FontSizePt = 12,
    };
}
