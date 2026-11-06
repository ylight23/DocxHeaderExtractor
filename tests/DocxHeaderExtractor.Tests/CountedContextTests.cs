using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Placement;
using DocxHeaderExtractor.HeadingAgent.Placement;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The optional placement context: each heading to place is shown the nearest earlier counted-marker heading (a fact of position). Off by
/// default, so the production packet and contract do not change. Synthetic lines, not report text.
/// </summary>
public sealed class CountedContextTests
{
    const string Plan = "P1-L1", Section = "P1-L2", Bullet = "P1-L9";

    // Plan (no marker), "B. Công tác xã hội" (counted), five body lines, then a bullet item six lines below the counted heading.
    static (CanonicalSemanticBoundHeading[] Bound, HeadingPlacementEvidence Evidence) Fixture()
    {
        var texts = new[] { ("P1-L1", "Kế hoạch"), ("P1-L2", "B. Công tác xã hội:"), ("P1-L3", "dòng 1"), ("P1-L4", "dòng 2"), ("P1-L5", "dòng 3"),
            ("P1-L6", "dòng 4"), ("P1-L7", "dòng 5"), ("P1-L8", "dòng 6"), ("P1-L9", "Duy trì chất lượng") };
        var lines = texts.Select((t, i) => new SemanticSourceAtom(t.Item1, "id:" + t.Item1, i, 1, i, 0, t.Item2)).ToArray();
        var headings = new[] { Plan, Section, Bullet };
        var bound = lines.Where(l => headings.Contains(l.Alias)).Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text,
            "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        const string bulletStyle = "{\"name\":\"List Paragraph\",\"bold\":true,\"numberLabel\":\"\\uF0B7\",\"numberLevel\":0}";
        const string letterStyle = "{\"name\":\"List Paragraph\",\"bold\":true,\"numberLabel\":\"B.\",\"numberLevel\":0}";
        var styles = new Dictionary<string, string> { [Section] = letterStyle, [Bullet] = bulletStyle };
        var states = lines.ToDictionary(l => l.Alias, _ => new HeadingAliasState("SELECT", "SELECT", "HEADING", "READY_FOR_BINDING"));
        return (bound, new HeadingPlacementEvidence("{}", styles) { Source = new(lines, states, new string('b', 64)) });
    }

    static HeadingPlacer Placer(bool countedContext, List<string> packets) => new(request =>
    {
        var body = JsonNode.Parse(request.Body)!;
        var packet = body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>();
        packets.Add(packet);
        return new(HierarchyTestProtocol.Reply(packet, new Dictionary<string, string> { [Plan] = "ROOT", [Section] = "ROOT", [Bullet] = Section })!, [], "stop");
    }, countedContext: countedContext);

    [Fact]
    public async Task With_the_option_off_the_packet_and_the_contract_are_unchanged()
    {
        var (bound, evidence) = Fixture();
        var packets = new List<string>();
        var result = await ParentResolver.PlaceAsync(bound, Placer(false, packets), PlacementScript.Access, evidence, CancellationToken.None);
        Assert.All(packets, p => Assert.DoesNotContain("nearestCountedHeading", p, StringComparison.Ordinal));
        Assert.DoesNotContain(":counted-context-v1", result.Observation.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_option_on_a_bullet_far_below_a_counted_heading_is_shown_that_heading()
    {
        var (bound, evidence) = Fixture();
        var packets = new List<string>();
        var result = await ParentResolver.PlaceAsync(bound, Placer(true, packets), PlacementScript.Access, evidence, CancellationToken.None);
        Assert.Contains(":counted-context-v1", result.Observation.Contract, StringComparison.Ordinal);
        var packet = JsonNode.Parse(packets[0])!;
        var near = packet["nearestCountedHeading"]!.AsObject();
        // The bullet (P1-L9) is outside the ±4-line source window of the counted heading (P1-L2); the context still names it.
        Assert.Equal(Section, near[Bullet]!["alias"]!.GetValue<string>());
        Assert.Equal("B.", near[Bullet]!["marker"]!.GetValue<string>());
        Assert.Equal(7, near[Bullet]!["linesBack"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_heading_with_no_counted_marker_above_it_gets_no_entry()
    {
        var (bound, evidence) = Fixture();
        var packets = new List<string>();
        await ParentResolver.PlaceAsync(bound, Placer(true, packets), PlacementScript.Access, evidence, CancellationToken.None);
        var near = JsonNode.Parse(packets[0])!["nearestCountedHeading"]!.AsObject();
        // Section's earlier heading (Plan) has no marker, so Section has no entry; bullets are never counted markers.
        Assert.False(near.ContainsKey(Section));
        Assert.False(near.ContainsKey(Plan));
    }
}
