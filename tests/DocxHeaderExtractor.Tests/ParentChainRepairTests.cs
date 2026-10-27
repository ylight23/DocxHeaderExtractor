using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Hierarchy;
using DocxHeaderExtractor.DocumentProcessing.Placement;
using DocxHeaderExtractor.HeadingAgent.Placement;

namespace DocxHeaderExtractor.Tests;

public sealed class ParentChainRepairTests
{
    static (CanonicalSemanticBoundHeading[] Bound, HeadingPlacementEvidence Evidence) Fixture(int count = 5)
    {
        var lines = Enumerable.Range(0, count).Select(i => new SemanticSourceAtom("H" + i, "id:" + i, i, 1, i, 0, "Heading " + i)).ToArray();
        var bound = lines.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text,
            "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var states = lines.ToDictionary(l => l.Alias, _ => new HeadingAliasState("SELECT", "SELECT", null, "READY_FOR_BINDING"));
        return (bound, new("{}", new Dictionary<string, string>()) { Source = new(lines, states, new string('a', 64)) });
    }
    static JsonNode Packet(PreparedRequest r) => JsonNode.Parse(JsonNode.Parse(r.Body)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
    static ModelTurn Reply(JsonNode p, IReadOnlyDictionary<string, string> parents) => new(JsonSerializer.Serialize(new
    {
        protocolVersion = HeadingPlacer.Version,
        placements = p["toPlace"]!.AsArray().Select(a => new { alias = a!.GetValue<string>(), parent = parents[a.GetValue<string>()] })
    }), [], "stop");

    [Theory]
    [InlineData("NONE", "ANCESTOR_OUTSIDE_TREE")]
    [InlineData("UNRESOLVED", "ANCESTOR_UNRESOLVED")]
    public async Task Repair_includes_the_blocker_and_descendants_and_keeps_unrelated_relations(string terminal, string code)
    {
        var (bound, evidence) = Fixture();
        var parents = new Dictionary<string, string> { ["H0"] = "ROOT", ["H1"] = terminal, ["H2"] = "H1", ["H3"] = "H2", ["H4"] = "H0" };
        var packets = new List<JsonNode>();
        var placer = new HeadingPlacer(r =>
        {
            var p = Packet(r); packets.Add(p);
            if (p["stage"]!.GetValue<string>() == ParentChainRepair.Phase)
            {
                Assert.Equal(new[] { "H1", "H2", "H3" }, p["toPlace"]!.AsArray().Select(a => a!.GetValue<string>()));
                Assert.Equal(new[] { "H0", "H4" }, p["fixedRelations"]!.AsArray().Select(a => a!["alias"]!.GetValue<string>()));
                Assert.Contains(code, p["protocolDetails"]!.ToJsonString());
                parents["H1"] = "ROOT";
            }
            return Reply(p, parents);
        }, repairParentChains: true);
        var result = await ParentResolver.PlaceAsync(bound, placer, PlacementScript.Access, evidence, CancellationToken.None);
        Assert.Equal(2, packets.Count);
        Assert.Equal(0, result.Observation.UnresolvedCount);
        Assert.Equal(1, result.Observation.RepairCount);
        var repair = Assert.IsType<PlacementChainRepairObservation>(result.Observation.ChainRepair);
        Assert.Equal(2, repair.InitialConflicts.Count);
        Assert.Empty(repair.FinalConflicts);
        Assert.Equal(new[] { "H1" }, repair.ChangedAliases);
        Assert.Equal(bound.Select(h => (h.Alias, h.Text)), result.Headings.Select(h => (h.Alias, h.Text)));
    }

    [Fact]
    public async Task A_failed_or_unchanged_repair_preserves_proposals_and_stops_without_guessing_a_root()
    {
        var (bound, evidence) = Fixture();
        var parents = new Dictionary<string, string> { ["H0"] = "NONE", ["H1"] = "H0", ["H2"] = "H1", ["H3"] = "UNRESOLVED", ["H4"] = "ROOT" };
        var packets = new List<JsonNode>();
        var placer = new HeadingPlacer(r => { var p = Packet(r); packets.Add(p); return Reply(p, parents); }, repairParentChains: true);
        var result = await ParentResolver.PlaceAsync(bound, placer, PlacementScript.Access, evidence, CancellationToken.None);
        Assert.Equal(2, packets.Count);
        Assert.Equal(3, result.Observation.UnresolvedCount);
        Assert.Equal("NONE", result.Observation.Decisions.Single(d => d.Alias == "H0").ProposedParentAlias);
        Assert.Equal(2, result.Observation.ChainRepair!.FinalConflicts.Count);
        Assert.Equal(new[] { "H0", "H1", "H2" }, result.Observation.ChainRepair.RequestedAliases);
        Assert.DoesNotContain("H3", result.Observation.ChainRepair.RequestedAliases);
    }

    [Fact]
    public async Task Repair_does_not_rewrite_upstream_relations_and_model_can_choose_uncertainty()
    {
        var (bound, evidence) = Fixture(3);
        bound[0] = bound[0] with { RelationHints = ["parent-node:NONE"] };
        var parents = new Dictionary<string, string> { ["H0"] = "NONE", ["H1"] = "H0", ["H2"] = "H1" };
        var placer = new HeadingPlacer(r =>
        {
            var p = Packet(r);
            if (p["stage"]!.GetValue<string>() == ParentChainRepair.Phase)
            {
                Assert.DoesNotContain(p["toPlace"]!.AsArray(), a => a!.GetValue<string>() == "H0");
                parents["H1"] = parents["H2"] = "UNRESOLVED";
            }
            return Reply(p, parents);
        }, repairParentChains: true);
        var result = await ParentResolver.PlaceAsync(bound, placer, PlacementScript.Access, evidence, CancellationToken.None);
        Assert.Equal(2, result.Observation.UnresolvedCount);
        Assert.Empty(result.Observation.ChainRepair!.FinalConflicts);
        Assert.Equal("NONE", result.Observation.Decisions.Single(d => d.Alias == "H0").ProposedParentAlias);
        Assert.Equal("UPSTREAM_PROPOSAL", (await placer.ProposeAsync(new(bound, evidence), PlacementScript.Access, CancellationToken.None)).Basis["H0"]);
    }

    [Fact]
    public async Task Default_and_already_consistent_graphs_make_no_chain_repair_calls()
    {
        var (bound, evidence) = Fixture(3);
        var wrong = new Dictionary<string, string> { ["H0"] = "NONE", ["H1"] = "H0", ["H2"] = "ROOT" };
        var legacy = new HeadingPlacer(r => Reply(Packet(r), wrong));
        var a = await legacy.ProposeAsync(new(bound, evidence), PlacementScript.Access, CancellationToken.None);
        Assert.Single(a.Calls); Assert.Null(a.ChainRepair);
        var right = new Dictionary<string, string> { ["H0"] = "ROOT", ["H1"] = "H0", ["H2"] = "UNRESOLVED" };
        var candidate = new HeadingPlacer(r => Reply(Packet(r), right), repairParentChains: true);
        var b = await candidate.ProposeAsync(new(bound, evidence), PlacementScript.Access, CancellationToken.None);
        Assert.Single(b.Calls); Assert.Empty(b.ChainRepair!.InitialConflicts); Assert.Equal(0, b.ChainRepair.Passes);
        Assert.Equal(HierarchyResolver.Unresolved, HierarchyResolver.DeriveHierarchyFromModelRelations(
            [bound[2] with { RelationHints = ["parent-node:UNRESOLVED"] }]).Single().Resolution);
    }

    [Fact]
    public async Task Repair_protocol_failure_is_bounded_and_does_not_discard_original_proposals()
    {
        var (bound, evidence) = Fixture(3);
        var parents = new Dictionary<string, string> { ["H0"] = "NONE", ["H1"] = "H0", ["H2"] = "H1" };
        var placer = new HeadingPlacer(r =>
        {
            var p = Packet(r);
            return p["stage"]!.GetValue<string>() == ParentChainRepair.Phase ? new("{}", [], "stop") : Reply(p, parents);
        }, repairParentChains: true);
        var result = await ParentResolver.PlaceAsync(bound, placer, PlacementScript.Access, evidence, CancellationToken.None);
        Assert.Equal(2, result.Observation.UnresolvedCount);
        Assert.Equal(2, result.Observation.RepairCount); // identical refused response stops before a third round
        Assert.Equal(1, result.Observation.ChainRepair!.Passes);
        Assert.Equal("H0", result.Observation.Decisions.Single(d => d.Alias == "H1").ProposedParentAlias);
        Assert.NotEmpty(result.Observation.Failures);
    }
}
