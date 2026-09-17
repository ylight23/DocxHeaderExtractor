using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticTextTrueHeadingContractTests
{
    [Fact]
    public void Heading_decision_is_required_and_independent_from_role()
    {
        var response = SemanticTextTrueHeadingContract.Parse("""
            {"headings":[
              {"source":"S1","text":"Section","headingDecision":"TRUE_HEADING","role":"CONTENT_HEADING"},
              {"source":"S2","text":"TOC entry","headingDecision":"NOT_TRUE_HEADING","role":"TOC_ENTRY"},
              {"source":"S3","text":"Label","headingDecision":"NOT_TRUE_HEADING","role":"OTHER_STRUCTURAL_LABEL"},
              {"source":"S4","text":"Section-like","headingDecision":"NOT_TRUE_HEADING","role":"SECTION"}
            ]}
            """);

        Assert.Equal("TRUE_HEADING", response.Headings[0].HeadingDecision);
        Assert.Equal("NOT_TRUE_HEADING", response.Headings[1].HeadingDecision);
        Assert.Equal("NOT_TRUE_HEADING", response.Headings[2].HeadingDecision);
        Assert.Equal("SECTION", response.Headings[3].Role);
    }

    [Fact]
    public void Missing_or_unknown_heading_decision_fails_closed()
    {
        Assert.Throws<FormatException>(() => SemanticTextTrueHeadingContract.Parse(
            "{\"headings\":[{\"source\":\"S1\",\"text\":\"x\",\"role\":\"SECTION\"}]}"));
        Assert.Throws<FormatException>(() => SemanticTextTrueHeadingContract.Parse(
            "{\"headings\":[{\"source\":\"S1\",\"text\":\"x\",\"headingDecision\":\"MAYBE\",\"role\":\"SECTION\"}]}"));
    }

    [Fact]
    public void Schema_exposes_heading_decision_as_required_independent_field()
    {
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(SemanticTextTrueHeadingContract.Schema()));
        var item = schema.RootElement.GetProperty("properties").GetProperty("headings")
            .GetProperty("items");
        Assert.True(item.GetProperty("properties").TryGetProperty("headingDecision", out _));
        Assert.Contains("headingDecision", item.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("TRUE_HEADING", SemanticTextTrueHeadingContract.System);
        Assert.Contains("NOT_TRUE_HEADING", SemanticTextTrueHeadingContract.System);
    }
}
