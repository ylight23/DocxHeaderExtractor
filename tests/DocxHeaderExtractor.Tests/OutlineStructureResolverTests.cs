using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class OutlineStructureResolverTests
{
    [Fact]
    public void Recovers_and_levels_roman_arabic_letter_outline_and_removes_bullets()
    {
        var paragraphs = new[]
        {
            P(0, "I. VÙNG TRỜI", bold: true, caps: true),
            P(1, "1. Mục Alpha", bold: true),
            P(2, "- Trong kế hoạch: số liệu"),
            P(3, "2. Máy bay quân sự Ta", bold: true),
            P(4, "3. Máy bay quân sự nước ngoài", bold: true),
            P(5, "a) Trong dự báo"),
            P(6, "b) Ngoài dự báo"),
            P(7, "- KQ Trung Quốc: số liệu"),
            P(8, "II. VÙNG BIỂN", bold: true, caps: true),
            P(9, "1. Vùng biển phía Bắc", bold: true),
            P(10, "2. Vùng biển miền Trung", bold: true),
        };
        var accepted = new Dictionary<int, HeadingRecord>
        {
            [0] = H(0, 1, paragraphs),
            [1] = H(1, 2, paragraphs),
            [2] = H(2, 3, paragraphs), // false positive của model
            [4] = H(4, 6, paragraphs), // level drift
            [5] = H(5, 1, paragraphs),
            [8] = H(8, 3, paragraphs),
            [9] = H(9, 2, paragraphs),
        };

        var result = OutlineStructureResolver.Apply(paragraphs, accepted);

        Assert.Equal(3, result.Recovered); // I.2, b), và II.2
        Assert.Equal(1, result.Removed);
        Assert.DoesNotContain(2, accepted.Keys);
        Assert.DoesNotContain(7, accepted.Keys);
        Assert.Equal(1, accepted[0].Level);
        Assert.Equal(2, accepted[4].Level);
        Assert.Equal(3, accepted[5].Level);
        Assert.Equal(3, accepted[6].Level);
        Assert.Equal(1, accepted[8].Level);
        Assert.Equal(2, accepted[10].Level);
    }

    [Fact]
    public void Does_not_activate_for_an_isolated_numbered_list()
    {
        var paragraphs = new[] { P(0, "1. Một"), P(1, "2. Hai"), P(2, "a) ký hiệu") };
        var accepted = new Dictionary<int, HeadingRecord>();

        var result = OutlineStructureResolver.Apply(paragraphs, accepted);

        Assert.Equal(0, result.Recovered);
        Assert.Empty(accepted);
    }

    [Fact]
    public void Does_not_promote_numeric_status_label_that_looks_like_roman_prefix()
    {
        var paragraphs = new[]
        {
            P(10, "I. PHẦN MỘT", bold: true, caps: true),
            P(11, "1. Nội dung một", bold: true),
            P(12, "2. Nội dung hai", bold: true),
            P(83, "A: 04, B: 04,", bold: true, caps: true),
            P(320, "III. KẾT QUẢ THỰC HIỆN", bold: true, caps: true),
            P(321, "1. Công việc một", bold: true),
            P(322, "2. Công việc hai", bold: true),
        };
        var accepted = new Dictionary<int, HeadingRecord>();

        OutlineStructureResolver.Apply(paragraphs, accepted);

        Assert.DoesNotContain(83, accepted.Keys);
        Assert.Equal(1, accepted[10].Level);
        Assert.Equal(1, accepted[320].Level);
    }

    private static SlimParagraph P(int index, string text, bool bold = false, bool caps = false) =>
        new() { Index = index, StableId = $"p[{index}]", Text = text, Bold = bold, AllCaps = caps };

    private static HeadingRecord H(int index, int level, IReadOnlyList<SlimParagraph> paragraphs) =>
        new() { Index = index, Level = level, Text = paragraphs.Single(p => p.Index == index).Text };
}
