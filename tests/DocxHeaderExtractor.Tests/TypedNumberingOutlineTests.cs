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

    private static List<HeadingRecord> Build(params string[] texts)
    {
        List<SlimParagraph> ps = [];
        for (var i = 0; i < texts.Length; i++)
            ps.Add(new SlimParagraph { Index = i, StableId = $"p[{i}]", Text = texts[i], FontSizePt = 13 });
        var doc = new SlimDocument { FileName = "typed.docx", SourcePath = "typed.docx", Paragraphs = ps }.Build();
        return TypedNumberingOutline.Build(doc);
    }
}
