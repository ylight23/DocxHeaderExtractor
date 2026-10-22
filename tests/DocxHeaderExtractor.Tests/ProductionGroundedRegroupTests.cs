using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Tools;
using static DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol;
using DocxHeaderExtractor.Agents;

namespace DocxHeaderExtractor.Tests;

using DocxHeaderExtractor.Contracts.Scan;

using DocxHeaderExtractor.HeadingAgent.Scan;

public sealed class ProductionGroundedRegroupTests
{
internal static ScanAgentSource Source()
    {
        var lines = Enumerable.Range(1, 16).Select(i => new OcrLine($"P001-ocr-original-L{i:000}", "original " + i,
            new PixelBox(20, i * 30, 250, 20), 95, 95, false, [])).ToArray();
        var view = new OcrView(new("ocr-original", "none", 1, "test", "1", "vie", 1), EvidenceOrigin.OcrDerived,
            [new(1, new string('a', 64), 400, 600, new string('b', 64), lines)]);
        var transcription = new TranscriptionSet(TranscriptionSet.CurrentVersion, "fixture", "fixture", 2,
            [new(lines[0].Id, "prefix Heading tail", ScanTranscription.Accepted)], [], 0, 0);
        return new(new(ScanOcrEvidence.CurrentVersion, new string('c', 64), "ScannedPdf", 1, new string('d', 64), [view], []),
            transcriptions: transcription);
    }

    static string A(int n) => $"P001-L{n:000}";
    static readonly string[][] Groups = [[A(1), A(2)], [A(3)]];
    static JsonObject Packet(PreparedRequest request) =>
        JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!.Text())!.AsObject();
    static JsonObject Verdict(JsonNode c, string decision, string fault = "")
    {
        var groups = c["proposedGroups"]!.Deserialize<string[][]>()!;
        var aliases = groups.SelectMany(g => g).ToArray();
        var v = JsonSerializer.SerializeToNode(new
        {
            run = c["run"]!.GetValue<string>(), decision, reason = "PRIVATE_REASON_NOT_FOR_SAFE_AUDIT", evidenceRefs = new[] { aliases[0] },
            membership = aliases.Select(a => new { alias = a, status = "HEADING", evidenceRefs = new[] { a } })
        })!.AsObject();
        if (decision == "REGROUP")
        {
            v["currentGroups"] = JsonSerializer.SerializeToNode(groups);
            v["suggestedGroups"] = JsonSerializer.SerializeToNode(fault == "same" ? groups : GroundedVerifierFixture.Different(groups));
            switch (fault)
            {
                case "missing": v.Remove("suggestedGroups"); break;
                case "echo": v["currentGroups"] = JsonSerializer.SerializeToNode(GroundedVerifierFixture.Different(groups)); break;
                case "duplicate": v["suggestedGroups"] = new JsonArray(new JsonArray(aliases[0], aliases[0], aliases[2])); break;
                case "reverse": v["suggestedGroups"] = new JsonArray(new JsonArray(aliases.Reverse().Select(a => (JsonNode?)JsonValue.Create(a)).ToArray())); break;
                case "unknown": v["suggestedGroups"] = new JsonArray(new JsonArray("PRIVATE_UNKNOWN_ALIAS")); break;
                case "empty": v["suggestedGroups"] = new JsonArray(); break;
                case "nonheading": v["membership"]![0]!["status"] = "NOT_HEADING"; break;
            }
        }
        return v;
    }
    static string Reply(JsonObject packet, string decision, string fault = "") => JsonSerializer.Serialize(new
    { protocolVersion = GroupVerifier.Version, verdicts = packet["candidates"]!.AsArray().Select(c => Verdict(c!, decision, fault)) });

    [Fact]
    public void Accept_may_echo_the_exact_current_partition_without_a_suggestion()
    {
        var c = new JsonObject { ["run"] = "V1", ["proposedGroups"] = JsonSerializer.SerializeToNode(Groups) };
        var verdict = Verdict(c, "ACCEPT");
        verdict["currentGroups"] = JsonSerializer.SerializeToNode(Groups);
        verdict["suggestedGroups"] = null;
        var parsed = GroupVerifier.ParseVerdict(verdict, new("V1", Groups), Groups.SelectMany(g => g).ToHashSet(), [], _ => { });
        Assert.Equal("ACCEPT", parsed.Decision);
        Assert.Null(parsed.SuggestedGroups);
    }

    [Fact]
    public void Contradiction_repairs_in_same_production_conversation_without_grouper_or_semantic_cycle()
    {
        int calls = 0;
        string? first = null;
        AgentUnit.Model model = (unit, round, request) =>
        {
            calls++;
            Assert.Equal("VERIFY-001", unit);
            var packet = Packet(request);
            Assert.Equal("GROUP_VERIFICATION", packet["stage"]!.GetValue<string>());
            if (round == 1) { first = Reply(packet, "REGROUP", "same"); return new(first, [], "stop"); }
            var messages = JsonNode.Parse(request.Body)!["messages"]!.AsArray();
            Assert.Equal(first, messages[2]!["content"]!.Text());
            Assert.Contains("BOUNDARY_VERDICT_SELF_CONTRADICTORY", messages[3]!["content"]!.Text());
            return new(Reply(packet, "ACCEPT"), [], "stop");
        };
        var result = GroupVerifier.Verify(Source(), new(Groups, [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new(30, 1), 1, default, 0, "Extract headings.");
        Assert.Equal(2, calls);
        Assert.Empty(result.Failures);
        Assert.Empty(result.UnresolvedAliases);
        Assert.Equal(Groups.Length, result.Groups.Count);
        Assert.Equal(0, result.Observation.BoundaryDisputes);
        var observations = result.Observation.BoundaryVerdicts!;
        Assert.Equal("BOUNDARY_VERDICT_SELF_CONTRADICTORY", observations[0].ProtocolError);
        Assert.All(observations, o => { Assert.Equal(0, o.BoundaryRepairCycle); Assert.False(o.Applied); });
        Assert.DoesNotContain("PRIVATE_REASON", JsonSerializer.Serialize(result.Observation));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("echo")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("reverse")]
    [InlineData("unknown")]
    [InlineData("empty")]
    [InlineData("nonheading")]
    public void Invalid_grounded_claims_stop_at_protocol_bound_and_never_authorize(string fault)
    {
        int calls = 0;
        AgentUnit.Model model = (_, _, request) => { calls++; return new(Reply(Packet(request), "REGROUP", fault), [], "stop"); };
        var check = GroupVerifier.Ask(Source(), [new("V1", Groups)],
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new(20, 1), default, 0, "Extract headings.", "VERIFY", 2);
        Assert.Equal(3, calls);
        Assert.Empty(check.Verdicts);
        Assert.NotNull(check.Failure);
        Assert.All(check.BoundaryVerdicts, b => { Assert.False(b.Applied); Assert.Equal(2, b.BoundaryRepairCycle); });
        Assert.DoesNotContain("PRIVATE_UNKNOWN_ALIAS", JsonSerializer.Serialize(check.BoundaryVerdicts));
    }

    [Fact]
    public void Valid_different_suggestion_is_audited_but_excluded_from_grouper_feedback()
    {
        AgentUnit.Model model = (_, _, request) => new(Reply(Packet(request), "REGROUP"), [], "stop");
        var check = GroupVerifier.Ask(Source(), [new("V1", Groups)],
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new(20, 1), default, 0, "Extract headings.", "VERIFY", 0);
        var verdict = Assert.Single(check.Verdicts);
        Assert.NotNull(verdict.SuggestedGroups);
        Assert.NotNull(Assert.Single(check.BoundaryVerdicts).SuggestedGroups);
        Assert.False(check.BoundaryVerdicts[0].Applied);
        var feedback = JsonSerializer.Serialize(GroupVerifier.BoundaryFeedback(new("V1", Groups), verdict, 1));
        Assert.DoesNotContain("SuggestedGroups", feedback);
        Assert.DoesNotContain("CurrentGroups", feedback);
    }

    [Fact]
    public void Independently_accepted_candidate_survives_neighbors_consistency_repair()
    {
        var initial = new AgentGrouping.Result([[A(1), A(2)], [A(5), A(6)]], [], [], 0);
        AgentUnit.Model model = (_, round, request) =>
        {
            var packet = Packet(request);
            var candidates = packet["candidates"]!.AsArray();
            if (round == 2) { Assert.Single(candidates); Assert.Equal("V2", candidates[0]!["run"]!.GetValue<string>()); }
            return new(JsonSerializer.Serialize(new { protocolVersion = GroupVerifier.Version,
                verdicts = candidates.Select(c => Verdict(c!, round == 1 && c!["run"]!.GetValue<string>() == "V2" ? "REGROUP" : "ACCEPT", "same")) }), [], "stop");
        };
        var result = GroupVerifier.Verify(Source(), initial, new Dictionary<string, TextOffsetSpan>(),
            new Dictionary<string, string>(), model, new(20, 1), 1, default, 0, "Extract headings.");
        Assert.Equal(2, result.Groups.Count);
        Assert.Empty(result.Failures);
    }
}
