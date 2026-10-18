using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.V6Qualification;

namespace DocxHeaderExtractor.Tests;

public sealed class BoundaryGroundedCohortTests
{
    static string A(int n) => $"P001-L{n:000}";
    static JsonObject Verdict(JsonNode candidate, string decision)
    {
        var aliases = candidate["proposedGroups"]!.AsArray().SelectMany(g => g!.AsArray()).Select(a => a!.GetValue<string>()).ToArray();
        return JsonSerializer.SerializeToNode(new
        {
            run = candidate["run"]!.GetValue<string>(), decision, reason = "PRIVATE_COHORT_REASON", evidenceRefs = new[] { aliases[0] },
            membership = aliases.Select(a => new { alias = a, status = "HEADING", evidenceRefs = new[] { a } })
        })!.AsObject();
    }

    [Fact]
    public void Complete_universe_is_frozen_before_D_and_no_case_is_replaced_or_pooled_with_pilot()
    {
        var source = BoundaryStaticCanaryTests.Source();
        var proposals = Enumerable.Range(0, 5).Select(i => new BoundaryGroundedCohort.Proposal("S" + i,
            [[A(i * 3 + 1), A(i * 3 + 2)], [A(i * 3 + 3)]])).ToArray();
        int c0Calls = 0, dCalls = 0;
        bool frozen = false;
        var snapshots = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        AgentUnit.Model model = (unit, round, request) =>
        {
            var packet = BoundaryStaticCanary.Packet(request);
            var candidate = packet["candidates"]![0]!;
            var initial = unit.Contains("COHORT-C0", StringComparison.Ordinal);
            if (initial)
            {
                Assert.False(frozen);
                Interlocked.Increment(ref c0Calls);
                return new(JsonSerializer.Serialize(new { protocolVersion = AgentGroupVerification.Version,
                    verdicts = new[] { Verdict(candidate, unit.StartsWith("S4-", StringComparison.Ordinal) ? "ACCEPT" : "REGROUP") } }), [], "stop");
            }
            Assert.True(frozen);
            Assert.Equal(5, c0Calls);
            Interlocked.Increment(ref dCalls);
            if (unit.Contains("INDEPENDENT", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("PRIVATE_COHORT_REASON", request.Body);
                return new(JsonSerializer.Serialize(new { protocolVersion = AgentGroupVerification.Version,
                    verdicts = new[] { Verdict(candidate, "ACCEPT") } }), [], "stop");
            }
            var decision = unit.StartsWith("S3-", StringComparison.Ordinal) ? "UNRESOLVED"
                : unit.StartsWith("S0-", StringComparison.Ordinal) && round == 2 ? "ACCEPT" : "REGROUP";
            var verdict = Verdict(candidate, decision);
            verdict["currentGroups"] = candidate["proposedGroups"]!.DeepClone();
            verdict["suggestedGroups"] = decision != "REGROUP" ? null :
                unit.StartsWith("S1-", StringComparison.Ordinal)
                    ? new JsonArray(candidate["proposedGroups"]!.AsArray().SelectMany(g => g!.AsArray()).Select(a => (JsonNode?)new JsonArray(a!.DeepClone())).ToArray())
                    : candidate["proposedGroups"]!.DeepClone();
            return new(JsonSerializer.Serialize(new { protocolVersion = BoundaryGroundedCanary.Version, verdicts = new[] { verdict } }), [], "stop");
        };
        var report = BoundaryGroundedCohort.Run(source, proposals, model, new(100, 1), 4, default,
            (name, content) => snapshots[name] = content,
            baselines =>
            {
                Assert.Equal(5, c0Calls);
                Assert.Equal(0, dCalls);
                Assert.Equal(4, baselines.Count(b => b.Status == "ELIGIBLE_REGROUP"));
                Assert.Equal("C0_ACCEPT", baselines[4].Status);
                Assert.All(baselines, b => Assert.True(snapshots.ContainsKey(b.Id + "-seed")));
                frozen = true;
            });
        Assert.Equal(5, report.Baselines.Length);
        Assert.Equal(4, report.Outcomes.Length);
        Assert.Equal("SELF_CONTRADICTION_TO_ACCEPT_CURRENT", report.Outcomes.Single(o => o.Id == "S0").Category);
        Assert.Equal("CHANGED_INDEPENDENT_ACCEPT", report.Outcomes.Single(o => o.Id == "S1").Category);
        Assert.Equal("PERSISTENT_SELF_CONTRADICTION", report.Outcomes.Single(o => o.Id == "S2").Category);
        Assert.Equal("MEMBERSHIP_DISPUTE_OR_UNRESOLVED", report.Outcomes.Single(o => o.Id == "S3").Category);
        Assert.DoesNotContain("PRIVATE_COHORT_REASON", JsonSerializer.Serialize(report));
    }

    [Fact]
    public void Failed_freeze_barrier_prevents_all_D_calls()
    {
        int calls = 0;
        AgentUnit.Model model = (_, _, request) =>
        {
            calls++;
            var candidate = BoundaryStaticCanary.Packet(request)["candidates"]![0]!;
            return new(JsonSerializer.Serialize(new { protocolVersion = AgentGroupVerification.Version,
                verdicts = new[] { Verdict(candidate, "REGROUP") } }), [], "stop");
        };
        Assert.Throws<IOException>(() => BoundaryGroundedCohort.Run(BoundaryStaticCanaryTests.Source(),
            [new("S0", [[A(1), A(2)]])], model, new(100, 1), 1, default, (_, _) => { }, _ => throw new IOException()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Duplicate_scopes_cannot_inflate_cohort_denominator() => Assert.Throws<InvalidOperationException>(() =>
        BoundaryGroundedCohort.ValidateProposals(BoundaryStaticCanaryTests.Source(),
            [new("A", [[A(1), A(2)]]), new("B", [[A(1)], [A(2)]])]));

    [Fact]
    public void Explicit_live_flag_is_required() => Assert.Equal(2,
        BoundaryGroundedCohortCommand.Run(["boundary-grounded-cohort", "missing", "missing"]));

    [Theory]
    [InlineData("GROUNDED_ACCEPT_CURRENT", null, null, "DIRECT_ACCEPT_CURRENT")]
    [InlineData("GROUNDED_ACCEPT_CURRENT", "BOUNDARY_CURRENT_GROUPS_MISMATCH", null, "OTHER_PROTOCOL_REPAIR_TO_ACCEPT_CURRENT")]
    [InlineData("GROUNDED_ACCEPT_CURRENT", "BOUNDARY_VERDICT_SELF_CONTRADICTORY", null, "SELF_CONTRADICTION_TO_ACCEPT_CURRENT")]
    [InlineData("UNRESOLVED", null, "BOUNDARY_VERDICT_SELF_CONTRADICTORY", "PERSISTENT_SELF_CONTRADICTION")]
    [InlineData("UNRESOLVED", null, "VERDICT_MEMBERSHIP_DISPUTE", "MEMBERSHIP_DISPUTE_OR_UNRESOLVED")]
    [InlineData("UNRESOLVED", null, "GROUNDED_VERDICT_PROTOCOL_INVALID", "OTHER_PROTOCOL_OR_TRANSPORT_FAILURE")]
    public void Categories_do_not_credit_unobserved_consistency_repairs(string status, string? repaired, string? failure, string expected)
    {
        var rounds = new List<BoundaryGroundedCanary.Round>();
        if (repaired is not null) rounds.Add(new(1, "stop", 10, "REGROUP", [[A(1), A(2)]], [[A(1), A(2)]], repaired, []));
        rounds.Add(new(rounds.Count + 1, "stop", 10, status == "GROUNDED_ACCEPT_CURRENT" ? "ACCEPT" : null, [[A(1), A(2)]], null, failure, []));
        var r = new BoundaryGroundedCanary.Result(status, "sha", [A(1), A(2)], [[A(1), A(2)]], null, null, false, failure, rounds);
        Assert.Equal(expected, BoundaryGroundedCohort.Classify(r));
    }

    [Fact]
    public void Independent_rejection_is_retained_as_changed_unresolved()
    {
        var result = new BoundaryGroundedCanary.Result("UNRESOLVED", "sha", [A(1), A(2)], [[A(1), A(2)]],
            [[A(1)], [A(2)]], "REGROUP", false, null,
            [new(1, "stop", 10, "REGROUP", [[A(1), A(2)]], [[A(1)], [A(2)]], null, [])]);
        Assert.Equal("CHANGED_INDEPENDENT_REJECT_OR_UNRESOLVED", BoundaryGroundedCohort.Classify(result));
    }
}
