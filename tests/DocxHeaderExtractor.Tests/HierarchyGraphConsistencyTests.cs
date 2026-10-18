using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

public sealed class HierarchyGraphConsistencyTests
{
    [Theory]
    [InlineData("parent-root")]
    [InlineData("child-root")]
    public async Task Conflicting_accepted_edges_get_one_targeted_source_based_repair_and_independent_reverification(string correction)
    {
        var parents = new Dictionary<string, string> { ["A"] = "NONE", ["B"] = "A", ["C"] = "B", ["D"] = "ROOT" };
        var repaired = new Dictionary<string, string>(parents) { [correction == "parent-root" ? "A" : "B"] = "ROOT" };
        using var model = new Model(p => HierarchyTestProtocol.Reply(p.ToJsonString(),
            Stage(p) == "HIERARCHY_GRAPH_REPAIR" ? repaired : parents)!);
        var result = await Run(parents.Keys.ToArray(), model);
        Assert.Equal(new[] { "A", "B" }, result.Observation.GraphRepairAliases);
        Assert.Empty(result.Observation.GraphUnresolvedAliases);
        Assert.Equal(0, result.Observation.UnresolvedCount);
        var conflict = Assert.Single(result.Observation.GraphConflicts, c => c.Checkpoint == "after-initial-verification");
        Assert.Equal("OUTSIDE_PARENT_CONFLICT", conflict.Code);
        Assert.Equal("A", conflict.ParentAlias);
        Assert.Equal(new[] { "B" }, conflict.Children);
        Assert.Equal(new[] { "A", "B" }, Targets(Assert.Single(model.Packets, p => Stage(p) == "HIERARCHY_GRAPH_REPAIR")));
        Assert.Equal(new[] { "A", "B", "C" }, Targets(Assert.Single(model.Packets, p => Stage(p) == "HIERARCHY_GRAPH_REVERIFY")));
        Assert.All(result.Observation.Decisions, d => Assert.Equal(repaired[d.Alias], d.ProposedParentAlias));
        Assert.Equal(correction == "parent-root" ? 4 : 3, result.Observation.PlacedCount);
        Assert.Equal(correction == "parent-root" ? 0 : 1, result.Observation.OutOfHierarchyCount);
    }

    [Fact]
    public async Task Conflict_components_crossing_batches_are_repaired_as_a_whole()
    {
        var parents = new Dictionary<string, string> { ["A"] = "NONE" };
        foreach (var i in Enumerable.Range(0, 18)) parents["child-" + i] = "A";
        parents["independent"] = "ROOT";
        var fixedParents = new Dictionary<string, string>(parents) { ["A"] = "ROOT" };
        using var model = new Model(p => HierarchyTestProtocol.Reply(p.ToJsonString(),
            Stage(p) == "HIERARCHY_GRAPH_REPAIR" ? fixedParents : parents)!);
        var result = await Run(parents.Keys.ToArray(), model);
        Assert.Equal(19, result.Observation.GraphRepairAliases.Count);
        Assert.DoesNotContain("independent", result.Observation.GraphRepairAliases);
        Assert.Equal(2, model.Packets.Count(p => Stage(p) == "HIERARCHY_GRAPH_REPAIR"));
        Assert.Equal(2, model.Packets.Count(p => Stage(p) == "HIERARCHY_GRAPH_REVERIFY"));
        Assert.Equal(20, result.Observation.PlacedCount);
        Assert.Empty(result.Observation.GraphUnresolvedAliases);
    }

    [Fact]
    public async Task An_unresolved_conflict_does_not_quarantine_an_independent_repaired_component()
    {
        var parents = new Dictionary<string, string> { ["A"] = "NONE", ["B"] = "A", ["C"] = "NONE", ["D"] = "C", ["E"] = "ROOT" };
        var fixedParents = new Dictionary<string, string>(parents) { ["A"] = "ROOT" };
        using var model = new Model(p => HierarchyTestProtocol.Reply(p.ToJsonString(),
            Stage(p) == "HIERARCHY_GRAPH_REPAIR" ? fixedParents : parents)!);
        var result = await Run(parents.Keys.ToArray(), model);
        Assert.Equal(new[] { "C", "D" }, result.Observation.GraphUnresolvedAliases);
        Assert.Equal(3, result.Observation.PlacedCount);
        Assert.Equal(2, result.Observation.UnresolvedCount);
        Assert.Equal(0, result.Observation.OutOfHierarchyCount);
    }

    [Fact]
    public async Task Outside_node_and_independent_root_do_not_trigger_graph_repair()
    {
        var parents = new Dictionary<string, string> { ["A"] = "NONE", ["B"] = "ROOT" };
        using var model = new Model(p => HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!);
        var result = await Run(parents.Keys.ToArray(), model);
        Assert.Empty(result.Observation.GraphConflicts);
        Assert.Empty(result.Observation.GraphRepairAliases);
        Assert.Equal(2, result.Observation.LogicalCalls);
        Assert.Equal(1, result.Observation.PlacedCount);
        Assert.Equal(1, result.Observation.OutOfHierarchyCount);
    }

    [Theory]
    [InlineData("still-conflicting")]
    [InlineData("malformed-repair")]
    [InlineData("unverified-repair")]
    public async Task Repeated_or_unverified_conflict_is_quarantined_without_reusing_old_approvals_or_looping(string mode)
    {
        var parents = new Dictionary<string, string> { ["A"] = "NONE", ["B"] = "A", ["C"] = "B", ["D"] = "ROOT" };
        using var model = new Model(p => mode == "malformed-repair" && Stage(p) == "HIERARCHY_GRAPH_REPAIR"
            ? "{" : HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!)
        { Finish = p => mode == "unverified-repair" && Stage(p) == "HIERARCHY_GRAPH_REVERIFY" ? "length" : "stop" };
        var result = await Run(parents.Keys.ToArray(), model);
        Assert.Equal(new[] { "A", "B" }, result.Observation.GraphUnresolvedAliases);
        Assert.Equal(3, result.Observation.UnresolvedCount);
        Assert.Equal(1, result.Observation.PlacedCount);
        Assert.Equal(0, result.Observation.OutOfHierarchyCount);
        Assert.All(result.Headings.Where(h => h.Alias is "A" or "B"), h => Assert.Empty(h.RelationHints));
        Assert.All(result.Observation.Decisions.Where(d => d.Alias is "A" or "B"),
            d => Assert.Equal("graph-conflict-unresolved", d.ResolutionBasis));
        Assert.InRange(model.Packets.Count(p => Stage(p) == "HIERARCHY_GRAPH_REPAIR"), 1, EvidenceHeadingPlacement.MaxRounds);
        Assert.True(result.Observation.LogicalCalls <= 8);
    }

    [Fact]
    public async Task Graph_gate_runs_after_existing_edge_repair_too()
    {
        var parents = new Dictionary<string, string> { ["A"] = "NONE", ["B"] = "A" };
        using var model = new Model(p =>
        {
            var reply = JsonNode.Parse(HierarchyTestProtocol.Reply(p.ToJsonString(),
                Stage(p) == "HIERARCHY_GRAPH_REPAIR" ? new Dictionary<string, string> { ["A"] = "ROOT", ["B"] = "A" } : parents)!)!;
            if (Stage(p) == "HIERARCHY_VERIFY")
                reply["verdicts"]!.AsArray().Single(v => v!["alias"]!.GetValue<string>() == "A")!["decision"] = "REJECT";
            return reply.ToJsonString();
        });
        var result = await Run(parents.Keys.ToArray(), model);
        Assert.DoesNotContain(result.Observation.GraphConflicts, c => c.Checkpoint == "after-initial-verification");
        Assert.Contains(result.Observation.GraphConflicts, c => c.Checkpoint == "after-edge-repair");
        Assert.Equal(new[] { "A", "B" }, result.Observation.GraphRepairAliases);
        Assert.Equal(0, result.Observation.UnresolvedCount);
    }

    static Task<HeadingParentResolver.PlacementResult> Run(string[] aliases, Model model)
    {
        var lines = aliases.Select((a, i) => new SemanticSourceAtom(a, "source-" + a, i, 1, i, 0, "Section " + a)).ToArray();
        var bound = lines.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text,
            "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var evidence = new HeadingPlacementEvidence("{}", new Dictionary<string, string>())
        { Source = new(lines, lines.ToDictionary(l => l.Alias, _ => new HeadingAliasState("SELECT", "SELECT", "HEADING", "READY_FOR_BINDING"))) };
        return HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
    }

    static string Stage(JsonNode p) => p["stage"]!.GetValue<string>();
    static string[] Targets(JsonNode p) => p["toPlace"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();

    sealed class Model(Func<JsonNode, string> script) : IStructuredInferenceTransport
    {
        public ConcurrentQueue<JsonNode> Packets { get; } = new();
        public Func<JsonNode, string> Finish { get; init; } = _ => "stop";
        public string ModelName => "scripted";
        public int ContextSize => 32768;
        public int SharedPrefixTokens => 0;
        public string RuntimeDescription => "offline graph regression";
        public Task<StructuredInferenceReply> CompleteStructuredAsync(string systemPrompt, string userMessage,
            int maxOutputTokens, CancellationToken ct = default)
        {
            var packet = JsonNode.Parse(userMessage)!;
            Packets.Enqueue(packet);
            return Task.FromResult(new StructuredInferenceReply(script(packet), Finish(packet), maxOutputTokens));
        }
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
            => throw new InvalidOperationException("Structured transport required in regression.");
        public void Dispose() { }
    }
}
