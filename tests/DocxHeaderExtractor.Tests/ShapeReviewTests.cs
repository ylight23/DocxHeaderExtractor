using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent.Grouping;

namespace DocxHeaderExtractor.Tests;

public sealed class ShapeReviewTests
{
    static JsonNode Facts(string font, double size, double left, double bold = 0, int blockLines = 1) => new JsonObject
    {
        ["layout"] = new JsonObject
        {
            ["bbox"] = new JsonObject { ["left"] = left },
            ["font"] = new JsonObject { ["name"] = font, ["size"] = size, ["boldRatio"] = bold, ["italicRatio"] = 0.0 },
        },
        ["block"] = new JsonObject { ["lines"] = blockLines },
    };

    [Fact]
    public void Look_reads_measurements_only_and_rounds_the_edge_and_size()
    {
        Assert.Equal(ShapeReview.Look(Facts("serif", 10, 86.3)), ShapeReview.Look(Facts("serif", 10.1, 86.9)));
        Assert.NotEqual(ShapeReview.Look(Facts("serif", 10, 86)), ShapeReview.Look(Facts("serif", 10, 86, bold: 1)));
        Assert.NotEqual(ShapeReview.Look(Facts("serif", 10, 86)), ShapeReview.Look(Facts("serif", 10, 86, blockLines: 3)));
        Assert.Null(ShapeReview.Look(JsonNode.Parse("{}")));
    }

    [Fact]
    public void Only_look_alike_lines_that_disagree_across_requests_are_asked_again()
    {
        // 0,1 rejected in U1; 2 selected in U2: one group. 3,4 disagree inside one request only: not asked. 5 has another look.
        string?[] looks = ["a", "a", "a", "b", "b", "c"];
        string?[] decisions = ["NOT_SELECT", "NOT_SELECT", "SELECT", "SELECT", "NOT_SELECT", "SELECT"];
        string?[] units = ["U1", "U1", "U2", "U3", "U3", "U3"];
        var groups = ShapeReview.Groups(looks, decisions, units);
        Assert.Equal([[0, 1, 2]], groups);
    }

    [Fact]
    public void A_look_shared_by_too_many_lines_is_left_as_decided()
    {
        var n = ShapeReview.MaxMembers + 1;
        var groups = ShapeReview.Groups(Enumerable.Repeat<string?>("a", n).ToArray(),
            Enumerable.Range(0, n).Select(i => (string?)(i == 0 ? "SELECT" : "NOT_SELECT")).ToArray(),
            Enumerable.Range(0, n).Select(i => (string?)("U" + i % 3)).ToArray());
        Assert.Empty(groups);
    }

    [Fact]
    public void The_reply_must_carry_the_shape_protocol_and_every_member()
    {
        string[] members = ["A", "B"];
        Assert.NotNull(AgentSiblingReview.Parse("{\"protocolVersion\":\"" + ShapeReview.Version + "\",\"decisions\":[{\"alias\":\"A\",\"decision\":\"SELECT\"},{\"alias\":\"B\",\"decision\":\"SELECT\"}]}", members, out _, ShapeReview.Version));
        Assert.Null(AgentSiblingReview.Parse("{\"protocolVersion\":\"" + AgentSiblingReview.Version + "\",\"decisions\":[{\"alias\":\"A\",\"decision\":\"SELECT\"},{\"alias\":\"B\",\"decision\":\"SELECT\"}]}", members, out var failure, ShapeReview.Version));
        Assert.Equal("PROTOCOL_VERSION_INVALID", failure);
    }
}
