using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using Xunit;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Ranh giới quan trọng nhất của bộ lọc: cái gì là tín hiệu do ĐỊNH DẠNG OOXML quy định
/// (luôn bật, đúng với mọi ngôn ngữ) và cái gì là bảng từ khoá theo ngôn ngữ
/// (phải tắt được bằng --structural-only).
/// </summary>
public class LexicalGatingTests
{
    private static readonly ExtractionOptions Lexical = new() { UseLexicalRules = true };
    private static readonly ExtractionOptions Structural = new() { UseLexicalRules = false };

    private static SlimParagraph P(string text, string? styleId = null, string? styleName = null,
        int? outline = null, bool bold = false, double? size = null) =>
        new()
        {
            Index = 0,
            Text = text,
            StyleId = styleId,
            StyleName = styleName,
            OutlineLevel = outline,
            Bold = bold,
            FontSizePt = size,
            BodyFontSizePt = 13,
        };

    [Theory]
    [InlineData("Heading1", 1)]
    [InlineData("Heading3", 3)]
    [InlineData("heading 2", 2)]
    [InlineData("Title", 1)]
    [InlineData("Subtitle", 2)]
    [InlineData("TOCHeading", 1)]
    public void Built_in_ooxml_styles_are_recognised_in_both_modes(string style, int expected)
    {
        var p = P("Bất kỳ nội dung gì", styleId: style);

        Assert.Equal(expected, HeadingHeuristics.LevelFromStyle(p, Lexical));
        Assert.Equal(expected, HeadingHeuristics.LevelFromStyle(p, Structural));
    }

    [Fact]
    public void Outline_level_alone_is_enough_in_both_modes()
    {
        var p = P("Tiêu đề dùng style tự chế", styleId: "MyCustomStyle", outline: 2);

        Assert.Equal(3, HeadingHeuristics.LevelFromStyle(p, Lexical));
        Assert.Equal(3, HeadingHeuristics.LevelFromStyle(p, Structural));
    }

    [Fact]
    public void Outline_level_in_table_is_evidence_not_trusted_style()
    {
        var p = P("Câu hướng dẫn biểu mẫu.", styleId: "Normal", outline: 0, size: 14);
        p = new SlimParagraph
        {
            Index = p.Index, Text = p.Text, StyleId = p.StyleId, OutlineLevel = p.OutlineLevel,
            FontSizePt = p.FontSizePt, BodyFontSizePt = p.BodyFontSizePt, TableDepth = 1,
        };

        HeadingHeuristics.Classify(p, Structural);

        Assert.NotEqual(ParagraphRole.StyledHeading, p.Role);
        Assert.False(p.HasBuiltInHeadingStyle);
    }

    [Fact]
    public void Multi_level_numbering_in_table_remains_a_candidate()
    {
        var p = new SlimParagraph
        {
            Index = 0,
            Text = "3.2. Ngoài dự báo (tổng số tốp/số chiếc/tốp đêm): 02/02/0",
            StyleId = "Normal",
            BodyFontSizePt = 12,
            FontSizePt = 12,
            TableDepth = 1,
        };

        HeadingHeuristics.Classify(p, Structural);

        Assert.True(p.IsCandidate);
        Assert.Equal(2, p.GuessedLevel);
    }

    [Theory]
    [InlineData("Tiêu đề 2")]
    [InlineData("Überschrift 2")]
    [InlineData("Заголовок 2")]
    public void Localized_style_names_only_count_when_lexical_rules_are_on(string styleName)
    {
        var p = P("Nội dung", styleId: "Custom", styleName: styleName);

        Assert.Equal(2, HeadingHeuristics.LevelFromStyle(p, Lexical));
        Assert.Null(HeadingHeuristics.LevelFromStyle(p, Structural));
    }

    [Fact]
    public void Dang_tu_nhan_kem_so_la_bang_chung_cau_truc_nen_khong_phu_thuoc_luat_tu_ngu()
    {
        // Đổi hợp đồng có chủ đích: "Chương 1 Tổng quan" từng được nhận nhờ một DANH SÁCH TỪ KHOÁ
        // (chương|phần|mục|điều|chapter|section|…) nằm sau cờ luật-từ-ngữ. Danh sách đó chỉ đúng
        // với vốn từ ai đó nghĩ ra sẵn, và giao diện lại mặc định TẮT cờ này — nên ở đúng cấu hình
        // chạy thật, dạng đánh số phổ biến nhất của văn bản Việt Nam không được điểm nào.
        // Giờ nó được nhận bằng HÌNH DẠNG (từ nhãn + số + phần tên), nên hai cấu hình phải bằng nhau.
        var lex = P("Chương 1 Tổng quan", bold: true);
        var str = P("Chương 1 Tổng quan", bold: true);

        HeadingHeuristics.Classify(lex, Lexical);
        HeadingHeuristics.Classify(str, Structural);

        Assert.Equal(lex.Score, str.Score);
        Assert.Equal(ParagraphRole.HeadingCandidate, str.Role);
    }

    [Fact]
    public void Caption_is_rejected_only_when_lexical_rules_are_on()
    {
        var lex = P("Hình ảnh 2.6. Giao diện kênh Youtube", bold: true);
        var str = P("Hình ảnh 2.6. Giao diện kênh Youtube", bold: true);

        HeadingHeuristics.Classify(lex, Lexical);
        HeadingHeuristics.Classify(str, Structural);

        Assert.Equal(ParagraphRole.Normal, lex.Role);
        Assert.Equal(0, lex.Score);
        Assert.True(str.Score > 0, "structural-only không được biết 'Hình ảnh' nghĩa là gì");
    }

    [Fact]
    public void Structural_signals_still_work_without_any_lexicon()
    {
        // Không từ khoá nào, chỉ định dạng: ngắn, đậm, chữ to hơn thân bài, canh giữa.
        var p = new SlimParagraph
        {
            Index = 0,
            Text = "Lorem Ipsum Dolor",
            StyleId = "Normal",
            Bold = true,
            AllCaps = true,
            FontSizePt = 17,
            BodyFontSizePt = 13,
            Alignment = "center",
            KeepNext = true,
        };

        HeadingHeuristics.Classify(p, Structural);

        Assert.Equal(ParagraphRole.HeadingCandidate, p.Role);
    }

    /// <summary>
    /// Đoạn thân bài bị gán nhầm w:outlineLvl là lỗi có thật trong tài liệu hành chính.
    /// Ký tự gạch đầu dòng phải thắng được style, nếu không nhánh style thoát sớm và
    /// mọi luật về hình thức không bao giờ chạy.
    /// </summary>
    [Theory]
    [InlineData("- Kích thước dữ liệu: Khoảng 200 GB trong 5 năm đầu.")]
    [InlineData("• Thành phần thứ hai của hệ thống")]
    [InlineData("+ Mục bổ sung theo yêu cầu")]
    public void Bullet_prefix_overrides_outline_level(string text)
    {
        var p = P(text, styleId: "Normal", outline: 3, size: 14);

        HeadingHeuristics.Classify(p, Structural);

        Assert.NotEqual(ParagraphRole.StyledHeading, p.Role);
        Assert.Equal(ParagraphRole.Normal, p.Role);
    }

    /// <summary>Không có numbering/list metadata thì định dạng đậm không đủ cứu bullet.</summary>
    [Fact]
    public void Bullet_prefix_veto_still_allows_strong_formatting_to_win_back()
    {
        var p = new SlimParagraph
        {
            Index = 0,
            Text = "– PHẦN THỨ HAI",
            StyleId = "Heading1",
            OutlineLevel = 0,
            Bold = true,
            AllCaps = true,
            FontSizePt = 18,
            BodyFontSizePt = 13,
            Alignment = "center",
            KeepNext = true,
        };

        HeadingHeuristics.Classify(p, Structural);

        Assert.Equal(ParagraphRole.Normal, p.Role);
    }

    [Fact]
    public void Uppercase_letter_prefix_is_not_limited_to_latin()
    {
        var cyrillic = P("Б) Второй раздел", bold: true);
        HeadingHeuristics.Classify(cyrillic, Structural);

        Assert.Equal(ParagraphRole.HeadingCandidate, cyrillic.Role);
    }
}
