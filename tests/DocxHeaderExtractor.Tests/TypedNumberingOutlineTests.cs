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

    private static List<HeadingRecord> Build(params string[] texts)
    {
        List<SlimParagraph> ps = [];
        for (var i = 0; i < texts.Length; i++)
            ps.Add(new SlimParagraph { Index = i, StableId = $"p[{i}]", Text = texts[i], FontSizePt = 13 });
        var doc = new SlimDocument { FileName = "typed.docx", SourcePath = "typed.docx", Paragraphs = ps }.Build();
        return TypedNumberingOutline.Build(doc);
    }
}
