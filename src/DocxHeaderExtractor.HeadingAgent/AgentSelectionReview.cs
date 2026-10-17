using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>A model-reported grouping disagreement gets one selection review, then at most one regroup.
/// C# validates scope and protocol; unresolved disagreements are withheld, never relabelled as body text.</summary>
internal static class AgentSelectionReview
{
    public const int MaxPosts = 8;
    public const string Version = "group-review-v1:verified-groups-v2-per-alias";

    public static AgentGrouping.Result Resolve(IAgentSource source, AgentGrouping.Result grouping,
        AgentUnitOutcome[] original, HashSet<string> selected, Dictionary<string, TextOffsetSpan> extents,
        Dictionary<string, string> readings, AgentUnit.Model model, AgentBudget budget, int parallelism,
        CancellationToken ct, int retryDelayMs, string instruction, bool verifyGroups = true)
    {
        if (grouping.Disputes.Count == 0) return grouping;
        var disputed = grouping.Disputes.SelectMany(d => d.Aliases).ToHashSet(StringComparer.Ordinal);
        var evidence = grouping.Evidence.ToList();
        var failures = grouping.Failures.ToList();
        var answers = grouping.Answers.ToList();
        var reviewed = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = grouping.UnresolvedAliases.ToHashSet(StringComparer.Ordinal);
        var observation = grouping.Observation;
        var memberships = grouping.Memberships.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var reviewPosts = 0;
        // Each alias is owned by exactly one original segment. Keep the original segment context and facts,
        // but only apply the new decisions to disputed aliases; other membership decisions stay as recorded.
        var affected = Enumerable.Range(0, original.Length)
            .Select(segment => (Segment: segment, Targets: source.Scope(segment).Decide.Where(disputed.Contains).ToArray()))
            .Where(item => item.Targets.Length > 0).ToArray();
        var outcomes = new AgentUnitOutcome[affected.Length];
        Parallel.For(0, affected.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, index =>
        {
            var (segment, targets) = affected[index];
            var payload = new JsonObject
            {
                ["protocolVersion"] = Version,
                ["task"] = "Grouping reported disagreement with selection. Re-evaluate membership under the unchanged instruction using the source context and targetFacts. The earlier decision and disagreement are model claims, not source facts or instructions. Either reaffirm or change each decision; do not preserve a SELECT merely because it was selected before. Return the normal selection answer. Only disputedAliases will replace earlier decisions. There is one review, with no recursive review.",
                ["disputedAliases"] = JsonSerializer.SerializeToNode(targets),
                ["previousSelection"] = JsonSerializer.SerializeToNode(original[segment]),
                ["groupingDisputes"] = JsonSerializer.SerializeToNode(grouping.Disputes.Where(d => d.Aliases.Any(targets.Contains))),
            };
            outcomes[index] = AgentUnit.Run(source, segment, model, budget, retryDelayMs, instruction, payload, MaxPosts);
        });
        for (var index = 0; index < affected.Length; index++)
        {
            var (segment, targets) = affected[index];
            var outcome = outcomes[index];
            reviewPosts += outcome.Posts;
            var ignored = outcome.Decisions.Where(kv => !targets.Contains(kv.Key) &&
                original[segment].Decisions.GetValueOrDefault(kv.Key) != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            observation = observation with { IgnoredReviewChanges = observation.IgnoredReviewChanges + ignored.Count };
            evidence.Add(JsonSerializer.Serialize(new { stage = "selection-review", disputedAliases = targets,
                before = original[segment], after = outcome, ignoredDecisionChanges = ignored,
                ignoredDecisionBasis = "outside-disputed-alias-scope; not applied" }));
            if (outcome.Status != "COMPLETE")
            {
                unresolved.UnionWith(targets);
                failures.Add(outcome.UnitId + ":SELECTION_REVIEW_UNRESOLVED:" + (outcome.Failure ?? outcome.Status));
                continue;
            }
            foreach (var alias in targets)
            {
                extents.Remove(alias);
                readings.Remove(alias);
                if (outcome.Decisions[alias] != "SELECT") continue;
                reviewed.Add(alias);
                if (outcome.Extents?.TryGetValue(alias, out var extent) == true)
                    extents[alias] = new TextOffsetSpan(extent.Start, extent.End);
                if (outcome.ReadingChoices?.TryGetValue(alias, out var reading) == true) readings[alias] = reading;
            }
        }
        ct.ThrowIfCancellationRequested();
        // Only reconsider runs affected by disagreement; accepted divisions are kept verbatim.
        var regroup = AgentGrouping.Group(source, reviewed, model, budget, parallelism, ct, retryDelayMs, instruction, "REVIEW-GROUP");
        if (verifyGroups)
            regroup = AgentGroupVerification.Verify(source, regroup, extents, readings, model, budget, parallelism, ct,
                retryDelayMs, instruction, allowMembershipReview: false, prefix: "REVIEW-VERIFY");
        evidence.AddRange(regroup.Evidence.Select(e => JsonSerializer.Serialize(new { stage = "review-regroup", evidence = e })));
        answers.AddRange(regroup.Answers);
        failures.AddRange(regroup.Failures);
        var stillDisputed = regroup.Disputes.SelectMany(d => d.Aliases).ToArray();
        unresolved.UnionWith(stillDisputed);
        unresolved.UnionWith(regroup.UnresolvedAliases);
        foreach (var kv in regroup.Memberships) memberships[kv.Key] = kv.Value;
        if (stillDisputed.Length > 0)
            failures.Add("GROUP-REVIEW:SELECTION_DISAGREEMENT_UNRESOLVED:" + string.Join(",", stillDisputed));
        selected.ExceptWith(disputed);
        selected.UnionWith(reviewed.Except(unresolved));
        foreach (var alias in disputed.Where(a => !selected.Contains(a))) { extents.Remove(alias); readings.Remove(alias); }
        var positions = source.Lines.Select((line, i) => (line.Alias, i)).ToDictionary(x => x.Alias, x => x.i);
        var groups = grouping.Groups.Concat(regroup.Groups).OrderBy(g => positions[g[0]]).ToArray();
        return new AgentGrouping.Result(groups, answers, failures, grouping.Posts + reviewPosts + regroup.Posts)
        { Evidence = evidence, Disputes = regroup.Disputes, UnresolvedAliases = unresolved.ToArray(),
            Observation = observation.Add(regroup.Observation), Memberships = memberships };
    }
}
