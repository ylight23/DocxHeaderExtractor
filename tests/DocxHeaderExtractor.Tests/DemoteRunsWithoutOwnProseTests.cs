using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class DemoteRunsWithoutOwnProseTests
{
    [Fact]
    public void Custom_style_lap_lai_duoi_outline_anchor_khong_bi_demote_theo_gia_dinh_prose()
    {
        var ps = new List<SlimParagraph>
        {
            P(0, "PART 2 – Employer’s Requirements", role: ParagraphRole.HeadingCandidate, score: 0.65, outline: 0),
            P(1, "Proposal Forms", style: "SPDForms1", role: ParagraphRole.HeadingCandidate, score: 0.65, bold: true, center: true),
            P(2, "Qualification Forms", style: "SPDForms1", role: ParagraphRole.HeadingCandidate, score: 0.65, bold: true, center: true),
            P(3, "Advance Payment Security", style: "SPDForms1", role: ParagraphRole.HeadingCandidate, score: 0.65, bold: true, center: true),
        };

        var state = State(ps);
        DocxSlimExtractor.DemoteRunsWithoutOwnProse(state.Paragraphs, structuralMarkers: 5);
        state.ApplyPolicyStateTo(ps);

        Assert.All(ps, p => Assert.True(p.IsCandidate));
    }

    [Fact]
    public void Ung_vien_khong_co_tin_hieu_rieng_van_bi_demote_trong_cum_khong_co_prose()
    {
        var ps = new List<SlimParagraph>
        {
            P(0, "Người lập biểu", role: ParagraphRole.HeadingCandidate, score: 0.45),
            P(1, "Nguyễn Văn A", role: ParagraphRole.HeadingCandidate, score: 0.45),
        };

        var state = State(ps);
        DocxSlimExtractor.DemoteRunsWithoutOwnProse(state.Paragraphs, structuralMarkers: 5);
        state.ApplyPolicyStateTo(ps);

        Assert.Equal(ParagraphRole.Normal, ps[0].Role);
        Assert.True(ps[1].IsCandidate);
    }

    private static SlimParagraph P(
        int index,
        string text,
        string style = "Normal",
        ParagraphRole role = ParagraphRole.Normal,
        double score = 0,
        int? outline = null,
        bool bold = false,
        bool center = false) => new()
    {
        Index = index,
        StableId = $"p[{index}]",
        Text = text,
        StyleId = style,
        OutlineLevel = outline,
        Bold = bold,
        Alignment = center ? "center" : "left",
        Role = role,
        Score = score,
    };

    private static OrderedDemotionState State(List<SlimParagraph> paragraphs)
    {
        var document = new SlimDocument
        {
            FileName = "demotion-test.docx",
            SourcePath = "demotion-test.docx",
            Paragraphs = paragraphs,
        }.Build();
        var source = SlimSourceFactsAdapter.Adapt(document);
        return OrderedDemotionState.Create(
            paragraphs,
            source,
            NumberingStyleFeatures.FromSourceDocument(source));
    }
}
