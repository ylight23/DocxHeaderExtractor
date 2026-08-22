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
    public void BuildQuestion_uses_neighbors_as_context_without_turning_them_into_candidates()
    {
        var candidate = Block("l7", "Trust Fund Asset Summary");
        var neighbor = Block("l6", "Key Trust Fund Activity");

        var prompt = PdfVisualBlockAnalyst.BuildQuestion(candidate, [neighbor, candidate]);

        Assert.Contains("candidate: Trust Fund Asset Summary", prompt);
        Assert.Contains("- Key Trust Fund Activity", prompt);
        Assert.Contains("không được phân loại", prompt);
    }

    [Fact]
    public void ParseDecision_Accepts_grounded_heading_with_usable_evidence()
    {
        var decision = PdfVisualBlockAnalyst.ParseDecision(
            "b7",
            "{\"id\":\"b7\",\"role\":\"heading_topic\",\"confidence\":0.82,\"visualEvidence\":[\"standalone_label\",\"distinct_heading_style\"],\"evidence\":\"ảnh crop hiển thị dòng Trust Fund Asset Summary như nhãn mục\"}");

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
        Assert.Equal("unusable-evidence-tags", decision.Evidence);
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
            "{\"id\":\"b3\",\"role\":\"table_or_chart_label\",\"confidence\":0.76,\"visualEvidence\":[\"inside_table_grid\",\"numeric_column\"],\"evidence\":\"ảnh crop nằm trong bảng và có các số liệu USD kèm cột\"}");

        Assert.Equal(PdfBlockRole.TableOrChartLabel, decision.Role);
        Assert.Equal(0.76, decision.Confidence, precision: 2);
    }

    [Fact]
    public void ParseDecision_Rejects_heading_without_a_role_compatible_tag()
    {
        var decision = PdfVisualBlockAnalyst.ParseDecision(
            "b4",
            "{\"id\":\"b4\",\"role\":\"heading_topic\",\"confidence\":0.99,\"visualEvidence\":[\"numeric_column\"],\"evidence\":\"dòng trông nổi bật\"}");

        Assert.Equal(PdfBlockRole.Uncertain, decision.Role);
        Assert.Equal("unusable-evidence-tags", decision.Evidence);
    }

    [Fact]
    public void Batch_prompt_and_parser_keep_each_decision_grounded_to_its_block_id()
    {
        var first = Block("b1", "Introduction");
        var second = Block("b2", "Total assets");
        var prompt = PdfVisualBlockAnalyst.BuildBatchQuestion([first, second], [[first], [second]]);
        var decisions = PdfVisualBlockAnalyst.ParseBatchDecisions([first, second],
            "{\"blocks\":[{\"id\":\"b1\",\"role\":\"heading_topic\",\"confidence\":0.91,\"visualEvidence\":[\"standalone_label\",\"section_boundary\"],\"evidence\":\"crop shows a standalone section label above prose\"},{\"id\":\"b2\",\"role\":\"table_or_chart_label\",\"confidence\":0.88,\"visualEvidence\":[\"inside_table_grid\"],\"evidence\":\"crop shows a metric inside a numeric table\"}]}");

        Assert.Contains("crop 1; id=b1", prompt);
        Assert.Contains("crop 2; id=b2", prompt);
        Assert.Equal(PdfBlockRole.HeadingTopic, decisions[0].Role);
        Assert.Equal(PdfBlockRole.TableOrChartLabel, decisions[1].Role);
    }

    private static PdfSemanticBlock Block(string id, string text) => new(
        id, [], new PdfStyleKey(12, "Arial", "black"), 1, 100, 90, 72, 300, text);
}
