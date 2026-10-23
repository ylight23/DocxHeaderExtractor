using System.Diagnostics;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Hierarchy;
using DocxHeaderExtractor.Contracts.Provider;

namespace DocxHeaderExtractor.DocumentProcessing.Placement;

/// <summary>
/// The C# half of parent placement. The placer (<see cref="IHeadingPlacer"/>) proposes a parent for each settled heading; this keeps a relation
/// only when it names ROOT, NONE, UNRESOLVED or an earlier bound heading, and the hierarchy resolver derives depth from what is kept. It never
/// adds, removes or rewrites a heading and never asks a model anything. Unresolved ancestry remains a legitimate outcome.
/// </summary>
internal static class ParentResolver
{
    internal sealed record PlacementResult(
        IReadOnlyList<CanonicalSemanticBoundHeading> Headings,
        PlacementAudit Observation);

    internal static async Task<PlacementResult> PlaceAsync(
        IReadOnlyList<CanonicalSemanticBoundHeading> bound, IHeadingPlacer placer, ModelAccess access,
        HeadingPlacementEvidence evidence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(placer);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(evidence);
        var clock = Stopwatch.StartNew();
        var ordered = bound.OrderBy(h => h.SourceOrdinal).ThenBy(h => h.Start).ToArray();
        var source = evidence.Source ?? throw new ArgumentException("PLACEMENT_SOURCE_REQUIRED", nameof(evidence));
        var lines = source.Lines.OrderBy(l => l.Ordinal).ToArray();
        var byAlias = ordered.ToDictionary(h => h.Alias, StringComparer.Ordinal);
        var parts = ordered.SelectMany(h => h.Parts.Count > 0 ? h.Parts.Select(p => p.Alias) : [h.Alias]).ToHashSet(StringComparer.Ordinal);
        var ownerByAlias = ordered.SelectMany(h => (h.Parts.Count > 0 ? h.Parts.Select(p => p.Alias) : [h.Alias])
            .Select(alias => (Alias: alias, h.SourceId))).ToDictionary(p => p.Alias, p => p.SourceId, StringComparer.Ordinal);

        var proposal = await placer.ProposeAsync(new PlacementRequest(ordered, evidence), access, ct).ConfigureAwait(false);
        var proposed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (alias, parent) in proposal.Parents) proposed[alias] = parent;
        var statusByAlias = new Dictionary<string, string>(proposal.Basis, StringComparer.Ordinal);
        var failures = new List<string>(proposal.Failures);

        // A relation is usable only when it names ROOT/NONE or an earlier bound heading.
        // The hierarchy resolver independently derives depth and rejects unresolved, missing,
        // forward, self, and out-of-tree parents. No semantic ACCEPT is required.
        var placed = ordered.Select(heading =>
        {
            if (!proposed.TryGetValue(heading.Alias, out var parent)) return heading;
            var valid = parent is "ROOT" or "NONE" or "UNRESOLVED" ||
                byAlias.TryGetValue(parent, out var parentHeading) && parentHeading.SourceOrdinal < heading.SourceOrdinal;
            if (!valid)
            {
                statusByAlias[heading.Alias] = "CSharpGraphValidationRejected";
                failures.Add("HIERARCHY_GRAPH:INVALID_PARENT:" + heading.Alias);
                return heading with { RelationHints = heading.RelationHints.Where(hint => !hint.StartsWith("parent-node:", StringComparison.Ordinal)).ToArray() };
            }
            return heading with { RelationHints = heading.RelationHints.Where(hint => !hint.StartsWith("parent-node:", StringComparison.Ordinal))
                .Append("parent-node:" + parent).ToArray() };
        }).ToArray();

        var requested = ordered.Select(h => h.SourceId).ToHashSet(StringComparer.Ordinal);
        var result = Observe(placed, requested, ordered.Length == 0 ? "placement-not-required" : "placement-proposed",
            proposed.Count, failures.Count == 0 ? null : "CSharpGraphValidation");
        var hierarchy = HierarchyResolver.DeriveHierarchyFromModelRelations(placed).ToDictionary(h => h.SourceId, StringComparer.Ordinal);
        var decisions = result.Observation.Decisions.Select(decision => decision with
        {
            ProposedParentAlias = proposed.GetValueOrDefault(decision.Alias),
            VerificationStatus = "NOT_RUN",
            ResolutionBasis = statusByAlias.GetValueOrDefault(decision.Alias) switch
            {
                "CSharpGraphValidationRejected" => "csharp-graph-validation-rejected",
                "UPSTREAM_PROPOSAL" => "upstream-parent-proposal",
                "MODEL_PROPOSAL" => hierarchy[decision.SourceId].Resolution == HierarchyResolver.Unresolved
                    ? "model-proposal-unresolved-or-invalid" : "model-parent-proposal",
                _ => "no-parent-proposal"
            }
        }).ToArray();
        var unresolved = decisions.Where(d => d.Resolution == HierarchyResolver.Unresolved).ToArray();
        var sourceTrace = lines.Select(line =>
        {
            source.AliasStates.TryGetValue(line.Alias, out var state);
            ownerByAlias.TryGetValue(line.Alias, out var owner);
            return new HeadingPlacementSourceObservation(line.Alias, line.SourceId, state?.InitialSelection, state?.FinalSelection,
                state?.VerificationMembership, parts.Contains(line.Alias) ? "BOUND" : state?.BindingStatus ?? "NOT_BOUND",
                owner is not null && hierarchy.TryGetValue(owner, out var resolved) ? resolved.Resolution : "not-bound");
        }).ToArray();
        var observation = result.Observation with
        {
            Contract = placer.Contract,
            LogicalCalls = proposal.LogicalCalls,
            Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            VerifiedCount = 0,
            RepairCount = 0,
            EdgeRepairs = [],
            GraphConflicts = [],
            GraphRepairAliases = [],
            GraphUnresolvedAliases = [],
            Failures = failures.ToArray(),
            Calls = proposal.Calls,
            SourceTrace = sourceTrace,
            ModelEvidence = proposal.ModelEvidence,
            UnresolvedSummary = new(
                unresolved.Count(d => d.ResolutionBasis != "ancestor-chain-unresolved"),
                unresolved.Count(d => d.ResolutionBasis == "ancestor-chain-unresolved"),
                0,
                unresolved.Count(d => d.ResolutionBasis == "no-parent-proposal"),
                unresolved.Count(d => d.ResolutionBasis == "model-proposal-unresolved-or-invalid"),
                0,
                unresolved.Count(d => d.ResolutionBasis == "csharp-graph-validation-rejected")),
            Decisions = decisions
        };
        return result with { Observation = observation };
    }

    internal static PlacementResult Observe(
        IReadOnlyList<CanonicalSemanticBoundHeading> headings,
        IReadOnlySet<string> requested,
        string status,
        int responseEntryCount = 0,
        string? failureClass = null)
    {
        var hierarchy = HierarchyResolver.DeriveHierarchyFromModelRelations(headings)
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var decisions = headings.Where(item => requested.Contains(item.SourceId))
            .GroupBy(item => item.SourceId, StringComparer.Ordinal).Select(group => group.First()).Select(item =>
            new HeadingPlacementDecisionObservation(item.SourceId, item.Alias, hierarchy[item.SourceId].Resolution)).ToArray();
        var placed = decisions.Count(item => item.Resolution is HierarchyResolver.ResolvedParent or HierarchyResolver.ResolvedRoot);
        var outside = decisions.Count(item => item.Resolution == HierarchyResolver.OutOfHierarchy);
        var unresolved = decisions.Count(item => item.Resolution == HierarchyResolver.Unresolved);
        if (status == "placement-accepted" && placed + outside == 0) status = "placement-unresolved";
        var requestedCount = status == "placement-not-requested" ? 0 : requested.Count;
        return new(headings, new(status, requestedCount, responseEntryCount, placed, outside, unresolved, failureClass, decisions));
    }
}
