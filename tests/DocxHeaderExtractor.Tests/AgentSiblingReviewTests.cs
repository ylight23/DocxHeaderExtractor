using DocxHeaderExtractor.HeadingAgent.Grouping;

namespace DocxHeaderExtractor.Tests;

/// <summary>The numbering form is read from the text only, sequences follow the numbers, and an answer must name every member once.</summary>
public sealed class AgentSiblingReviewTests
{
    [Theory]
    [InlineData("3. Ủy ban nhân dân cấp xã thực hiện", "#.", 3)]
    [InlineData("Điều 43. Trách nhiệm của Ủy ban", "Điều #.", 43)]
    [InlineData("a) Thực hiện các việc chứng thực", null, 0)]
    [InlineData("Ủy ban nhân dân cấp xã có nhiệm vụ", null, 0)]
    public void The_form_is_the_words_before_the_first_number_the_number_and_its_punctuation(string text, string? key, int value)
    {
        var form = AgentSiblingReview.Form(text);
        if (key is null) { Assert.Null(form); return; }
        Assert.Equal((key, value), form!.Value);
    }

    [Fact]
    public void A_parse_takes_every_member_once_and_refuses_a_missing_or_unknown_one()
    {
        var members = new[] { "A", "B" };
        var ok = AgentSiblingReview.Parse("{\"protocolVersion\":\"" + AgentSiblingReview.Version + "\",\"decisions\":[{\"alias\":\"A\",\"decision\":\"NOT_SELECT\",\"reason\":\"x\"},{\"alias\":\"B\",\"decision\":\"SELECT\",\"reason\":\"y\"}]}", members, out var none);
        Assert.Null(none);
        Assert.Equal("NOT_SELECT", ok!["A"]);
        Assert.Null(AgentSiblingReview.Parse("{\"protocolVersion\":\"" + AgentSiblingReview.Version + "\",\"decisions\":[{\"alias\":\"A\",\"decision\":\"SELECT\"}]}", members, out var missing));
        Assert.Equal("MEMBER_MISSING", missing);
        Assert.Null(AgentSiblingReview.Parse("{\"protocolVersion\":\"" + AgentSiblingReview.Version + "\",\"decisions\":[{\"alias\":\"C\",\"decision\":\"SELECT\"}]}", members, out var unknown));
        Assert.Equal("ALIAS_UNKNOWN", unknown);
    }
}
