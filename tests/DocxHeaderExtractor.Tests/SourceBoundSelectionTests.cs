using System.Text.Json;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceBoundSelectionTests
{
    static readonly Dictionary<string, string> Texts = new()
    { ["A"] = "A sentence that continues the previous paragraph.", ["B"] = "3. Research methods" };
    static readonly Dictionary<string, int> Order = new() { ["A"] = 0, ["B"] = 1 };

    static AgentProtocol.SelectReply Parse(object[] decisions) => AgentProtocol.ParseSelect(JsonSerializer.Serialize(new
    { protocolVersion = SourceBoundSelection.Version, status = "COMPLETE", decisions }), Order, ["A", "B"],
        SourceBoundSelection.Version, Texts);

    [Fact]
    public void A_neighbors_heading_text_cannot_be_bound_to_the_body_alias()
    {
        var result = Parse([
            new { alias = "A", decision = "SELECT", sourceText = Texts["B"], evidenceRefs = new[] { "A" } },
            new { alias = "B", decision = "NOT_SELECT" }]);
        Assert.False(result.Ok);
        Assert.Equal("SOURCE_TEXT_MISMATCH", result.Failure);
        Assert.Empty(result.Values);
        Assert.Contains(Texts["A"], result.Detail);
        var repair = AgentProtocol.RepairMessage("PROTOCOL:" + result.Failure, null, result.Detail);
        Assert.Contains("never moves a selection", repair);
    }

    [Fact]
    public void The_model_can_correct_its_alias_and_text_without_a_CSharp_membership_override()
    {
        var result = Parse([
            new { alias = "A", decision = "NOT_SELECT" },
            new { alias = "B", decision = "SELECT", sourceText = Texts["B"], evidenceRefs = new[] { "B" } }]);
        Assert.True(result.Ok, result.Failure);
        Assert.Equal("SELECT", result.Values["B"]);
        Assert.Equal("NOT_SELECT", result.Values["A"]);
    }

    [Fact]
    public void A_source_bound_but_semantically_wrong_decision_is_not_silently_vetoed()
    {
        var result = Parse([
            new { alias = "A", decision = "SELECT", sourceText = Texts["A"], evidenceRefs = new[] { "A" } },
            new { alias = "B", decision = "NOT_SELECT" }]);
        Assert.True(result.Ok, result.Failure);
        Assert.Equal("SELECT", result.Values["A"]);
    }

    [Theory]
    [InlineData("missing", "SOURCE_TEXT_REQUIRED")]
    [InlineData("null", "SOURCE_TEXT_REQUIRED")]
    [InlineData("whitespace", "SOURCE_TEXT_MISMATCH")]
    [InlineData("on-not-select", "SOURCE_TEXT_NOT_ON_SELECT")]
    public void Binding_requires_verbatim_text_and_only_on_SELECTED_targets(string mode, string failure)
    {
        var decisions = new[]
        {
            new Dictionary<string, object?> { ["alias"] = "A", ["decision"] = "NOT_SELECT" },
            new Dictionary<string, object?> { ["alias"] = "B", ["decision"] = "SELECT", ["evidenceRefs"] = new[] { "B" } }
        };
        if (mode != "missing") decisions[1]["sourceText"] = mode == "null" ? null : mode == "whitespace" ? Texts["B"] + " " : Texts["B"];
        if (mode == "on-not-select") decisions[0]["sourceText"] = Texts["A"];
        var result = Parse(decisions);
        Assert.False(result.Ok);
        Assert.Equal(failure, result.Failure);
    }

    [Fact]
    public void Legacy_protocol_rejects_the_new_field_and_candidates_cannot_be_accidentally_combined()
    {
        var json = JsonSerializer.Serialize(new { protocolVersion = AgentProtocol.SelectExtentVersion, status = "COMPLETE",
            decisions = new[] { new { alias = "B", decision = "SELECT", sourceText = Texts["B"], evidenceRefs = new[] { "B" } } } });
        var result = AgentProtocol.ParseSelect(json, Order, ["B"], AgentProtocol.SelectExtentVersion, Texts);
        Assert.False(result.Ok);
        Assert.Equal("DECISION_KEYS_INVALID", result.Failure);
        Assert.Throws<ArgumentException>(() => new PdfSelector(inlineGrouping: true, sourceBound: true));
        Assert.DoesNotContain("PDF_SELECT_SOURCE_BOUND_V1", new PdfSelector().SelectorId);
    }
}
