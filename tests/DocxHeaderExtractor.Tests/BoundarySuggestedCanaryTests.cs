using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.V6Qualification;

namespace DocxHeaderExtractor.Tests;

public sealed class BoundarySuggestedCanaryTests
{
    static string A(int n) => $"P001-L{n:000}";
    static JsonObject Verdict(string decision, string[] aliases) => JsonSerializer.SerializeToNode(new
    {
        run = "V1", decision, reason = "PRIVATE_REASON", evidenceRefs = new[] { A(1) },
        membership = aliases.Select(a => new { alias = a, status = "HEADING", evidenceRefs = new[] { a } })
    })!.AsObject();

    [Theory]
    [InlineData("changed", "ACCEPT", "QUALIFIED_ACCEPT", 1)]
    [InlineData("changed", "REGROUP", "UNRESOLVED", 1)]
    [InlineData("same", "ACCEPT", "SAME_REJECTED_PARTITION", 0)]
    [InlineData("missing", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("duplicate", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("reversed", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("unknown", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("empty", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("absent", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("json", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("run", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("refs", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("membership", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("length", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("tool", "ACCEPT", "UNRESOLVED", 0)]
    [InlineData("recant", "ACCEPT", "PROPOSER_ACCEPT", 0)]
    public void Suggestions_have_no_authority_and_only_valid_changed_proposals_reach_fresh_verifier(
        string mode, string independentDecision, string status, int expectedIndependent)
    {
        var source = BoundaryStaticCanaryTests.Source();
        string[][] input = [[A(1), A(2)], [A(3)]];
        var aliases = input.SelectMany(g => g).ToArray();
        var seed = new JsonObject
        {
            ["packet"] = JsonSerializer.SerializeToNode(new
            {
                stage = "GROUP_VERIFICATION", documentSha256 = source.SourceSha256,
                candidates = new[] { new { run = "V1", proposedGroups = input, targetFacts = new { }, lines = new object[0] } },
                context = aliases.Select(a => new { alias = a }), documentStyles = new { }
            }),
            ["Verdicts"] = JsonSerializer.SerializeToNode(new[] { new AgentGroupVerification.Verdict("V1", "REGROUP", "PRIVATE_REASON", [A(1)],
                aliases.Select(a => new AgentGroupVerification.Membership(a, "HEADING", [a])).ToArray()) }),
            ["extents"] = new JsonObject(), ["readings"] = new JsonObject()
        };
        int independent = 0;
        AgentUnit.Model model = (unit, _, request) =>
        {
            if (unit == "C-INDEPENDENT")
            {
                independent++;
                Assert.DoesNotContain("PRIVATE_REASON", request.Body);
                Assert.DoesNotContain("frozenComplaint", request.Body);
                Assert.DoesNotContain("suggestedGroups", request.Body);
                return new(JsonSerializer.Serialize(new { protocolVersion = AgentGroupVerification.Version,
                    verdicts = new[] { Verdict(independentDecision, aliases) } }), [], "stop");
            }
            Assert.Contains("PRIVATE_REASON", request.Body);
            var verdict = Verdict(mode == "recant" ? "ACCEPT" : "REGROUP", aliases);
            string[][] suggestion = mode switch
            {
                "same" => input, "missing" => [[A(1)], [A(2)]], "duplicate" => [[A(1), A(1)], [A(3)]],
                "reversed" => [[A(2), A(1)], [A(3)]], "unknown" => [[A(1)], [A(2), A(4)]],
                "empty" => [[], aliases], _ => [[A(1)], [A(2), A(3)]]
            };
            verdict["suggestedGroups"] = mode == "recant" ? null : JsonSerializer.SerializeToNode(suggestion);
            if (mode == "absent") verdict.Remove("suggestedGroups");
            if (mode == "run") verdict["run"] = "OTHER";
            if (mode == "refs") verdict["evidenceRefs"] = new JsonArray("UNSEEN");
            if (mode == "membership") verdict["membership"]![1]!["status"] = "NOT_HEADING";
            var content = JsonSerializer.Serialize(new { protocolVersion = BoundarySuggestedCanary.Version, verdicts = new[] { verdict } });
            if (mode == "json") content = content[..^2];
            return new(content, mode == "tool" ? [new("id", "get_source_context", "{}")] : [], mode == "length" ? "length" : "stop");
        };
        var result = BoundarySuggestedCanary.Run(source, seed, model, new(12, 1), default);
        Assert.Equal(status, result.Status);
        Assert.Equal(expectedIndependent, independent);
        Assert.Equal(status == "QUALIFIED_ACCEPT", result.QualifiedAccept);
        Assert.DoesNotContain("PRIVATE_REASON", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Production_verdict_still_ignores_and_reports_suggested_groups()
    {
        var verdict = Verdict("REGROUP", [A(1)]);
        verdict["suggestedGroups"] = new JsonArray(new JsonArray(A(1)));
        var diagnostics = new List<AgentGroupVerification.FieldDiagnostic>();
        var parsed = AgentGroupVerification.ParseVerdict(verdict,
            new("V1", [[A(1)]]), new HashSet<string> { A(1) }, diagnostics, _ => { });
        Assert.Equal("REGROUP", parsed.Decision);
        Assert.Contains("suggestedGroups", Assert.Single(diagnostics).Unexpected);
        Assert.DoesNotContain("suggestedGroups", JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public void Live_flag_is_required_before_reading_files() => Assert.Equal(2,
        BoundarySuggestedCanaryCommand.Run(["boundary-suggested-c", "missing", "missing", "missing", "missing"]));
}
