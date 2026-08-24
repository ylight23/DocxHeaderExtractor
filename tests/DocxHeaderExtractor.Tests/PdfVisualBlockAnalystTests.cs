using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfVisualBlockAnalystTests
{
    [Fact]
    public void BuildQuestion_Constrains_the_vlm_to_the_existing_candidate()
    {
        var block = Block("b7", "Trust Fund Asset Summary");

        var prompt = PdfVisualBlockAnalyst.BuildQuestion(block);

        Assert.Contains("Không tạo block mới", prompt);
        Assert.Contains("không tự gán cấp/cha-con", prompt);
        Assert.Contains("trả uncertain", prompt);
        Assert.Contains("\"id\":\"b7\"", prompt);
    }

    [Fact]
    public void ParseDecision_Accepts_grounded_heading_with_usable_evidence()
    {
        var decision = PdfVisualBlockAnalyst.ParseDecision(
            "b7",
            "{\"id\":\"b7\",\"role\":\"heading_topic\",\"confidence\":0.82,\"evidence\":\"ảnh crop hiển thị dòng Trust Fund Asset Summary như nhãn mục\"}");

        Assert.Equal("b7", decision.Id);
        Assert.Equal(PdfBlockRole.HeadingTopic, decision.Role);
        Assert.Equal(0.82, decision.Confidence, precision: 2);
    }

    [Fact]
    public void ParseDecision_Rejects_placeholder_or_empty_evidence()
    {
        var decision = PdfVisualBlockAnalyst.ParseDecision(
            "b1",
            "{\"id\":\"b1\",\"role\":\"heading_topic\",\"confidence\":0.9,\"evidence\":\"...\"}");

        Assert.Equal(PdfBlockRole.Uncertain, decision.Role);
        Assert.Equal(0, decision.Confidence);
        Assert.Equal("unusable-evidence", decision.Evidence);
    }

    [Fact]
    public void ParseDecision_Rejects_wrong_block_id()
    {
        var decision = PdfVisualBlockAnalyst.ParseDecision(
            "b1",
            "{\"id\":\"b2\",\"role\":\"heading_topic\",\"confidence\":0.9,\"evidence\":\"ảnh crop có một nhãn mục rõ ràng\"}");

        Assert.Equal(PdfBlockRole.Uncertain, decision.Role);
        Assert.Equal("id-mismatch", decision.Evidence);
    }

    [Fact]
    public void ParseDecision_Maps_table_label_role()
    {
        var decision = PdfVisualBlockAnalyst.ParseDecision(
            "b3",
            "{\"id\":\"b3\",\"role\":\"table_or_chart_label\",\"confidence\":0.76,\"evidence\":\"ảnh crop nằm trong bảng và có các số liệu USD kèm cột\"}");

        Assert.Equal(PdfBlockRole.TableOrChartLabel, decision.Role);
        Assert.Equal(0.76, decision.Confidence, precision: 2);
    }

    [Fact]
    public void SelectNeighborhoodUsesThreeNearestLinesAboveAndBelowOnSamePage()
    {
        var target = Line("Target heading", 500);
        var block = new PdfSemanticBlock("b1", [target], PdfStyleClusterProfile.StyleOf(target), 1, 500, 500, 72, 300, target.Text);
        var lines = new[]
        {
            Line("above far", 650), Line("above 3", 530), Line("above 2", 520), Line("above 1", 510),
            target,
            Line("below 1", 490), Line("below 2", 480), Line("below 3", 470), Line("below far", 400),
            new PdfLine(2, 700, 12, "other page", 0, "", 0, 72, 300, "Arial", "black"),
        };

        var neighborhood = PdfVisualBlockAnalyst.SelectNeighborhood(block, lines);

        Assert.Equal(new[] { 510d, 520d, 530d }, neighborhood.Above.Select(line => line.Y).Order());
        Assert.Equal(new[] { 470d, 480d, 490d }, neighborhood.Below.Select(line => line.Y).Order());
        Assert.True(neighborhood.TopY > 530);
        Assert.True(neighborhood.BottomY < 470);
    }

    private static PdfLine Line(string text, double y) => new(1, y, 12, text, 0.8, "", 0, 72, 300, "Arial", "black");

    private static PdfSemanticBlock Block(string id, string text) => new(
        id, [], new PdfStyleKey(12, "Arial", "black"), 1, 100, 90, 72, 300, text);
}
