using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfPairwiseGroupingTests
{
    static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"), out _, sourceBound: true)!);

    static JsonObject Packet(PreparedRequest request) => JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    static JsonObject Reply(JsonObject packet) => new()
    {
        ["protocolVersion"] = PdfBoundaryDecisions.Version,
        ["runs"] = new JsonArray(packet["runs"]!.AsArray().Select(r => (JsonNode)new JsonObject
        {
            ["run"] = r!["run"]!.GetValue<string>(),
            ["boundaries"] = new JsonArray(r["lines"]!.AsArray().Skip(1).Select((right, i) => (JsonNode)new JsonObject
            {
                ["leftAlias"] = r["lines"]![i]!["alias"]!.GetValue<string>(), ["rightAlias"] = right!["alias"]!.GetValue<string>(),
                ["leftText"] = r["lines"]![i]!["selectedText"]!.GetValue<string>(), ["rightText"] = right["selectedText"]!.GetValue<string>(),
                ["decision"] = i == 0 ? "JOIN" : "SPLIT", ["reason"] = "Fixture model boundary decision."
            }).ToArray())
        }).ToArray())
    };

    [Fact]
    public void Model_pair_decisions_are_folded_exactly_and_respect_selected_spans()
    {
        var source = Source.Value; var aliases = source.Lines.Take(3).Select(l => l.Alias).ToHashSet();
        var extents = new Dictionary<string, TextOffsetSpan> { [source.Lines[0].Alias] = new(1, 10) };
        var measuredBefore = source.LayoutFacts[source.Lines[0].Alias].ToJsonString();
        var result = AgentGrouping.Group(source, aliases, (_, _, request) =>
        {
            var p = Packet(request); var line = p["runs"]![0]!["lines"]![0]!;
            Assert.Equal(source.Lines[0].Text.Substring(1, 9), line["selectedText"]!.GetValue<string>());
            Assert.NotNull(line["measuredFacts"]); Assert.NotNull(p["documentStyles"]);
            Assert.NotNull(p["runs"]![0]!["contextAfter"]);
            Assert.All(p["runs"]![0]!["contextAfter"]!.AsArray(), c =>
            { Assert.Null(c!["selected"]); Assert.Equal("context-only", c["role"]!.GetValue<string>()); });
            return new ModelTurn(Reply(p).ToJsonString(), [], "stop");
        }, AgentSelector.BudgetFor(source), 6, CancellationToken.None, 0, headingExtents: extents, pairwisePdf: true);
        Assert.Empty(result.Failures); Assert.Equal(2, result.Groups.Count);
        Assert.Equal(source.Lines.Take(2).Select(l => l.Alias), result.Groups[0]);
        Assert.Equal(source.Lines[2].Alias, Assert.Single(result.Groups[1]));
        Assert.Equal(measuredBefore, source.LayoutFacts[source.Lines[0].Alias].ToJsonString());
    }

    [Theory]
    [InlineData("alias", "PROTOCOL:BOUNDARY_PAIR_MISMATCH")]
    [InlineData("text", "PROTOCOL:BOUNDARY_SOURCE_TEXT_MISMATCH")]
    [InlineData("coverage", "PROTOCOL:BOUNDARY_COVERAGE")]
    [InlineData("decision", "PROTOCOL:BOUNDARY_DECISION_INVALID")]
    [InlineData("reason", "PROTOCOL:BOUNDARY_REASON_INVALID")]
    [InlineData("duplicate-run", "PROTOCOL:RUN_LISTED_TWICE")]
    [InlineData("size", "PROTOCOL:HEADING_SIZE_INVALID")]
    public void Invalid_pairs_never_create_a_guessed_partition(string change, string expectedFailure)
    {
        var source = Source.Value;
        var chunk = new[] { new AgentGrouping.Run("R1", Enumerable.Range(0, change == "size" ? 9 : 3).ToArray()) };
        JsonObject? payload = null;
        var selected = chunk[0].Lines.Select(i => source.Lines[i].Alias).ToHashSet();
        AgentGrouping.Group(source, selected, (_, _, request) =>
        { payload = Packet(request); return new(Reply(payload).ToJsonString(), [], "stop"); }, AgentSelector.BudgetFor(source),
            1, CancellationToken.None, 0, pairwisePdf: true);
        var reply = Reply(payload!); var boundaries = reply["runs"]![0]!["boundaries"]!.AsArray();
        switch (change)
        {
            case "alias": boundaries[0]!["leftAlias"] = "OTHER"; break;
            case "text": boundaries[0]!["leftText"] = "Wrong source"; break;
            case "coverage": boundaries.RemoveAt(0); break;
            case "decision": boundaries[0]!["decision"] = "MAYBE"; break;
            case "reason": boundaries[0]!["reason"] = ""; break;
            case "duplicate-run": reply["runs"]!.AsArray().Add(reply["runs"]![0]!.DeepClone()); break;
            case "size": foreach (var b in boundaries) b!["decision"] = "JOIN"; break;
        }
        var parsed = PdfBoundaryDecisions.Parse(reply.ToJsonString(), payload!.ToJsonString(), chunk, source, out var failure, out _);
        Assert.Equal(expectedFailure, failure);
        Assert.True(parsed is null || parsed.Count == 0);
    }

    [Fact]
    public void Repair_uses_the_pair_contract_and_exhaustion_reports_fallback_without_accepting_partition()
    {
        var source = Source.Value; var calls = 0;
        var selected = source.Lines.Take(2).Select(l => l.Alias).ToHashSet();
        var result = AgentGrouping.Group(source, selected, (_, turn, request) =>
        {
            calls++;
            if (turn > 1) Assert.Contains("PDF_HEADING_BOUNDARIES_V1", JsonNode.Parse(request.Body)!["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
            var reply = Reply(Packet(request)); reply["runs"]![0]!["boundaries"]![0]!["leftText"] = "wrong";
            return new(reply.ToJsonString(), [], "stop");
        }, AgentSelector.BudgetFor(source), 1, CancellationToken.None, 0, pairwisePdf: true);
        Assert.Equal(1 + AgentGrouping.MaxRepairs, calls);
        Assert.NotEmpty(result.Failures); Assert.Equal(1, result.Observation.FallbackRuns);
        Assert.All(result.Provenance, p => Assert.Equal("FALLBACK", p.Decision));
    }

    [Fact]
    public void Candidate_has_distinct_request_cache_identity_and_preserves_default_selector()
    {
        var source = Source.Value; var chunk = new[] { new AgentGrouping.Run("R1", [0, 1]) };
        Assert.NotEqual(AgentGrouping.BatchKey(source, chunk, 1, PdfBoundaryInstruction.Instruction),
            AgentGrouping.BatchKey(source, chunk, 1, PdfBoundaryInstruction.Instruction, pairwisePdf: true));
        Assert.NotEqual(new PdfSelector().SelectorId, new PdfSelector(pairwiseGrouping: true).SelectorId);
        Assert.Contains(AgentProtocol.Sha(PdfBoundaryDecisions.Prompt)[..8], new PdfSelector(pairwiseGrouping: true).SelectorId);
        Assert.Throws<ArgumentException>(() => new PdfSelector(pairwiseGrouping: true, inlineGrouping: true));
    }

    [Fact]
    public void Pairwise_grouping_does_not_change_any_selection_request_or_selected_alias()
    {
        var source = Source.Value;
        var chosen = source.Cores.SelectMany((_, i) => source.Scope(i).Decide.Take(2)).ToHashSet();
        var snapshots = new List<Dictionary<string, string>>();
        foreach (var pairwise in new[] { false, true })
        {
            var requests = new ConcurrentDictionary<string, string>();
            ModelTurn Model(string unit, int turn, PreparedRequest request)
            {
                var p = Packet(request);
                if (p["stage"]!.GetValue<string>() == "GROUP")
                    return new(pairwise ? Reply(p).ToJsonString() : JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                        runs = p["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(),
                            headings = r["lines"]!.AsArray().Select(l => new[] { l!["alias"]!.GetValue<string>() }) }) }), [], "stop");
                Assert.True(requests.TryAdd(unit, request.Sha256));
                return new(JsonSerializer.Serialize(new { protocolVersion = SourceBoundSelection.Version, status = "COMPLETE",
                    decisions = p["targets"]!.AsArray().Select(t => chosen.Contains(t!["alias"]!.GetValue<string>())
                        ? (object)new { alias = t["alias"]!.GetValue<string>(), decision = "SELECT", sourceText = t["text"]!.GetValue<string>(), evidenceRefs = new[] { t["alias"]!.GetValue<string>() } }
                        : new { alias = t["alias"]!.GetValue<string>(), decision = "NOT_SELECT" }) }), [], "stop");
            }
            var result = AgentSelector.Select(source, Model, AgentSelector.BudgetFor(source, PdfMembershipInstructionV5.Instruction),
                6, CancellationToken.None, 0, () => 0, PdfMembershipInstructionV5.Instruction, pairwisePdfGrouping: pairwise);
            Assert.Empty(result.UnitFailures); Assert.True(chosen.SetEquals(result.SelectedAliases));
            snapshots.Add(requests.ToDictionary());
        }
        Assert.Equal(snapshots[0].OrderBy(x => x.Key), snapshots[1].OrderBy(x => x.Key));
    }
}
