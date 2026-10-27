using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>Bounded model reconsideration of complete scopes; formatting only routes review, never decides membership.</summary>
internal static class BoundarySelectionReview
{
    internal const int MaxScopes = 2, MaxPosts = 2;
    internal const string Purpose = """
        Reconsider membership under instruction for every target in this scope. Previous decisions
        are fallible data; neither SELECT nor NOT_SELECT is presumed right. Check the local unit
        beginning at this location, any contributing title lines, and the descriptive lines next to
        it. Use what follows to distinguish its title from event attributes, rather than classifying
        all institutional names as metadata or all bold lines as headings. Compare the selected lines
        elsewhere as context, not as labels to copy. Membership does not require an outline parent.
        Keep a decision when supported, revise it when the source supports a different one, and use
        UNRESOLVED when evidence is insufficient. Give a complete source-bound response for this scope.
        """;

    internal static int[] Plan(AgentSource source, IReadOnlyList<AgentUnitOutcome> outcomes)
    {
        var eligible = Enumerable.Range(0, outcomes.Count).Where(i => outcomes[i].Status == "COMPLETE").ToArray();
        if (eligible.Length == 0) return [];
        double Size(int line) => source.LayoutFacts[source.Lines[line].Alias]["style"]?["sizePt"]?.GetValue<double>() ?? 0;
        // The first scope and one scope with the strongest measured font size. No text keywords or Gold.
        var first = eligible[0];
        var other = eligible.Where(i => i != first).OrderByDescending(i => Enumerable.Range(source.Cores[i].Start,
            source.Cores[i].End - source.Cores[i].Start).Max(Size)).ThenBy(i => i).Take(1);
        return new[] { first }.Concat(other).ToArray();
    }

    internal static JsonObject Context(IAgentSource source, IReadOnlyList<AgentUnitOutcome> snapshot, int index)
    {
        var selected = snapshot.Where(o => o.Status == "COMPLETE").SelectMany(o => o.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key)).ToHashSet();
        return JsonSerializer.SerializeToNode(new
        {
            version = "BOUNDARY_SELECTION_REVIEW_V1", purpose = Purpose,
            routing = "first-complete-scope-and-one-largest-font-scope; routing is not a heading label",
            previousDecisions = snapshot[index].Decisions,
            selectedSourceLines = source.Lines.Where(l => selected.Contains(l.Alias)).Select(l => new { alias = l.Alias, page = l.Page, text = l.Text })
        })!.AsObject();
    }

    internal static string[] NameCandidates(IAgentSource source, AgentUnitOutcome outcome)
    {
        var positions = source.Lines.Select((l, i) => (l.Alias, i)).ToDictionary(x => x.Alias, x => x.i);
        var selected = outcome.Decisions.Where(x => x.Value == "SELECT").Select(x => x.Key)
            .OrderBy(a => positions[a]).ToArray();
        if (selected.Length < 2) return [];
        bool Centered(string alias)
        {
            var line = source.Lines[positions[alias]];
            var pageLines = source.Lines.Where(l => l.Page == line.Page).ToArray();
            var lefts = pageLines.Select(l => Evidence.LeftEdgeOf(l.Bbox)).Where(x => x is not null).Select(x => x!.Value).ToArray();
            var rights = pageLines.Select(l => Evidence.RightEdgeOf(l.Bbox)).Where(x => x is not null).Select(x => x!.Value).ToArray();
            return lefts.Length > 0 && rights.Length > 0 && Evidence.LeftEdgeOf(line.Bbox) is { } left
                && Evidence.RightEdgeOf(line.Bbox) is { } right
                && Math.Abs((left + right) / 2 - (lefts.Min() + rights.Max()) / 2) <= 8;
        }
        if (!Centered(selected[0])) return [];
        var run = new List<string> { selected[0] };
        foreach (var alias in selected.Skip(1))
        {
            var previous = positions[run[^1]]; var current = positions[alias];
            if (current != previous + 1 || source.Lines[current].Page != source.Lines[previous].Page || !Centered(alias)) break;
            run.Add(alias);
        }
        return run.Count >= 2 ? run.ToArray() : [];
    }

    internal static (string[] Events, string[] Failures) Apply(IAgentSource source, AgentUnitOutcome[] outcomes,
        AgentUnit.Model model, AgentBudget budget, int parallelism, string instruction, CancellationToken ct, int retryDelayMs = 0)
    {
        if (source is not AgentSource pdf) throw new ArgumentException("PDF_BOUNDARY_REVIEW_REQUIRED");
        var namesOnly = source.Flavor.PreloadPageLayoutEvidence;
        var scope = Plan(pdf, outcomes).Where(i => !namesOnly || NameCandidates(source, outcomes[i]).Length > 0).ToArray();
        var snapshot = outcomes.ToArray();
        var revised = new AgentUnitOutcome[scope.Length];
        Parallel.For(0, scope.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(MaxScopes, parallelism), CancellationToken = ct }, n =>
        {
            var candidates = namesOnly ? NameCandidates(source, snapshot[scope[n]]) : [];
            var tools = source.Flavor.CreateTools(source);
            (int Start, int End)? targetScope = namesOnly ? (tools.PositionOf(candidates[0]), tools.PositionOf(candidates[^1]) + 1) : null;
            revised[n] = AgentUnit.Run(source, scope[n], model, budget, retryDelayMs,
                namesOnly ? PdfNameContributionReview.Instruction : instruction,
                selectionReview: namesOnly ? new JsonObject
                {
                    ["version"] = "PDF_NAME_CONTRIBUTION_REVIEW_V1", ["purpose"] = PdfNameContributionReview.Purpose,
                    ["candidateAliases"] = new JsonArray(NameCandidates(source, snapshot[scope[n]]).Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                    ["routing"] = "centered-opening-selected-run-in-first-and-largest-font-scope; routing supplies no semantic label"
                } : Context(source, snapshot, scope[n]), maxPosts: MaxPosts, scope: targetScope);
        });
        var events = new List<string>(); var failures = new List<string>();
        for (var n = 0; n < scope.Length; n++)
        {
            var i = scope[n]; var before = snapshot[i]; var after = revised[n];
            var exactCoverage = (namesOnly ? NameCandidates(source, before) : before.Decisions.Keys.ToArray())
                .ToHashSet().SetEquals(after.Decisions.Keys);
            var applied = after.Status == "COMPLETE" && exactCoverage;
            // Apply only a complete model decision for this owned scope; never merge a partial verdict or infer SELECT.
            if (applied && namesOnly)
            {
                var candidates = NameCandidates(source, before).ToHashSet(StringComparer.Ordinal);
                var decisions = before.Decisions.ToDictionary(x => x.Key, x =>
                    candidates.Contains(x.Key) && after.Decisions[x.Key] == "NOT_SELECT" ? "NOT_SELECT" : x.Value, StringComparer.Ordinal);
                var removed = candidates.Any(a => before.Decisions[a] != decisions[a]);
                outcomes[i] = before with { Decisions = decisions,
                    Reasons = before.Reasons?.Where(x => decisions.GetValueOrDefault(x.Key) == "SELECT")
                        .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                    Analysis = removed ? null : before.Analysis };
            }
            else if (applied) outcomes[i] = after with { UnitId = before.UnitId };
            else failures.Add("BOUNDARY_REVIEW:" + before.UnitId + ":" + (after.Failure ?? after.Status));
            events.Add(JsonSerializer.Serialize(new { stage = namesOnly ? "name-contribution-review" : "boundary-selection-review", unit = before.UnitId,
                reviewUnit = after.UnitId, applied, after.Status, after.Failure, after.Posts, after.RepairsUsed,
                candidateAliases = namesOnly ? NameCandidates(source, before) : null,
                before = before.Decisions, proposed = after.Decisions,
                after = namesOnly ? outcomes[i].Decisions : after.Decisions, after.RequestSha256s }));
        }
        return (events.ToArray(), failures.ToArray());
    }
}
