using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class TypedNumberingOutlineTests
{
    [Fact]
    public void Cap_lay_truc_tiep_tu_do_sau_marker()
    {
        var r = Build(
            "1. Overview",
            "1.1. Requirements Notation",
            "1.2. Syntax Notation",
            "1.2.1. Imported Rules",
            "2. Operation");

        Assert.Equal([1, 2, 2, 3, 1], r.Select(h => h.Level));
        Assert.All(r, h => Assert.Equal("typed_number_depth", h.ConfidenceBasis));
    }

    [Fact]
    public void Khong_suy_cap_theo_thu_tu_signature_nhu_hanh_chinh()
    {
        var r = Build(
            "1.1. Requirements Notation",
            "1.1.1. Imported Rules",
            "2. Overview");

        Assert.Equal([2, 3, 1], r.Select(h => h.Level));
    }

    [Fact]
    public void Bo_footer_RFC_lap_lai_khoi_tieu_de()
    {
        var r = Build("4.2. Freshness Fielding, et al. Standards Track Page 11");

        Assert.Single(r);
        Assert.Equal("4.2. Freshness", r[0].Text);
        Assert.Equal(2, r[0].Level);
    }

    [Fact]
    public void Khong_bo_noi_dung_thuong_khong_phai_footer()
    {
        var text = "4.2. Freshness Fielding explains cache behavior";
        var r = Build(text);

        Assert.Single(r);
        Assert.Equal(text, r[0].Text);
    }

    [Fact]
    public void Bo_muc_luc_go_tay_khoi_outline_than_bai()
    {
        var doc = new SlimDocument
        {
            FileName = "typed.docx",
            SourcePath = "typed.docx",
            Paragraphs =
            [
                new SlimParagraph
                {
                    Index = 0,
                    StableId = "p[0]",
                    Text = "1.1 Basic American Legal Principles 3 1.2 Sources and Types of Law 5",
                    InTableOfContents = true,
                },
                new SlimParagraph
                {
                    Index = 1,
                    StableId = "p[1]",
                    Text = "1.1 Basic American Legal Principles The American legal system has its roots.",
                },
            ],
        }.Build();

        var r = TypedNumberingOutline.Build(doc);

        Assert.Single(r);
        Assert.Equal("p[1]", r[0].StableId);
    }

    [Fact]
    public void Bo_muc_luc_go_tay_bi_gop_thanh_mot_doan_dai()
    {
        var r = Build(
            "1 Chapter One 3 1.1 First Section 3 1.2 Second Section 5 2 Chapter Two 9 2.1 Third Section 10 2.2 Fourth Section 12",
            "1.1 First Section Body text");

        Assert.Single(r);
        Assert.Equal("p[1]", r[0].StableId);
    }

    [Fact]
    public void Bo_page_header_text_layout_khoi_outline_than_bai()
    {
        var r = Build(
            "4 1 • American Law, Legal Reasoning, and the Legal System Figure 1.2 body text",
            "1.1 Basic American Legal Principles Body text");

        Assert.Single(r);
        Assert.StartsWith("1.1", r[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_inline_body_ghi_span_khop_nguon()
    {
        var r = Build("1. Budget Anchor: 123 million reported");

        var h = Assert.Single(r);
        Assert.Equal("1. Budget Anchor", h.Text);
        Assert.Equal("123 million reported", h.InlineBody);
        Assert.Equal("1. Budget Anchor: 123 million reported", h.OriginalText);
        Assert.Equal(new TextOffsetSpan(0, "1. Budget Anchor".Length), h.HeadingSpan);
        Assert.Equal(new TextOffsetSpan("1. Budget Anchor: ".Length, h.OriginalText!.Length), h.InlineBodySpan);
    }

    [Fact]
    public void Bo_caption_label_table_figure_box_note_nhung_giu_section()
    {
        var r = Build(
            "Table 12: Net Commitments by Region In millions of U.S. dollars",
            "Figure 8: Borrowings In billions of U.S. dollars",
            "Box 3: Types of Guarantees Provided by IBRD",
            "Note C - Investments and Note H - Transactions",
            "SECTION V: OTHER DEVELOPMENT ACTIVITIES");

        var only = Assert.Single(r);
        Assert.StartsWith("SECTION V", only.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Section_declared_dung_cap_semantic_thay_vi_do_sau_marker()
    {
        var doc = new SlimDocument
        {
            FileName = "wb.docx",
            SourcePath = "wb.docx",
            Paragraphs =
            [
                P(0, "PART 1 - Bidding Procedures"),
                P(1, "Section I - Instructions to Bidders"),
                P(2, "Section II \u00E2\u20AC\u201C Bid Data Sheet (BDS) 31"),
                P(3, "Section III - Evaluation and Qualification Criteria"),
                P(4, "Section IV - Bidding Forms"),
                P(5, "SECTION V: OTHER DEVELOPMENT ACTIVITIES"),
            ],
        }.Build();

        var only = Assert.Single(TypedNumberingOutline.Build(doc), h => h.Text.StartsWith("SECTION V", StringComparison.Ordinal));
        Assert.Equal(2, only.Level);
    }

    [Fact]
    public void Bo_so_thap_phan_kem_don_vi_nhung_giu_heading_decimal_that()
    {
        var r = Build(
            "1.5 GHz is a good starting point for the local model.",
            "1.5 Model Validation The validation step compares held-out data.");

        var only = Assert.Single(r);
        Assert.StartsWith("1.5 Model Validation", only.Text, StringComparison.Ordinal);
        Assert.Equal(2, only.Level);
    }

    [Fact]
    public void Bo_so_tien_va_ty_le_thap_phan_nhung_giu_heading_decimal_that()
    {
        var r = Build(
            "39.9 billion was committed, and $33.1 billion was disbursed by IDA.",
            "75.5 percent of respondents answered the survey.",
            "1.5 Model Validation The validation step compares held-out data.");

        var only = Assert.Single(r);
        Assert.StartsWith("1.5 Model Validation", only.Text, StringComparison.Ordinal);
        Assert.Equal(2, only.Level);
    }

    [Fact]
    public void Bo_dong_bang_day_ma_so_nhung_giu_heading_decimal_that()
    {
        var r = Build(
            "65.68 (HER) PROJECT Emergency Food Security 3 P178280 2022",
            "4.71 Africa Senegal River Basin Western and 1306 P131323 Climate Change Resilience 2014",
            "1.5 Model Validation The validation step compares held-out data.");

        var only = Assert.Single(r);
        Assert.StartsWith("1.5 Model Validation", only.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nhan_dien_layout_typed_chu_yeu_la_so_lieu_bang()
    {
        var rows = Enumerable.Range(0, 40)
            .Select(i => P(i, $"{i + 1}.23 Project Alpha {100 + i} P{170000 + i} 2025"))
            .ToList();
        var doc = new SlimDocument { FileName = "finance.docx", SourcePath = "finance.docx", Paragraphs = rows }.Build();

        Assert.True(TypedNumberingOutline.LooksLikeQuantitativeTypedLayout(doc));
    }

    [Fact]
    public void Bo_duong_dan_so_co_thanh_phan_0_vi_thuong_la_so_lieu_hoac_code()
    {
        var r = Build(
            "0.85. This means the error shrinks quickly.",
            "1.0 samples = 2 samples = 31 samples = 73",
            "1.1 Valid Heading Body text.");

        var only = Assert.Single(r);
        Assert.StartsWith("1.1 Valid Heading", only.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Cat_title_sach_text_layout_co_bullet_va_so_trang_nhung_giu_span_nguon()
    {
        var r = Build("2.1 • Negotiation 15 prone to zero-sum thinking.");

        var only = Assert.Single(r);
        Assert.Equal("2.1 Negotiation", only.Text);
        Assert.Equal("prone to zero-sum thinking.", only.InlineBody);
        Assert.Equal("2.1 • Negotiation 15 prone to zero-sum thinking.", only.OriginalText);
        Assert.Equal(new TextOffsetSpan(0, "2.1 • Negotiation 15 ".Length), only.HeadingSpan);
        Assert.Equal(new TextOffsetSpan("2.1 • Negotiation 15 ".Length, only.OriginalText!.Length), only.InlineBodySpan);
    }

    private static List<HeadingRecord> Build(params string[] texts)
    {
        List<SlimParagraph> ps = [];
        for (var i = 0; i < texts.Length; i++)
            ps.Add(P(i, texts[i]));
        var doc = new SlimDocument { FileName = "typed.docx", SourcePath = "typed.docx", Paragraphs = ps }.Build();
        return TypedNumberingOutline.Build(doc);
    }

    private static SlimParagraph P(int index, string text) => new()
    {
        Index = index,
        StableId = $"p[{index}]",
        Text = text,
        FontSizePt = 13,
    };
}
