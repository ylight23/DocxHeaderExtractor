using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Relations used by the relation-first HDSA construct phase.</summary>
public enum HdsaRelationType
{
    [JsonStringEnumMemberName("PARENT_OF")]
    ParentOf,

    [JsonStringEnumMemberName("SIBLING_OF")]
    SiblingOf,

    [JsonStringEnumMemberName("SAME_PARENT")]
    SameParent,

    [JsonStringEnumMemberName("CONTINUATION_OF")]
    ContinuationOf,

    [JsonStringEnumMemberName("SAME_SEMANTIC_NODE")]
    SameSemanticNode,

    [JsonStringEnumMemberName("PRECEDES")]
    Precedes,

    [JsonStringEnumMemberName("FOLLOWS")]
    Follows,
}

public enum HdsaRelationSpace
{
    Order,
    Structural,
    SemanticIdentity,
    Derived,
}

public static class HdsaRelationSemantics
{
    public static HdsaRelationSpace Space(HdsaRelationType relation) => relation switch
    {
        HdsaRelationType.Precedes or HdsaRelationType.Follows => HdsaRelationSpace.Order,
        HdsaRelationType.ParentOf => HdsaRelationSpace.Structural,
        HdsaRelationType.ContinuationOf or HdsaRelationType.SameSemanticNode => HdsaRelationSpace.SemanticIdentity,
        HdsaRelationType.SiblingOf or HdsaRelationType.SameParent => HdsaRelationSpace.Derived,
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
    };

    public static bool DeterminesTreeDepth(HdsaRelationType relation) => relation == HdsaRelationType.ParentOf;
}

/// <summary>Untrusted relation output. IDs must already be source-backed occurrence identities.</summary>
public sealed record HdsaRelationProposal(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("relation")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HdsaRelationType Relation);

/// <summary>Canonical relation after deterministic normalization.</summary>
public sealed record HdsaRelation(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("relation")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HdsaRelationType Relation);

public sealed record HdsaRelationRejection(
    [property: JsonPropertyName("proposal")] HdsaRelationProposal Proposal,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record HdsaRelationNormalizationResult(
    IReadOnlyList<HdsaRelation> Relations,
    IReadOnlyList<HdsaRelationRejection> Rejected,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Canonicalizes relation representation without resolving its meaning. Symmetric relations get
/// one endpoint order; duplicate proposals are collapsed; no semantic winner is selected.
/// </summary>
public static class HdsaRelationNormalizer
{
    private static readonly ISet<HdsaRelationType> SymmetricRelations =
        new HashSet<HdsaRelationType>
        {
            HdsaRelationType.SiblingOf,
            HdsaRelationType.SameParent,
            HdsaRelationType.SameSemanticNode,
        };

    public static HdsaRelationNormalizationResult Normalize(
        IEnumerable<string> occurrenceIds,
        IEnumerable<HdsaRelationProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(occurrenceIds);
        ArgumentNullException.ThrowIfNull(proposals);

        var known = occurrenceIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var rejected = new List<HdsaRelationRejection>();
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var canonical = new Dictionary<(string From, string To, HdsaRelationType Relation), HdsaRelation>();

        foreach (var proposal in proposals)
        {
            if (string.IsNullOrWhiteSpace(proposal.From) || string.IsNullOrWhiteSpace(proposal.To))
            {
                Reject(proposal, "EMPTY_ENDPOINT", rejected, errors);
                continue;
            }
            if (!known.Contains(proposal.From) || !known.Contains(proposal.To))
            {
                Reject(proposal, "UNKNOWN_OCCURRENCE", rejected, errors);
                continue;
            }
            if (string.Equals(proposal.From, proposal.To, StringComparison.Ordinal))
            {
                Reject(proposal, "SELF_RELATION", rejected, errors);
                continue;
            }

            var (from, to) = CanonicalEndpoints(proposal.From, proposal.To, proposal.Relation);
            canonical.TryAdd((from, to, proposal.Relation), new HdsaRelation(from, to, proposal.Relation));
        }

        return new(
            canonical.Values
                .OrderBy(item => item.Relation)
                .ThenBy(item => item.From, StringComparer.Ordinal)
                .ThenBy(item => item.To, StringComparer.Ordinal)
                .ToArray(),
            rejected,
            errors.Order(StringComparer.Ordinal).ToArray());
    }

    private static (string From, string To) CanonicalEndpoints(string from, string to, HdsaRelationType relation) =>
        SymmetricRelations.Contains(relation) && string.CompareOrdinal(from, to) > 0
            ? (to, from)
            : (from, to);

    private static void Reject(
        HdsaRelationProposal proposal,
        string reason,
        ICollection<HdsaRelationRejection> rejected,
        ISet<string> errors)
    {
        rejected.Add(new(proposal, reason));
        errors.Add(reason);
    }
}

public sealed record HdsaGraphValidationResult(
    IReadOnlyList<HdsaRelation> AcceptedRelations,
    IReadOnlyList<HdsaRelationRejection> RejectedRelations,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates a normalized relation graph. Parent edges are the only edges that affect tree depth.
/// A child with multiple parents and every parent-cycle are rejected fail-closed; no tie-breaker
/// uses role, style, numbering, or a proposed level.
/// </summary>
public static class HdsaGraphValidator
{
    public static HdsaGraphValidationResult Validate(
        IEnumerable<string> occurrenceIds,
        HdsaRelationNormalizationResult normalized)
    {
        ArgumentNullException.ThrowIfNull(occurrenceIds);
        ArgumentNullException.ThrowIfNull(normalized);

        var known = occurrenceIds.ToHashSet(StringComparer.Ordinal);
        var accepted = normalized.Relations.ToList();
        var rejected = normalized.Rejected.ToList();
        var errors = new HashSet<string>(normalized.Errors, StringComparer.Ordinal);

        var parentEdges = accepted.Where(item => item.Relation == HdsaRelationType.ParentOf).ToArray();
        foreach (var group in parentEdges.GroupBy(item => item.To, StringComparer.Ordinal))
        {
            var distinctParents = group.Select(item => item.From).Distinct(StringComparer.Ordinal).ToArray();
            if (distinctParents.Length <= 1) continue;
            foreach (var edge in group)
            {
                accepted.Remove(edge);
                rejected.Add(new(new(edge.From, edge.To, edge.Relation), "MULTIPLE_PARENTS"));
            }
            errors.Add("MULTIPLE_PARENTS");
        }

        var parentByChild = accepted
            .Where(item => item.Relation == HdsaRelationType.ParentOf)
            .ToDictionary(item => item.To, item => item.From, StringComparer.Ordinal);
        var cycleEdges = FindCycleEdges(parentByChild);
        foreach (var edge in accepted.Where(item => item.Relation == HdsaRelationType.ParentOf && cycleEdges.Contains((item.From, item.To))).ToArray())
        {
            accepted.Remove(edge);
            rejected.Add(new(new(edge.From, edge.To, edge.Relation), "PARENT_CYCLE"));
        }
        if (cycleEdges.Count > 0) errors.Add("PARENT_CYCLE");

        foreach (var relation in accepted)
        {
            if (!known.Contains(relation.From) || !known.Contains(relation.To))
                throw new InvalidOperationException("HDSA_NORMALIZER_ENDPOINT_INVARIANT_BROKEN");
        }

        return new(
            accepted.OrderBy(item => item.Relation)
                .ThenBy(item => item.From, StringComparer.Ordinal)
                .ThenBy(item => item.To, StringComparer.Ordinal)
                .ToArray(),
            rejected,
            errors.Order(StringComparer.Ordinal).ToArray());
    }

    private static HashSet<(string From, string To)> FindCycleEdges(IReadOnlyDictionary<string, string> parentByChild)
    {
        var cycleEdges = new HashSet<(string From, string To)>();
        foreach (var start in parentByChild.Keys)
        {
            var path = new List<string>();
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            var current = start;
            while (parentByChild.TryGetValue(current, out var parent))
            {
                if (indexById.TryGetValue(current, out var cycleStart))
                {
                    var cycle = path.Skip(cycleStart).Append(current).ToArray();
                    for (var index = 0; index + 1 < cycle.Length; index++)
                        cycleEdges.Add((cycle[index + 1], cycle[index]));
                    break;
                }
                indexById[current] = path.Count;
                path.Add(current);
                current = parent;
            }
        }
        return cycleEdges;
    }
}

public sealed record HdsaTreeNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("level")] int Level);

public sealed record HdsaTree(
    IReadOnlyList<HdsaTreeNode> Nodes,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Builds a tree only from validated PARENT_OF relations; level is derived from depth.</summary>
public static class HdsaTreeConstructor
{
    public static HdsaTree Build(
        IEnumerable<string> occurrenceIds,
        HdsaGraphValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(occurrenceIds);
        ArgumentNullException.ThrowIfNull(validation);
        // The caller supplies source/document order. Do not sort by semantic label or ID here:
        // relation construction must preserve parser-owned occurrence order.
        var ids = occurrenceIds.Distinct(StringComparer.Ordinal).ToArray();
        if (!validation.IsValid) return new([], validation.Errors);

        var parentByChild = validation.AcceptedRelations
            .Where(item => item.Relation == HdsaRelationType.ParentOf)
            .ToDictionary(item => item.To, item => item.From, StringComparer.Ordinal);
        var nodes = ids.Select(id =>
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            var level = 1;
            var current = id;
            while (parentByChild.TryGetValue(current, out var parent))
            {
                if (!path.Add(current)) return new HdsaTreeNode(id, parentByChild.GetValueOrDefault(id), 0);
                level++;
                current = parent;
            }
            return new HdsaTreeNode(id, parentByChild.GetValueOrDefault(id), level);
        }).ToArray();
        if (nodes.Any(node => node.Level == 0)) return new([], ["PARENT_CYCLE"]);
        return new(new ReadOnlyCollection<HdsaTreeNode>(nodes), []);
    }
}
