using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.V6Qualification;

internal static class BoundaryGroundedCohort
{
    internal static readonly string[] Categories = ["SELF_CONTRADICTION_TO_ACCEPT_CURRENT", "CHANGED_INDEPENDENT_ACCEPT",
        "CHANGED_INDEPENDENT_REJECT_OR_UNRESOLVED", "PERSISTENT_SELF_CONTRADICTION", "MEMBERSHIP_DISPUTE_OR_UNRESOLVED",
        "DIRECT_ACCEPT_CURRENT", "OTHER_PROTOCOL_REPAIR_TO_ACCEPT_CURRENT", "OTHER_PROTOCOL_OR_TRANSPORT_FAILURE"];
    internal sealed record Proposal(string Id, string[][] Groups);
    internal sealed record Baseline(string Id, string Status, string[] Aliases, string? PacketSha256);
    internal sealed record Outcome(string Id, string Category, BoundaryGroundedCanary.Result? Result, string? Failure);
    internal sealed record Report(Baseline[] Baselines, Outcome[] Outcomes);

    internal static string Classify(BoundaryGroundedCanary.Result r)
    {
        if (r.Status == "QUALIFIED_ACCEPT_CHANGED") return "CHANGED_INDEPENDENT_ACCEPT";
        if (r.SuggestedGroups is not null && r.Rounds.LastOrDefault()?.Decision == "REGROUP" && r.Rounds[^1].ProtocolError is null)
            return "CHANGED_INDEPENDENT_REJECT_OR_UNRESOLVED";
        if (r.Status == "GROUNDED_ACCEPT_CURRENT")
            return r.Rounds.Any(x => x.ProtocolError == "BOUNDARY_VERDICT_SELF_CONTRADICTORY")
                ? "SELF_CONTRADICTION_TO_ACCEPT_CURRENT"
                : r.Rounds.Count == 1 ? "DIRECT_ACCEPT_CURRENT" : "OTHER_PROTOCOL_REPAIR_TO_ACCEPT_CURRENT";
        if (r.Failure == "BOUNDARY_VERDICT_SELF_CONTRADICTORY") return "PERSISTENT_SELF_CONTRADICTION";
        if (r.Failure is "VERDICT_MEMBERSHIP_DISPUTE" or "VERDICT_UNRESOLVED") return "MEMBERSHIP_DISPUTE_OR_UNRESOLVED";
        return "OTHER_PROTOCOL_OR_TRANSPORT_FAILURE";
    }

    internal static Report Run(IAgentSource source, Proposal[] proposals, AgentUnit.Model model, AgentBudget budget,
        int parallelism, CancellationToken ct, Action<string, string> retainPrivate,
        Action<Baseline[]> freeze, Action<Report>? progress = null)
    {
        ValidateProposals(source, proposals);
        var baselines = new Baseline[proposals.Length];
        var seeds = new JsonObject?[proposals.Length];
        var outcomes = new Outcome?[proposals.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
        var gate = new object();
        // Collect the entire predeclared universe first. No D call may select which C0 seeds get retained.
        Parallel.For(0, proposals.Length, options, index =>
        {
            var p = proposals[index];
            PreparedRequest? first = null;
            AgentUnit.Model baselineModel = (unit, round, request) =>
            {
                first ??= request;
                return model(p.Id + "-" + unit, round, request);
            };
            try
            {
                var candidate = new AgentGroupVerification.Candidate(p.Id, p.Groups);
                var check = AgentGroupVerification.Ask(source, [candidate], new Dictionary<string, TextOffsetSpan>(),
                    new Dictionary<string, string>(), baselineModel, budget, ct, 0,
                    source is ScanAgentSource ? ScanAgentProtocol.Instruction : Instruction, "COHORT-C0", 0);
                var packet = first is null ? null : BoundaryStaticCanary.Packet(first);
                var seed = JsonSerializer.SerializeToNode(new { packet, check.Verdicts, check.Evidence,
                    extents = new Dictionary<string, TextOffsetSpan>(), readings = new Dictionary<string, string>() })!.AsObject();
                retainPrivate(p.Id + "-seed", seed.ToJsonString());
                var verdict = check.Verdicts.SingleOrDefault();
                var status = verdict is null ? "NO_VALID_C0_VERDICT" : "C0_" + verdict.Decision;
                if (verdict?.Decision == "REGROUP" && verdict.Membership.All(m => m.Status == "HEADING") && packet is not null)
                {
                    // D has static evidence only. Exclude tool-informed seeds prospectively, never silently drop them.
                    if (check.VerifierEvidence?.Requests.Count > 0) status = "EXCLUDED_DYNAMIC_EVIDENCE";
                    else { status = "ELIGIBLE_REGROUP"; seeds[index] = seed; }
                }
                baselines[index] = new(p.Id, status, candidate.Aliases, packet is null ? null : Sha(packet.ToJsonString()));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                baselines[index] = new(p.Id, "BASELINE_RUNTIME_FAILURE", p.Groups.SelectMany(g => g).ToArray(), null);
            }
        });
        ct.ThrowIfCancellationRequested();
        // Synchronous durable barrier: caller must persist every baseline and eligible seed identity BEFORE D starts.
        freeze(baselines);
        Publish();
        Parallel.For(0, proposals.Length, options, index =>
        {
            if (seeds[index] is not { } seed) return;
            var p = proposals[index];
            AgentUnit.Model scoped = (unit, round, request) => model(p.Id + "-" + unit, round, request);
            Outcome outcome;
            try
            {
                var result = BoundaryGroundedCanary.Run(source, seed, scoped, budget, ct,
                    (name, text) => retainPrivate(p.Id + "-" + name, text));
                outcome = new(p.Id, Classify(result), result, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { outcome = new(p.Id, "OTHER_PROTOCOL_OR_TRANSPORT_FAILURE", null, "D_RUNTIME_FAILURE"); }
            lock (gate) { outcomes[index] = outcome; Publish(); }
        });
        return Snapshot();

        Report Snapshot() => new(baselines.ToArray(), outcomes.OfType<Outcome>().ToArray());
        void Publish() => progress?.Invoke(Snapshot());
    }

    internal static void ValidateProposals(IAgentSource source, Proposal[] proposals)
    {
        if (proposals.Length is < 1 or > 64 || proposals.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != proposals.Length ||
            proposals.Any(p => p.Id.Length == 0 || p.Id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-')))
            throw new InvalidOperationException("INVALID_COHORT_PROPOSALS");
        var positions = source.Lines.Select((line, i) => (line.Alias, i)).ToDictionary(x => x.Alias, x => x.i);
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in proposals)
        {
            var aliases = p.Groups.SelectMany(g => g).ToArray();
            if (p.Groups.Length == 0 || p.Groups.Any(g => g.Length is < 1 or > 8) || aliases.Length is < 2 or > 24 ||
                aliases.Any(a => !positions.ContainsKey(a)) ||
                !aliases.Select(a => positions[a]).SequenceEqual(Enumerable.Range(positions[aliases[0]], aliases.Length)) ||
                !scopes.Add(string.Join("\n", aliases))) throw new InvalidOperationException("INVALID_COHORT_PARTITION_OR_DUPLICATE_SCOPE");
        }
    }
}
