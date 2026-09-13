using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Sparse model output for identity collapse. An omitted pair is explicitly NO_CLAIM and
/// therefore remains split; the model is never required to assert DISTINCT for every pair.
/// </summary>
public sealed record HdsaSparsePositiveIdentityRelation(
    [property: JsonPropertyName("left")] string Left,
    [property: JsonPropertyName("right")] string Right,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("direction")] string Direction);

public sealed record HdsaSparsePositiveIdentityResponse(
    [property: JsonPropertyName("positiveRelations")]
    IReadOnlyList<HdsaSparsePositiveIdentityRelation> PositiveRelations);

public sealed record HdsaSparsePositiveIdentityValidation(
    bool Accepted,
    string? RejectionReason,
    IReadOnlyList<HdsaSparsePositiveIdentityRelation> PositiveRelations,
    IReadOnlyList<IReadOnlyList<string>> PositiveComponents,
    int NoClaimPairCount);

public static class HdsaGlobalIdentitySparsePositiveContract
{
    public const string Version = "hdsa-global-identity-sparse-positive-v1";

    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            positiveRelations = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        left = new { type = "string", minLength = 1 },
                        right = new { type = "string", minLength = 1 },
                        relation = new
                        {
                            type = "string",
                            @enum = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" },
                        },
                        direction = new { type = "string", @enum = new[] { "RIGHT_TO_LEFT" } },
                    },
                    required = new[] { "left", "right", "relation", "direction" },
                },
            },
        },
        required = new[] { "positiveRelations" },
    };

    public static HdsaSparsePositiveIdentityResponse Parse(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        RequireOnly(root, "positiveRelations");
        var relationsElement = root.GetProperty("positiveRelations");
        if (relationsElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("HDSA_IDENTITY_SPARSE_RELATIONS_NOT_ARRAY");

        var relations = relationsElement.EnumerateArray().Select(item =>
        {
            RequireOnly(item, "left", "right", "relation", "direction");
            return new HdsaSparsePositiveIdentityRelation(
                RequiredString(item, "left"),
                RequiredString(item, "right"),
                RequiredString(item, "relation"),
                RequiredString(item, "direction"));
        }).ToArray();
        return new(relations);
    }

    public static HdsaSparsePositiveIdentityValidation Validate(
        HdsaGlobalIdentityRelationRequest request,
        HdsaSparsePositiveIdentityResponse response)
    {
        var noClaimPairCount = request.Pairs.Count - response.PositiveRelations.Count;
        if (request.GoldDerivedInput)
            return Reject("GOLD_DERIVED_INPUT", noClaimPairCount);

        var pairs = request.Pairs.ToDictionary(
            item => (item.Left.NodeId, item.Right.NodeId), item => item, PairComparer.Instance);
        var knownNodes = request.Pairs.SelectMany(item => new[] { item.Left.NodeId, item.Right.NodeId })
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<(string Left, string Right)>(PairComparer.Instance);
        foreach (var relation in response.PositiveRelations)
        {
            if (!knownNodes.Contains(relation.Left) || !knownNodes.Contains(relation.Right))
                return Reject("UNKNOWN_NODE_ID", noClaimPairCount);
            if (string.Equals(relation.Left, relation.Right, StringComparison.Ordinal))
                return Reject("SELF_RELATION", noClaimPairCount);
            if (!HdsaGlobalIdentityRelationsV2.All.Contains(relation.Relation) ||
                relation.Relation is not ("SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF"))
                return Reject("POSITIVE_RELATION_INVALID", noClaimPairCount);
            if (relation.Relation == "CONTINUATION_OF" && relation.Direction != "RIGHT_TO_LEFT")
                return Reject("CONTINUATION_DIRECTION_INVALID", noClaimPairCount);
            if (!pairs.ContainsKey((relation.Left, relation.Right)))
                return Reject("NONCANONICAL_OR_UNKNOWN_PAIR", noClaimPairCount);
            if (!seen.Add((relation.Left, relation.Right)))
                return Reject("DUPLICATE_POSITIVE_PAIR", noClaimPairCount);
        }

        var continuationParents = response.PositiveRelations
            .Where(item => item.Relation == "CONTINUATION_OF")
            .GroupBy(item => item.Right, StringComparer.Ordinal);
        if (continuationParents.Any(group => group.Count() > 1))
            return Reject("MULTIPLE_CONTINUATION_PARENTS", noClaimPairCount);

        var continuationEdges = response.PositiveRelations
            .Where(item => item.Relation == "CONTINUATION_OF")
            .Select(item => (From: item.Right, To: item.Left))
            .ToArray();
        if (HasCycle(continuationEdges, knownNodes))
            return Reject("CONTINUATION_CYCLE", noClaimPairCount);

        var dsu = new DisjointSet(knownNodes);
        foreach (var relation in response.PositiveRelations)
            dsu.Union(relation.Left, relation.Right);
        var components = knownNodes.GroupBy(dsu.Find, StringComparer.Ordinal)
            .Select(group => (IReadOnlyList<string>)group.Order(StringComparer.Ordinal).ToArray())
            .OrderBy(group => group[0], StringComparer.Ordinal)
            .ToArray();
        return new(true, null, response.PositiveRelations, components,
            request.Pairs.Count - response.PositiveRelations.Count);
    }

    public static HdsaSparsePositiveIdentityResponse FromExhaustive(
        HdsaGlobalIdentityRelationResponse response) =>
        new(response.Relations
            .Where(item => item.Relation is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF")
            .Select(item => new HdsaSparsePositiveIdentityRelation(
                item.Left, item.Right, item.Relation, item.Direction))
            .ToArray());

    private static HdsaSparsePositiveIdentityValidation Reject(string reason, int pairCount) =>
        new(false, reason, [], [], pairCount);

    private static bool HasCycle(
        IReadOnlyList<(string From, string To)> edges, IReadOnlySet<string> nodes)
    {
        var adjacency = edges.GroupBy(item => item.From, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.To).ToArray(),
                StringComparer.Ordinal);
        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        bool Visit(string node)
        {
            state[node] = 1;
            if (adjacency.TryGetValue(node, out var children))
                foreach (var child in children)
                {
                    if (state.TryGetValue(child, out var childState) && childState == 1)
                        return true;
                    if (!state.ContainsKey(child) && Visit(child))
                        return true;
                }
            state[node] = 2;
            return false;
        }

        return nodes.Any(node => !state.ContainsKey(node) && Visit(node));
    }

    private static string RequiredString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new FormatException("HDSA_IDENTITY_SPARSE_" + property.ToUpperInvariant() + "_MISSING");

    private static void RequireOnly(JsonElement root, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!set.Contains(property.Name))
                throw new FormatException("HDSA_IDENTITY_SPARSE_EXTRA_PROPERTY:" + property.Name);
        foreach (var property in allowed)
            if (!root.TryGetProperty(property, out _))
                throw new FormatException("HDSA_IDENTITY_SPARSE_REQUIRED_PROPERTY:" + property);
    }

    private sealed class PairComparer : IEqualityComparer<(string Left, string Right)>
    {
        public static readonly PairComparer Instance = new();
        public bool Equals((string Left, string Right) x, (string Left, string Right) y) =>
            StringComparer.Ordinal.Equals(x.Left, y.Left) && StringComparer.Ordinal.Equals(x.Right, y.Right);
        public int GetHashCode((string Left, string Right) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Left), StringComparer.Ordinal.GetHashCode(obj.Right));
    }

    private sealed class DisjointSet
    {
        private readonly Dictionary<string, string> _parent;
        public DisjointSet(IEnumerable<string> nodes) =>
            _parent = nodes.ToDictionary(item => item, StringComparer.Ordinal);
        public string Find(string node) =>
            _parent[node] == node ? node : _parent[node] = Find(_parent[node]);
        public void Union(string left, string right)
        {
            left = Find(left);
            right = Find(right);
            if (!StringComparer.Ordinal.Equals(left, right))
                _parent[right] = left;
        }
    }
}
