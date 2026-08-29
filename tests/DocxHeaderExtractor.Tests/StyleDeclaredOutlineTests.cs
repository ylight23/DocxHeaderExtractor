using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Outline = ĐÚNG những gì tác giả khai bằng style Heading, cấp suy từ ký hiệu đánh số — định nghĩa
/// do người dùng xác nhận (§41). Đo trên khoá luận: <b>P 100% · R 100% · đúng cấp 100%</b>, 1,1 giây,
/// không gọi mô hình.
/// </summary>
public class StyleDeclaredOutlineTests
{
    /// <summary>
    /// Ba nhánh cấp, đúng cột <c>evidence</c> của đáp án: số gõ tay độ sâu d → d+1; numPr → 2;
    /// còn lại → 1.
    /// </summary>
    [Fact]
    public void Cap_suy_dung_ba_nhanh_bang_chung()
    {
        Assert.Equal(1, StyleDeclaredOutline.LevelOf(P("CHƯƠNG 1: CƠ SỞ LÝ LUẬN")));
        Assert.Equal(1, StyleDeclaredOutline.LevelOf(P("MỞ ĐẦU")));
        Assert.Equal(2, StyleDeclaredOutline.LevelOf(P("1. Lý do chọn đề tài", listId: 38)));
        Assert.Equal(3, StyleDeclaredOutline.LevelOf(P("1.1. Những khái niệm liên quan")));
        Assert.Equal(4, StyleDeclaredOutline.LevelOf(P("1.1.1. Nội dung truyền hình")));
        Assert.Equal(5, StyleDeclaredOutline.LevelOf(P("1.1.3.1. Khái niệm về mạng xã hội")));
    }

    /// <summary>
    /// Số gõ tay THẮNG numPr: <c>3.1.</c> nằm trong danh sách Word vẫn là cấp 3, không phải cấp 2.
    /// Thứ tự hai nhánh này quyết định 32/68 mục trên khoá luận.
    /// </summary>
    [Fact]
    public void So_go_tay_thang_numPr()
    {
        Assert.Equal(3, StyleDeclaredOutline.LevelOf(P("3.1. Mục đích nghiên cứu", listId: 38)));
    }

    /// <summary>Chỉ đoạn mang style Heading mới vào outline — đây là điểm bản lề của định nghĩa.</summary>
    [Fact]
    public void Chi_doan_mang_style_moi_vao_outline()
    {
        var doc = Doc(
            (0, "CHƯƠNG 1", true, null),
            (2, "1.1. Mục lớn", true, null),
            (4, "Tiểu kết chương 1", false, null),      // in đậm, không style
            (6, "1. Mục có numPr nhưng không style", false, 38));

        var outline = StyleDeclaredOutline.Build(doc);

        Assert.Equal([0, 2], outline.Select(h => h.Index));
        Assert.Equal([1, 3], outline.Select(h => h.Level));
    }

    /// <summary>Mọi mục đều là tuyên bố tường minh của tác giả nên tự nhận, không cần duyệt.</summary>
    [Fact]
    public void Moi_muc_deu_tu_nhan_vi_la_tuyen_bo_cua_tac_gia()
    {
        var outline = StyleDeclaredOutline.Build(Doc((0, "MỞ ĐẦU", true, null)));

        Assert.All(outline, h =>
        {
            Assert.Equal(HeadingSource.Style, h.Source);
            Assert.Equal(HeadingDecisionStatus.AutoAcceptedEvidence, h.DecisionStatus);
        });
    }

    [Fact]
    public void P1_P4_native_producers_match_legacy_outputs()
    {
        var doc = new SlimDocument
        {
            FileName = "producer-parity.docx",
            SourcePath = "producer-parity.docx",
            Paragraphs =
            [
                new SlimParagraph { Index = 0, StableId = "p[0]", Text = "1. Overview", HasBuiltInHeadingStyle = true, StyleId = "Heading1", OutlineLevel = 0 },
                new SlimParagraph { Index = 1, StableId = "p[1]", Text = "1.1. Requirements", HasBuiltInHeadingStyle = true, StyleId = "Heading2", OutlineLevel = 1 },
                new SlimParagraph { Index = 2, StableId = "p[2]", Text = "1.2. Syntax", HasBuiltInHeadingStyle = true, StyleId = "Heading2", OutlineLevel = 1 },
                new SlimParagraph { Index = 3, StableId = "p[3]", Text = "2. Operation", NumberingId = 7, NumberingLevel = 1, NumberLabel = "2.", HasBuiltInHeadingStyle = true, StyleId = "Heading1", OutlineLevel = 0 },
                new SlimParagraph { Index = 4, StableId = "p[4]", Text = "3. Security", NumberingId = 7, NumberingLevel = 1, NumberLabel = "3.", HasBuiltInHeadingStyle = true, StyleId = "Heading1", OutlineLevel = 0 },
                new SlimParagraph { Index = 5, StableId = "p[5]", Text = "4. Appendix", NumberingId = 7, NumberingLevel = 1, NumberLabel = "4.", HasBuiltInHeadingStyle = true, StyleId = "Heading1", OutlineLevel = 0 },
                new SlimParagraph { Index = 6, StableId = "p[6]", Text = "Body paragraph with ordinary prose." },
            ],
        }.Build();
        var native = PolicyStateFixture.FromSlim(doc).Paragraphs.Cast<IPolicyParagraph>().ToArray();

        Assert.Equal(StyleDeclaredOutline.Build(doc).Select(Project), StyleDeclaredOutline.Build(native).Select(Project));
        Assert.Equal(StyleDeclaredOutline.BuildFromOutlineLevel(doc).Select(Project), StyleDeclaredOutline.BuildFromOutlineLevel(native).Select(Project));
        Assert.Equal(StyleDeclaredOutline.BuildFromNumbering(doc).Select(Project), StyleDeclaredOutline.BuildFromNumbering(native).Select(Project));
        Assert.Equal(TypedNumberingOutline.Build(doc).Select(Project), TypedNumberingOutline.Build(native).Select(Project));
    }

    private static object Project(HeadingRecord heading) => new
    {
        heading.Index,
        heading.StableId,
        heading.SourceId,
        heading.Text,
        heading.Level,
        heading.HeadingSpan,
        heading.BoundarySource,
        heading.DecisionStatus,
        heading.ConfidenceBasis,
    };

    private static SlimParagraph P(string text, int? listId = null) =>
        new() { Index = 0, Text = text, HasBuiltInHeadingStyle = true, NumberingId = listId };

    private static SlimDocument Doc(params (int Index, string Text, bool Styled, int? ListId)[] items) =>
        new SlimDocument
        {
            FileName = "x.docx", SourcePath = "x.docx",
            Paragraphs = [.. items.Select(x => new SlimParagraph
            {
                Index = x.Index, Text = x.Text,
                HasBuiltInHeadingStyle = x.Styled, NumberingId = x.ListId,
            })],
        }.Build();
}
