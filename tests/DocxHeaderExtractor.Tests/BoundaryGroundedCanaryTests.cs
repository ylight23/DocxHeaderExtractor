using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.V6Qualification;

namespace DocxHeaderExtractor.Tests;

public sealed class BoundaryGroundedCanaryTests
{
    static string A(int n) => $"P001-L{n:000}";
    static readonly string[][] Input = [[A(1), A(2)], [A(3)]];
    static readonly string[][] Changed = [[A(1)], [A(2), A(3)]];
    const string Secret = "PRIVATE_COMPLAINT";
    static JsonObject Seed()
    {
        var source = BoundaryStaticCanaryTests.Source();
        var aliases = Input.SelectMany(g => g).ToArray();
        return new()
        {
            ["packet"] = JsonSerializer.SerializeToNode(new
            {
                stage = "GROUP_VERIFICATION", documentSha256 = source.SourceSha256,
                candidates = new[] { new { run = "V1", proposedGroups = Input, targetFacts = new { }, lines = Array.Empty<object>() } },
                context = aliases.Select(a => new { alias = a }), documentStyles = new { }
            }),
            ["Verdicts"] = JsonSerializer.SerializeToNode(new[] { new AgentGroupVerification.Verdict("V1", "REGROUP", Secret, [A(1)],
                aliases.Select(a => new AgentGroupVerification.Membership(a, "HEADING", [a])).ToArray()) }),
            ["extents"] = new JsonObject(), ["readings"] = new JsonObject()
        };
    }
    static JsonObject Verdict(string decision) => JsonSerializer.SerializeToNode(new
    {
        run = "V1", decision, reason = Secret, evidenceRefs = new[] { A(1) },
        membership = Input.SelectMany(g => g).Select(a => new { alias = a, status = "HEADING", evidenceRefs = new[] { a } })
    })!.AsObject();
    static string Reply(string decision, string[][] current, string[][]? suggested)
    {
        var v = Verdict(decision);
        v["currentGroups"] = JsonSerializer.SerializeToNode(current);
        v["suggestedGroups"] = JsonSerializer.SerializeToNode(suggested);
        return JsonSerializer.Serialize(new { protocolVersion = BoundaryGroundedCanary.Version, verdicts = new[] { v } });
    }

    [Theory]
    [InlineData("same", "BOUNDARY_VERDICT_SELF_CONTRADICTORY")]
    [InlineData("echo", "BOUNDARY_CURRENT_GROUPS_MISMATCH")]
    public void Consistency_error_is_repaired_in_same_conversation_without_semantic_cycle(string fault, string error)
    {
        int calls = 0;
        string? firstReply = null;
        AgentUnit.Model model = (unit, round, request) =>
        {
            calls++;
            Assert.Equal("D-VERIFIER", unit);
            var messages = JsonNode.Parse(request.Body)!["messages"]!.AsArray();
            if (round == 1)
            {
                Assert.Equal(2, messages.Count);
                firstReply = Reply("REGROUP", fault == "echo" ? Changed : Input, Input);
                return new(firstReply, [], "stop");
            }
            Assert.Equal(2, round);
            Assert.Equal(4, messages.Count);
            Assert.Equal(firstReply, messages[2]!["content"]!.GetValue<string>());
            Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());
            var feedback = JsonNode.Parse(messages[3]!["content"]!.GetValue<string>())!;
            Assert.Equal("PROTOCOL_CONSISTENCY_REPAIR", feedback["stage"]!.GetValue<string>());
            Assert.Equal(error, feedback["error"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(Input), feedback["proposedGroups"]));
            return new(Reply("ACCEPT", Input, null), [], "stop");
        };
        var result = BoundaryGroundedCanary.Run(BoundaryStaticCanaryTests.Source(), Seed(), model, new(12, 1), default);
        Assert.Equal(2, calls);
        Assert.Equal("GROUNDED_ACCEPT_CURRENT", result.Status);
        Assert.True(result.QualifiedAccept);
        Assert.Equal(error, result.Rounds[0].ProtocolError);
        Assert.Null(result.Rounds[1].ProtocolError);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Cancellation_after_inconsistent_response_prevents_repair_or_acceptance()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        AgentUnit.Model model = (_, _, _) =>
        {
            calls++;
            cancellation.Cancel();
            return new(Reply("REGROUP", Input, Input), [], "stop");
        };
        Assert.Throws<OperationCanceledException>(() => BoundaryGroundedCanary.Run(
            BoundaryStaticCanaryTests.Source(), Seed(), model, new(12, 1), cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Repeated_self_contradiction_stops_after_three_protocol_rounds()
    {
        int calls = 0;
        AgentUnit.Model model = (unit, round, request) =>
        {
            calls++;
            Assert.Equal("D-VERIFIER", unit);
            Assert.Equal(2 + (round - 1) * 2, JsonNode.Parse(request.Body)!["messages"]!.AsArray().Count);
            return new(Reply("REGROUP", Input, Input), [], "stop");
        };
        var result = BoundaryGroundedCanary.Run(BoundaryStaticCanaryTests.Source(), Seed(), model, new(12, 1), default);
        Assert.Equal(BoundaryGroundedCanary.MaxProtocolRounds, calls);
        Assert.Equal("UNRESOLVED", result.Status);
        Assert.Equal("BOUNDARY_VERDICT_SELF_CONTRADICTORY", result.Failure);
        Assert.False(result.QualifiedAccept);
    }

    [Theory]
    [InlineData("ACCEPT", true)]
    [InlineData("REGROUP", false)]
    [InlineData("UNRESOLVED", false)]
    public void Valid_changed_proposal_requires_fresh_independent_acceptance(string independentDecision, bool accept)
    {
        int independentCalls = 0;
        AgentUnit.Model model = (unit, round, request) =>
        {
            if (unit == "D-VERIFIER") return new(Reply("REGROUP", Input, Changed), [], "stop");
            independentCalls++;
            Assert.Equal("D-INDEPENDENT", unit);
            Assert.DoesNotContain(Secret, request.Body);
            Assert.DoesNotContain("frozenComplaint", request.Body);
            Assert.DoesNotContain("suggestedGroups", request.Body);
            return new(JsonSerializer.Serialize(new { protocolVersion = AgentGroupVerification.Version,
                verdicts = new[] { Verdict(independentDecision) } }), [], "stop");
        };
        var result = BoundaryGroundedCanary.Run(BoundaryStaticCanaryTests.Source(), Seed(), model, new(12, 1), default);
        Assert.Equal(1, independentCalls);
        Assert.Equal(accept, result.QualifiedAccept);
        Assert.Equal(independentDecision, result.IndependentVerdict);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("reverse")]
    [InlineData("unknown")]
    [InlineData("membership")]
    [InlineData("length")]
    [InlineData("json")]
    public void Invalid_responses_never_authorize_or_leak_model_values(string fault)
    {
        int calls = 0;
        AgentUnit.Model model = (unit, round, request) =>
        {
            calls++;
            Assert.Equal("D-VERIFIER", unit);
            var suggested = fault switch
            {
                "missing" => new[] { new[] { A(1), A(2) } },
                "duplicate" => [[A(1), A(1)], [A(3)]],
                "reverse" => [[A(2), A(1)], [A(3)]],
                "unknown" => [[A(1), A(2)], ["PRIVATE_UNKNOWN_ALIAS"]],
                _ => Changed
            };
            var content = Reply("REGROUP", Input, suggested);
            if (fault == "membership") content = content.Replace("\"HEADING\"", "\"NOT_HEADING\"");
            if (fault == "json") content = content[..^2];
            return new(content, [], fault == "length" ? "length" : "stop");
        };
        var result = BoundaryGroundedCanary.Run(BoundaryStaticCanaryTests.Source(), Seed(), model, new(12, 1), default);
        Assert.False(result.QualifiedAccept);
        Assert.Null(result.IndependentVerdict);
        Assert.Equal(fault == "length" ? 1 : 3, calls);
        Assert.DoesNotContain("PRIVATE_UNKNOWN_ALIAS", JsonSerializer.Serialize(result));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Missing_membership_and_evidence_are_named_in_repair_and_complete_template_is_preserved()
    {
        AgentUnit.Model model = (unit, round, request) =>
        {
            Assert.Equal("D-VERIFIER", unit);
            if (round == 1)
            {
                var root = JsonNode.Parse(Reply("ACCEPT", Input, null))!;
                root["verdicts"]![0]!.AsObject().Remove("membership");
                root["verdicts"]![0]!.AsObject().Remove("evidenceRefs");
                return new(root.ToJsonString(), [], "stop");
            }
            var messages = JsonNode.Parse(request.Body)!["messages"]!.AsArray();
            var repair = JsonNode.Parse(messages[^1]!["content"]!.GetValue<string>())!;
            Assert.Equal(new[] { "evidenceRefs", "membership" }, repair["missingFields"]!.Deserialize<string[]>());
            var template = repair["responseTemplate"]!["verdicts"]![0]!;
            Assert.Equal(3, template["membership"]!.AsArray().Count);
            Assert.NotNull(template["evidenceRefs"]);
            Assert.NotNull(template["currentGroups"]);
            Assert.True(template.AsObject().ContainsKey("suggestedGroups"));
            return new(Reply("ACCEPT", Input, null), [], "stop");
        };
        var result = BoundaryGroundedCanary.Run(BoundaryStaticCanaryTests.Source(), Seed(), model, new(12, 1), default);
        Assert.Equal("GROUNDED_ACCEPT_CURRENT", result.Status);
        Assert.Equal(new[] { "evidenceRefs", "membership" }, result.Rounds[0].MissingFields);
    }

    [Fact]
    public void Exhausted_budget_does_not_send_consistency_repair()
    {
        int calls = 0;
        AgentUnit.Model model = (_, _, _) => { calls++; return new(Reply("REGROUP", Input, Input), [], "stop"); };
        var result = BoundaryGroundedCanary.Run(BoundaryStaticCanaryTests.Source(), Seed(), model, new(1, 1), default);
        Assert.Equal(1, calls);
        Assert.False(result.QualifiedAccept);
    }

    [Fact]
    public void D_requires_explicit_live_flag() => Assert.Equal(2,
        BoundarySuggestedCanaryCommand.Run(["boundary-grounded-d", "missing", "missing", "missing", "missing"]));
}
