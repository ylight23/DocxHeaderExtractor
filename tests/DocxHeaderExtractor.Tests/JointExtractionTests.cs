using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Protocol;

namespace DocxHeaderExtractor.Tests;

public sealed class JointExtractionTests
{
    static readonly Dictionary<string, int> Positions = new() { ["A"] = 0, ["B"] = 1, ["C"] = 2, ["D"] = 3 };
    static JsonElement Groups(string json) => JsonDocument.Parse("{\"selectionGroups\":" + json + "}").RootElement.Clone();

    [Theory]
    [InlineData("{\"groups\":[[\"A\",\"B\"]],\"unresolvedAliases\":[\"C\"]}", null)]
    [InlineData("{\"groups\":[[\"A\"]],\"unresolvedAliases\":[]}", "INLINE_GROUP_COVERAGE")]
    [InlineData("{\"groups\":[[\"A\",\"A\"]],\"unresolvedAliases\":[\"B\",\"C\"]}", "INLINE_GROUP_MEMBERSHIP")]
    [InlineData("{\"groups\":[[\"A\",\"D\"]],\"unresolvedAliases\":[\"B\",\"C\"]}", "INLINE_GROUP_MEMBERSHIP")]
    [InlineData("{\"groups\":[[\"A\",\"C\"]],\"unresolvedAliases\":[\"B\"]}", "INLINE_GROUP_ORDER")]
    [InlineData("{\"groups\":[[\"B\"],[\"A\"]],\"unresolvedAliases\":[\"C\"]}", "INLINE_GROUP_ORDER")]
    [InlineData("{\"groups\":[[]],\"unresolvedAliases\":[\"A\",\"B\",\"C\"]}", "INLINE_GROUP_SIZE")]
    public void Inline_partitions_enforce_membership_order_and_coverage(string json, string? failure)
    {
        var decisions = new Dictionary<string, string> { ["A"] = "SELECT", ["B"] = "SELECT", ["C"] = "SELECT", ["D"] = "NOT_SELECT" };
        var result = InlineGrouping.Parse(Groups(json), decisions, Positions);
        Assert.Equal(failure, result.Failure);
        if (failure is not null) Assert.Null(result.Groups);
    }

    [Fact]
    public void Invalid_boundary_proposal_does_not_rewrite_a_valid_membership_decision()
    {
        var json = JsonSerializer.Serialize(new { protocolVersion = InlineGrouping.Version, status = "COMPLETE",
            decisions = new[] { new { alias = "A", decision = "SELECT", evidenceRefs = new[] { "A" } } },
            selectionGroups = new { groups = new[] { new[] { "UNKNOWN" } }, unresolvedAliases = Array.Empty<string>() } });
        var parsed = AgentProtocol.ParseSelect(json, Positions, ["A"], InlineGrouping.Version,
            new Dictionary<string, string> { ["A"] = "Title" });
        Assert.True(parsed.Ok, parsed.Failure);
        Assert.Equal("SELECT", parsed.Values["A"]);
        Assert.Equal("INLINE_GROUP_MEMBERSHIP", parsed.GroupingFailure);
        Assert.Null(parsed.SelectionGroups);
        Assert.False(AgentProtocol.ParseSelect(json, Positions, ["A"]).Ok);
    }

    sealed class Source(int count = 4) : IAgentSource
    {
        public string SourceSha256 => new('a', 64);
        public string SourceAliasUniverseSha256 => new('b', 64);
        public string EvidenceStoreSha256 => new('c', 64);
        public IReadOnlyList<AgentSourceLine> Lines { get; } = Enumerable.Range(0, count).Select(i =>
            new AgentSourceLine(((char)('A' + i)).ToString(), i, 1, ((char)('A' + i)).ToString(), null, null)).ToArray();
        public IReadOnlyList<(int Start, int End)> Cores => [(0, Lines.Count)];
        public (string[] Decide, int[] Shown) Scope(int segment) => (Lines.Select(l => l.Alias).ToArray(), Enumerable.Range(0, Lines.Count).ToArray());
        public IAgentFormat Flavor => PdfFormat.WithInlineGrouping;
    }

    [Theory]
    [InlineData("local", 0)]
    [InlineData("cross-scope", 1)]
    [InlineData("deferred", 1)]
    [InlineData("missing-neighbor", 1)]
    public void Only_complete_local_runs_bypass_the_grouping_request(string mode, int expectedCalls)
    {
        var calls = 0;
        var owners = Positions.Keys.ToDictionary(a => a, _ => "SEG-000");
        if (mode == "cross-scope") owners["B"] = "SEG-001";
        if (mode == "missing-neighbor") owners.Remove("C");
        var proposals = mode == "deferred" ? new[] { (IReadOnlyList<string>)new[] { "A" } } : new[] { (IReadOnlyList<string>)new[] { "A", "B" } };
        ModelTurn Model(string unit, int turn, PreparedRequest request)
        {
            calls++;
            return new("{\"protocolVersion\":\"PDF_HEADING_GROUPS_V1\",\"runs\":[{\"run\":\"R1\",\"headings\":[[\"A\",\"B\"]]}]}", [], "stop");
        }
        var result = AgentGrouping.Group(new Source(), new HashSet<string> { "A", "B" }, Model, new AgentBudget(10, 1),
            1, CancellationToken.None, retryDelayMs: 0, inlineGroups: proposals, ownership: owners);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(new[] { "A", "B" }, Assert.Single(result.Groups));
        Assert.Empty(result.Failures);
        Assert.All(result.Provenance, p => Assert.Equal(expectedCalls == 0 ? "INLINE_SELECTION" : "GROUP-001", p.UnitId));
    }

    [Fact]
    public void Mixed_inline_and_deferred_runs_keep_exact_coverage_and_correct_provenance()
    {
        var source = new Source(6);
        var result = AgentGrouping.Group(source, new HashSet<string> { "A", "B", "D", "E" }, (_, _, request) =>
        {
            var packet = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.Equal("R2", Assert.Single(packet["runs"]!.AsArray())!["run"]!.GetValue<string>());
            return new("{\"protocolVersion\":\"PDF_HEADING_GROUPS_V1\",\"runs\":[{\"run\":\"R2\",\"headings\":[[\"D\",\"E\"]]}]}", [], "stop");
        }, new AgentBudget(10, 1), 1, CancellationToken.None, retryDelayMs: 0,
            inlineGroups: [new[] { "A", "B" }], ownership: source.Lines.ToDictionary(l => l.Alias, _ => "SEG-000"));
        Assert.Empty(result.Failures);
        Assert.Equal(1, result.Posts);
        Assert.Equal(new[] { "A", "B", "D", "E" }, result.Groups.SelectMany(g => g));
        Assert.All(result.Provenance.Where(p => p.Alias is "A" or "B"), p => Assert.Equal("INLINE_SELECTION", p.UnitId));
        Assert.All(result.Provenance.Where(p => p.Alias is "D" or "E"), p => Assert.Equal("GROUP-001", p.UnitId));
    }

    [Fact]
    public async Task Whole_document_placement_measures_all_context_and_targets_once()
    {
        var (bound, evidence, parents) = HeadingPlacerTests.Fixture();
        var packets = new List<JsonNode>();
        var placer = new HeadingPlacer(request =>
        {
            var packet = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            packets.Add(packet);
            Assert.Equal(HeadingPlacer.OutputBudget(bound.Length), JsonNode.Parse(request.Body)!["max_tokens"]!.GetValue<int>());
            return new(HierarchyTestProtocol.Reply(packet.ToJsonString(), parents), [], "stop");
        }, wholeDocument: true);
        var proposal = await placer.ProposeAsync(new(bound, evidence), PlacementScript.Access, CancellationToken.None);
        Assert.Single(packets);
        Assert.Equal(bound.Length, packets[0]["toPlace"]!.AsArray().Count);
        Assert.Equal(bound.Length, proposal.Parents.Count);
        Assert.Empty(proposal.Failures);
        Assert.Equal(evidence.Source!.Lines.Count, packets[0]["sourceContext"]!.AsArray().Count);
    }

    [Fact]
    public async Task Whole_document_repair_keeps_accepted_rows_and_asks_only_for_invalid_relations()
    {
        var (bound, evidence, parents) = HeadingPlacerTests.Fixture();
        var packets = new List<JsonNode>();
        var placer = new HeadingPlacer(request =>
        {
            var packet = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            packets.Add(packet);
            var response = JsonNode.Parse(HierarchyTestProtocol.Reply(packet.ToJsonString(), parents)!)!;
            if (packets.Count == 1) response["placements"]!.AsArray()[0]!["parent"] = bound[^1].Alias;
            return new(response.ToJsonString(), [], "stop");
        }, wholeDocument: true);
        var proposal = await placer.ProposeAsync(new(bound, evidence), PlacementScript.Access, CancellationToken.None);
        Assert.Equal(2, packets.Count);
        Assert.Single(packets[1]["toPlace"]!.AsArray());
        Assert.Equal(bound.Length - 1, packets[1]["acceptedRelations"]!.AsArray().Count);
        Assert.Equal(bound.Length, proposal.Parents.Count);
        Assert.Empty(proposal.Failures);
    }

    [Fact]
    public async Task An_unsplittable_whole_document_packet_is_refused_without_discarding_evidence()
    {
        var (bound, evidence, _) = HeadingPlacerTests.Fixture();
        var oversized = evidence with { DocumentStylesJson = JsonSerializer.Serialize(new { raw = new string('x', 100000) }) };
        var calls = 0;
        var placer = new HeadingPlacer(_ => { calls++; return new("{}", [], "stop"); }, wholeDocument: true);
        var proposal = await placer.ProposeAsync(new(bound, oversized), PlacementScript.Access, CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Empty(proposal.Parents);
        Assert.NotEmpty(proposal.Failures);
    }
}
