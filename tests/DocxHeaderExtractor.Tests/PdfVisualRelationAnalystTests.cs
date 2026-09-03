using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfVisualRelationAnalystTests
{
    [Fact]
    public void BuildQuestion_Constrains_the_vlm_to_an_existing_pair()
    {
        var upper = Block("group", "Key Trust Fund Activity", 200);
        var lower = Block("title", "Trust Fund Asset Summary", 170);

        var prompt = PdfVisualRelationAnalyst.BuildQuestion(upper, lower);

        Assert.Contains("không trích xuất", prompt);
        Assert.Contains("không sửa text", prompt);
        Assert.Contains("không tạo heading mới", prompt);
        Assert.Contains("không tự gán cấp cuối", prompt);
        Assert.Contains("grounding kiểm lại", prompt);
    }

    [Fact]
    public void ParseDecision_Accepts_grounded_parent_child_relation()
    {
        var decision = PdfVisualRelationAnalyst.ParseDecision(
            "group", "title",
            "{\"upperId\":\"group\",\"lowerId\":\"title\",\"relation\":\"parent_child\",\"confidence\":0.84,\"evidence\":\"A là nhãn phần riêng biệt ngay trên tiêu đề B\"}");

        Assert.Equal(PdfVisualBlockRelation.ParentChild, decision.Relation);
        Assert.Equal(0.84, decision.Confidence, precision: 2);
    }

    [Fact]
    public void ParseDecision_Rejects_mismatched_pair_or_empty_evidence()
    {
        var wrongPair = PdfVisualRelationAnalyst.ParseDecision(
            "a", "b",
            "{\"upperId\":\"a\",\"lowerId\":\"c\",\"relation\":\"siblings\",\"confidence\":0.9,\"evidence\":\"cùng kiểu hiển thị trên trang\"}");
        var noEvidence = PdfVisualRelationAnalyst.ParseDecision(
            "a", "b",
            "{\"upperId\":\"a\",\"lowerId\":\"b\",\"relation\":\"siblings\",\"confidence\":0.9,\"evidence\":\"...\"}");

        Assert.Equal("id-mismatch", wrongPair.Evidence);
        Assert.Equal("unusable-evidence", noEvidence.Evidence);
    }

    private static PdfSemanticBlock Block(string id, string text, double y) => new(
        id, [], new PdfStyleKey(12, "Arial", "black"), 1, y, y - 10, 72, 300, text);
}
