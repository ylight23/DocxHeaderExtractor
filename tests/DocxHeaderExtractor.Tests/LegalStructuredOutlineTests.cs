using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class LegalStructuredOutlineTests
{
    [Fact]
    public void Chi_co_article_van_la_outline_phap_quy_khong_can_hai_signature()
    {
        var r = Build(
            "Article 1. Scope of regulation",
            "Article 2. Regulated entities",
            "Article 3. Interpretation of terms");

        Assert.Equal(3, r.Count);
        Assert.All(r, h => Assert.Equal(4, h.Level));
        Assert.All(r, h => Assert.Equal("legal_marker_declared", h.ConfidenceBasis));
    }

    [Fact]
    public void Chapter_va_article_co_cap_rieng_theo_he_phap_quy()
    {
        var r = Build(
            "Chapter II EMPLOYMENT AND RECRUITMENT",
            "Article 4. Employment and recruitment 1. Employers may recruit employees directly.",
            "Article 5. Employment contract");

        Assert.Equal(["Chapter II EMPLOYMENT AND RECRUITMENT", "Article 4. Employment and recruitment", "Article 5. Employment contract"],
            r.Select(h => h.Text));
        Assert.Equal([2, 4, 4], r.Select(h => h.Level));
        Assert.Equal("1. Employers may recruit employees directly.", r[1].InlineBody);
    }

    [Fact]
    public void Doan_gop_phap_quy_tach_duoc_nhieu_dieu_va_khong_nhan_khoan_lam_heading()
    {
        var r = Build(
            "Chương I QUY ĐỊNH CHUNG Điều 1. Phạm vi điều chỉnh1. Luật này quy định về quản lý." +
            "Điều 2. Đối tượng áp dụng 1. Cơ quan, tổ chức, cá nhân.");

        Assert.Equal(["Chương I QUY ĐỊNH CHUNG", "Điều 1. Phạm vi điều chỉnh", "Điều 2. Đối tượng áp dụng"],
            r.Select(h => h.Text));
        Assert.Equal([2, 4, 4], r.Select(h => h.Level));
    }

    [Fact]
    public void Tham_chieu_cheo_khong_thanh_heading()
    {
        var r = Build("Việc áp dụng quy định tại Điều 3 của Bộ luật này phải bảo đảm nguyên tắc chung.");

        Assert.Empty(r);
    }

    [Fact]
    public void Nhan_duoc_nhan_tieng_Viet_dau_to_hop_tu_pdf_convert()
    {
        var decomposed = "Điều 4. Áp dụng Bộ luật dân sự1. Nội dung.";

        var r = Build(decomposed);

        Assert.Single(r);
        Assert.Equal("Điều 4. Áp dụng Bộ luật dân sự", r[0].Text);
    }

    [Fact]
    public void Splitter_generic_khong_cat_lai_heading_phap_quy_da_tach()
    {
        var text = "Chương I QUY ĐỊNH CHUNG Điều 1. Phạm vi điều chỉnh 1. Luật này quy định." +
                   " Điều 2. Đối tượng áp dụng 1. Cơ quan, tổ chức.";
        var doc = Doc(text);
        var r = LegalStructuredOutline.Build(doc);
        var before = r.Select(h => h.Text).ToArray();

        var changed = InlineHeadingSplitter.Apply(r, doc);

        Assert.Equal(0, changed);
        Assert.Equal(before, r.Select(h => h.Text));
        Assert.Equal(["Chương I QUY ĐỊNH CHUNG", "Điều 1. Phạm vi điều chỉnh", "Điều 2. Đối tượng áp dụng"],
            r.Select(h => h.Text));
    }

    private static List<HeadingRecord> Build(params string[] texts)
        => LegalStructuredOutline.Build(Doc(texts));

    private static SlimDocument Doc(params string[] texts)
    {
        List<SlimParagraph> ps = [];
        for (var i = 0; i < texts.Length; i++)
            ps.Add(new SlimParagraph { Index = i, StableId = $"p[{i}]", Text = texts[i], FontSizePt = 13 });
        return new SlimDocument { FileName = "legal.docx", SourcePath = "legal.docx", Paragraphs = ps }.Build();
    }
}
