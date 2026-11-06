using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Hierarchy;
using DocxHeaderExtractor.DocumentProcessing.Placement;
using DocxHeaderExtractor.HeadingAgent.Placement;

namespace DocxHeaderExtractor.Tests;

/// <summary>The outline-depth placer: the model writes one depth per row; C# turns depths into parents and checks only the shape.
/// Synthetic lines and scripted replies, not report text or provider output.</summary>
public sealed class OutlinePlacerTests
{
    // II. plan / A. / B. / ● item (body line below) / ● item / C.
    static PlacementRequest Request()
    {
        var rows = new (string Alias, string Text, string? Label, bool Heading)[]
        {
            ("L1", "KẾ HOẠCH", "II.", true), ("L2", "Truyền thông:", "A.", true), ("L3", "Công tác xã hội:", "B.", true),
            ("L4", "Hoàn thiện quy trình", "●", true), ("L5", "- Xây dựng dự thảo", "-", false), ("L6", "Duy trì chất lượng", "●", true),
            ("L7", "Tổng đài:", "C.", true),
        };
        var lines = rows.Select((r, i) => new SemanticSourceAtom(r.Alias, "id:" + r.Alias, i, 1, i, 0, r.Text)).ToArray();
        var bound = lines.Where(l => rows.Single(r => r.Alias == l.Alias).Heading).Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId,
            l.Ordinal, l.Text, "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var styles = rows.Where(r => r.Label is not null).ToDictionary(r => r.Alias,
            r => $"{{\"name\":\"List Paragraph\",\"bold\":{(r.Heading ? "true" : "false")},\"numberLabel\":\"{r.Label}\",\"numberLevel\":0}}");
        var states = lines.ToDictionary(l => l.Alias, _ => new HeadingAliasState("SELECT", "SELECT", "HEADING", "READY_FOR_BINDING"));
        return new(bound, new HeadingPlacementEvidence("{}", styles) { Source = new(lines, states, new string('c', 64)) });
    }

    static string Reply(string depths) => $"{{\"protocolVersion\":\"{OutlinePlacer.Version}\",\"depths\":[{depths}]}}";

    static OutlinePlacer Scripted(List<JsonNode> packets, params string[] replies)
    {
        var turn = 0;
        return new OutlinePlacer(request =>
        {
            packets.Add(JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!);
            return new(replies[Math.Min(turn++, replies.Length - 1)], [], "stop");
        });
    }

    [Fact]
    public void The_model_sees_each_heading_as_an_outline_row_with_marker_weight_and_the_line_after_it()
    {
        var rows = OutlinePlacer.Rows(Request());
        Assert.Equal(["II.", "A.", "B.", "●", "●", "C."], rows.Select(r => r.Marker));
        Assert.All(rows, r => Assert.True(r.Bold));
        Assert.Equal("- Xây dựng dự thảo", rows.Single(r => r.Alias == "L4").After);
        // The line after a heading is never the next heading itself.
        Assert.Equal("", rows.Single(r => r.Alias == "L2").After);
    }

    [Fact]
    public async Task Depths_become_parents_and_the_two_bullets_share_their_section()
    {
        var packets = new List<JsonNode>();
        var result = await ParentResolver.PlaceAsync(Request().Ordered, Scripted(packets, Reply("1,2,2,3,3,2")), PlacementScript.Access,
            Request().Evidence, CancellationToken.None);
        var tree = HierarchyResolver.DeriveHierarchyFromModelRelations(result.Headings).ToDictionary(h => h.SourceId[3..]);
        Assert.Null(tree["L1"].ParentSourceId);
        Assert.Equal("id:L1", tree["L3"].ParentSourceId);
        Assert.Equal("id:L3", tree["L4"].ParentSourceId);
        Assert.Equal("id:L3", tree["L6"].ParentSourceId);
        Assert.Equal("id:L1", tree["L7"].ParentSourceId);
        Assert.Equal(3, tree["L6"].Level);
        Assert.Equal(OutlinePlacer.Version, result.Observation.Contract);
        var packet = Assert.Single(packets);
        Assert.Equal("HIERARCHY_OUTLINE", packet["stage"]!.GetValue<string>());
        Assert.Equal(6, packet["rows"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_depth_that_jumps_two_levels_is_refused_and_the_row_is_named_in_the_next_round()
    {
        var packets = new List<JsonNode>();
        var proposal = await Scripted(packets, Reply("1,3,2,3,3,2"), Reply("1,2,2,3,3,2"))
            .ProposeAsync(Request(), PlacementScript.Access, CancellationToken.None);
        Assert.Empty(proposal.Failures);
        Assert.Equal(2, packets.Count);
        Assert.Contains("DEPTH_JUMP", packets[1]["protocolErrors"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.StartsWith("row 2 has depth 3", packets[1]["protocolDetails"]![0]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_title_outside_the_outline_is_NONE_and_is_skipped_when_depths_are_compared()
    {
        var proposal = await Scripted([], Reply("\"NONE\",1,1,2,2,1")).ProposeAsync(Request(), PlacementScript.Access, CancellationToken.None);
        var parents = proposal.Parents.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("NONE", parents["L1"]);
        Assert.Equal("ROOT", parents["L2"]);
        Assert.Equal("L3", parents["L6"]);
    }

    [Theory]
    [InlineData("1,2,2,3,3")]          // one row short
    [InlineData("1,2,2,3,3,\"x\"")]    // not a depth
    [InlineData("2,2,2,3,3,2")]        // the first row cannot be depth 2
    public async Task A_reply_that_never_fits_the_shape_leaves_every_heading_without_a_proposal(string depths)
    {
        var proposal = await Scripted([], Reply(depths)).ProposeAsync(Request(), PlacementScript.Access, CancellationToken.None);
        Assert.Empty(proposal.Parents);
        Assert.Contains(proposal.Failures, f => f.EndsWith("NO_VALID_OUTLINE", StringComparison.Ordinal));
    }
}
