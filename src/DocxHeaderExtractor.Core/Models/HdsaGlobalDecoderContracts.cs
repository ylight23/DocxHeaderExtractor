using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>One parser/model supplied preference for one possible parent edge.</summary>
/// <remarks>
/// A null parent is the explicit virtual ROOT outcome. The decoder never creates a candidate
/// that is absent from this collection. Score is only a relative preference; it is not treated as
/// a calibrated probability.
/// </remarks>
public sealed record HdsaParentEdgeScore(
    [property: JsonPropertyName("childSemanticNodeId")] string ChildSemanticNodeId,
    [property: JsonPropertyName("parentSemanticNodeId")] string? ParentSemanticNodeId,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("rank")] int Rank = 0,
    [property: JsonPropertyName("provenance")] string Provenance = "",
    [property: JsonPropertyName("requestSha256")] string RequestSha256 = "",
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

/// <summary>
/// Contract envelope for a future rank-producing reasoner. The current decoder consumes only the
/// source-backed edge scores; this request type deliberately contains no Gold or level field.
/// </summary>
public sealed record HdsaParentEdgeScoreRequest(
    [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
    [property: JsonPropertyName("childSemanticNodeId")] string ChildSemanticNodeId,
    [property: JsonPropertyName("candidateParentSemanticNodeIds")] IReadOnlyList<string?> CandidateParentSemanticNodeIds,
    [property: JsonPropertyName("evidence")] HdsaRelationEvidence Evidence,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

public sealed record HdsaGlobalDecoderRejection(
    [property: JsonPropertyName("candidate")] HdsaParentEdgeScore Candidate,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record HdsaGlobalDecoderResult(
    bool IsValid,
    IReadOnlyList<HdsaParentEdgeScore> SelectedEdges,
    IReadOnlyList<HdsaGlobalDecoderRejection> RejectedCandidates,
    IReadOnlyList<string> Errors,
    double TotalScore,
    HdsaGraphValidationResult GraphValidation,
    HdsaTree Tree)
{
    public bool GoldUsed => false;
}

/// <summary>
/// Global shadow decoder for parent-edge preferences. It computes a maximum-weight arborescence
/// rooted at a virtual ROOT using Chu-Liu/Edmonds contraction. It does not change the candidate
/// universe, infer a missing ROOT edge, or use role/style/level/Gold as a tie-breaker.
/// </summary>
public static class HdsaGlobalParentDecoder
{
    public const string VirtualRootId = "__HDSA_VIRTUAL_ROOT__";

    public static HdsaGlobalDecoderResult Decode(
        IEnumerable<string> semanticNodeIds,
        IEnumerable<HdsaParentEdgeScore> candidates)
    {
        ArgumentNullException.ThrowIfNull(semanticNodeIds);
        ArgumentNullException.ThrowIfNull(candidates);

        var nodeIds = semanticNodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var known = nodeIds.ToHashSet(StringComparer.Ordinal);
        var rejected = new List<HdsaGlobalDecoderRejection>();
        var valid = new List<HdsaParentEdgeScore>();
        foreach (var candidate in candidates)
        {
            var reason = ValidateCandidate(candidate, known);
            if (reason is null) valid.Add(candidate);
            else rejected.Add(new(candidate, reason));
        }

        var deduplicated = valid
            .GroupBy(item => (item.ParentSemanticNodeId, item.ChildSemanticNodeId), EdgeKeyComparer.Instance)
            .Select(group => group.OrderByDescending(item => item.Score)
                .ThenBy(item => item.Rank)
                .ThenBy(item => ParentSortKey(item.ParentSemanticNodeId), StringComparer.Ordinal)
                .ThenBy(item => item.Provenance, StringComparer.Ordinal)
                .ThenBy(item => item.RequestSha256, StringComparer.Ordinal)
                .First())
            .ToArray();

        if (nodeIds.Length == 0)
            return EmptyResult(rejected, []);

        var internalEdges = deduplicated
            .Select((item, index) => new DecoderEdge(
                item.ParentSemanticNodeId ?? VirtualRootId,
                item.ChildSemanticNodeId,
                item.Score,
                index,
                item))
            .ToArray();
        var allNodes = nodeIds.Append(VirtualRootId).ToHashSet(StringComparer.Ordinal);
        var solved = Solve(allNodes, internalEdges, VirtualRootId);
        if (!solved.IsValid)
            return InvalidResult(nodeIds, rejected, solved.Errors);

        var selected = solved.Edges
            .Select(edge => edge.Original)
            .OrderBy(item => item.ChildSemanticNodeId, StringComparer.Ordinal)
            .ThenBy(item => ParentSortKey(item.ParentSemanticNodeId), StringComparer.Ordinal)
            .ThenByDescending(item => item.Score)
            .ToArray();
        if (selected.Length != nodeIds.Length || selected.Select(item => item.ChildSemanticNodeId).Distinct(StringComparer.Ordinal).Count() != nodeIds.Length)
            return InvalidResult(nodeIds, rejected, ["INCOMPLETE_ARBORESCENCE"]);

        var relations = selected
            .Where(item => item.ParentSemanticNodeId is not null)
            .Select(item => new HdsaRelationProposal(item.ParentSemanticNodeId!, item.ChildSemanticNodeId, HdsaRelationType.ParentOf));
        var normalized = HdsaRelationNormalizer.Normalize(nodeIds, relations);
        var graph = HdsaGraphValidator.Validate(nodeIds, normalized);
        var tree = HdsaTreeConstructor.Build(nodeIds, graph);
        if (!graph.IsValid || !tree.IsValid)
            return new(false, selected, rejected, graph.Errors.Concat(tree.Errors).Distinct(StringComparer.Ordinal).Order().ToArray(),
                selected.Sum(item => item.Score), graph, tree);

        return new(true, selected, rejected, [], selected.Sum(item => item.Score), graph, tree);
    }

    private static HdsaGlobalDecoderResult EmptyResult(
        IReadOnlyList<HdsaGlobalDecoderRejection> rejected,
        IReadOnlyList<string> errors) =>
        new(errors.Count == 0, [], rejected, errors, 0, new([], [], errors), new([], errors));

    private static HdsaGlobalDecoderResult InvalidResult(
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<HdsaGlobalDecoderRejection> rejected,
        IReadOnlyList<string> errors)
    {
        var graph = new HdsaGraphValidationResult([], [], errors);
        return new(false, [], rejected, errors, 0, graph, new([], errors));
    }

    private static string? ValidateCandidate(HdsaParentEdgeScore candidate, IReadOnlySet<string> known)
    {
        if (string.IsNullOrWhiteSpace(candidate.ChildSemanticNodeId)) return "EMPTY_CHILD";
        if (!known.Contains(candidate.ChildSemanticNodeId)) return "UNKNOWN_CHILD";
        if (candidate.ParentSemanticNodeId is not null && !known.Contains(candidate.ParentSemanticNodeId)) return "UNKNOWN_PARENT";
        if (string.Equals(candidate.ParentSemanticNodeId, candidate.ChildSemanticNodeId, StringComparison.Ordinal)) return "SELF_PARENT";
        if (!double.IsFinite(candidate.Score)) return "NON_FINITE_SCORE";
        if (candidate.GoldDerivedInput) return "GOLD_DERIVED_INPUT";
        return null;
    }

    private static string ParentSortKey(string? parent) => parent ?? VirtualRootId;

    private static SolveResult Solve(
        IReadOnlySet<string> nodes,
        IReadOnlyList<DecoderEdge> edges,
        string root)
    {
        var incoming = new Dictionary<string, DecoderEdge>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.Equals(node, root, StringComparison.Ordinal)) continue;
            var choices = edges.Where(edge => string.Equals(edge.To, node, StringComparison.Ordinal));
            var best = choices.OrderByDescending(edge => edge.Weight)
                .ThenBy(edge => edge.Original.Rank)
                .ThenBy(edge => ParentSortKey(edge.Original.ParentSemanticNodeId), StringComparer.Ordinal)
                .ThenBy(edge => edge.Original.Provenance, StringComparer.Ordinal)
                .ThenBy(edge => edge.Original.RequestSha256, StringComparer.Ordinal)
                .ThenBy(edge => edge.StableOrdinal)
                .FirstOrDefault();
            if (best is null) return new(false, [], ["MISSING_INCOMING_EDGE:" + node]);
            incoming[node] = best;
        }

        var cycle = FindCycle(nodes, incoming, root);
        if (cycle is null)
            return new(true, incoming.Values.ToArray(), []);

        var cycleId = string.Join("|", cycle.Order(StringComparer.Ordinal));
        var contractedRoot = root;
        var contractedNodes = nodes.Where(node => !cycle.Contains(node, StringComparer.Ordinal))
            .Append(cycleId)
            .ToHashSet(StringComparer.Ordinal);
        var contractedEdges = new List<DecoderEdge>();
        foreach (var edge in edges)
        {
            var fromInside = cycle.Contains(edge.From, StringComparer.Ordinal);
            var toInside = cycle.Contains(edge.To, StringComparer.Ordinal);
            if (fromInside && toInside) continue;
            var from = fromInside ? cycleId : edge.From;
            var to = toInside ? cycleId : edge.To;
            var weight = toInside ? edge.Weight - incoming[edge.To].Weight : edge.Weight;
            contractedEdges.Add(new(from, to, weight, edge.StableOrdinal, edge.Original, edge));
        }

        var contracted = Solve(contractedNodes, contractedEdges, contractedRoot);
        if (!contracted.IsValid) return contracted;

        var expanded = new List<DecoderEdge>();
        string? enteredNode = null;
        foreach (var edge in contracted.Edges)
        {
            if (string.Equals(edge.To, cycleId, StringComparison.Ordinal))
            {
                var previous = edge.Previous!;
                enteredNode = previous.To;
                expanded.Add(previous);
            }
            else if (string.Equals(edge.From, cycleId, StringComparison.Ordinal))
            {
                expanded.Add(edge.Previous!);
            }
            else
            {
                expanded.Add(edge.Previous ?? edge);
            }
        }

        if (enteredNode is null) return new(false, [], ["CYCLE_HAS_NO_ROOT_ENTRY"]);
        expanded.AddRange(cycle.Where(node => !string.Equals(node, enteredNode, StringComparison.Ordinal)).Select(node => incoming[node]));
        return new(true, expanded, []);
    }

    private static HashSet<string>? FindCycle(
        IEnumerable<string> nodes,
        IReadOnlyDictionary<string, DecoderEdge> incoming,
        string root)
    {
        foreach (var start in nodes.Where(node => !string.Equals(node, root, StringComparison.Ordinal)))
        {
            var path = new List<string>();
            var position = new Dictionary<string, int>(StringComparer.Ordinal);
            var current = start;
            while (!string.Equals(current, root, StringComparison.Ordinal) && incoming.TryGetValue(current, out var edge))
            {
                if (position.TryGetValue(current, out var cycleStart))
                    return path.Skip(cycleStart).ToHashSet(StringComparer.Ordinal);
                position[current] = path.Count;
                path.Add(current);
                current = edge.From;
            }
        }
        return null;
    }

    private sealed record DecoderEdge(
        string From,
        string To,
        double Weight,
        int StableOrdinal,
        HdsaParentEdgeScore Original,
        DecoderEdge? Previous = null);

    private sealed record SolveResult(bool IsValid, IReadOnlyList<DecoderEdge> Edges, IReadOnlyList<string> Errors);

    private sealed class EdgeKeyComparer : IEqualityComparer<(string? Parent, string Child)>
    {
        public static readonly EdgeKeyComparer Instance = new();
        public bool Equals((string? Parent, string Child) x, (string? Parent, string Child) y) =>
            string.Equals(x.Parent, y.Parent, StringComparison.Ordinal) && string.Equals(x.Child, y.Child, StringComparison.Ordinal);
        public int GetHashCode((string? Parent, string Child) obj) =>
            HashCode.Combine(obj.Parent is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.Parent), StringComparer.Ordinal.GetHashCode(obj.Child));
    }
}
