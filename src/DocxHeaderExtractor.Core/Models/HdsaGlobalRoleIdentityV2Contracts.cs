using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public sealed record HdsaGlobalRoleNodeInput(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("documentOrder")] int DocumentOrder);

public sealed record HdsaGlobalRoleClassificationRequest(
    [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
    [property: JsonPropertyName("nodes")] IReadOnlyList<HdsaGlobalRoleNodeInput> Nodes,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

public sealed record HdsaGlobalRoleNodeProposal(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("structuralRole")] string StructuralRole,
    [property: JsonPropertyName("outlineBearing")] bool OutlineBearing,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record HdsaGlobalRoleClassificationProposal(
    [property: JsonPropertyName("nodes")] IReadOnlyList<HdsaGlobalRoleNodeProposal> Nodes);

public sealed record HdsaGlobalRoleClassificationValidation(
    bool Accepted, string? RejectionReason,
    IReadOnlyList<HdsaGlobalRoleNodeProposal> Nodes);

public sealed record HdsaGlobalIdentityPairInput(
    [property: JsonPropertyName("pairId")] string PairId,
    [property: JsonPropertyName("left")] HdsaGlobalRoleNodeInput Left,
    [property: JsonPropertyName("right")] HdsaGlobalRoleNodeInput Right,
    [property: JsonPropertyName("leftStructuralRole")] string LeftStructuralRole,
    [property: JsonPropertyName("rightStructuralRole")] string RightStructuralRole,
    [property: JsonPropertyName("leftOutlineBearing")] bool LeftOutlineBearing,
    [property: JsonPropertyName("rightOutlineBearing")] bool RightOutlineBearing);

public sealed record HdsaGlobalIdentityRelationRequest(
    [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
    [property: JsonPropertyName("pairs")] IReadOnlyList<HdsaGlobalIdentityPairInput> Pairs,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

public sealed record HdsaGlobalIdentityRelationProposal(
    [property: JsonPropertyName("pairId")] string PairId,
    [property: JsonPropertyName("left")] string Left,
    [property: JsonPropertyName("right")] string Right,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record HdsaGlobalIdentityRelationResponse(
    [property: JsonPropertyName("relations")] IReadOnlyList<HdsaGlobalIdentityRelationProposal> Relations);

public sealed record HdsaGlobalIdentityRelationValidation(
    bool Accepted, string? RejectionReason,
    IReadOnlyList<HdsaGlobalIdentityRelationProposal> Relations,
    IReadOnlyList<IReadOnlyList<string>> AcceptedComponents);

public static class HdsaGlobalIdentityRelationsV2
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "SAME_SEMANTIC_REPEAT",
        "CONTINUATION_OF",
        "DISTINCT",
        "UNRESOLVED",
    };
}

public static class HdsaGlobalRoleClassificationContract
{
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            nodes = new
            {
                type = "array", minItems = 1,
                items = new
                {
                    type = "object", additionalProperties = false,
                    properties = new
                    {
                        nodeId = new { type = "string", minLength = 1 },
                        structuralRole = new { type = "string", @enum = HdsaGlobalSemanticNormalizationRoles.All.Order() },
                        outlineBearing = new { type = "boolean" },
                        confidence = new { type = "string", minLength = 1 },
                        reason = new { type = "string", minLength = 1 },
                    },
                    required = new[] { "nodeId", "structuralRole", "outlineBearing", "confidence", "reason" },
                },
            },
        },
        required = new[] { "nodes" },
    };

    public static HdsaGlobalRoleClassificationProposal Parse(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        RequireOnly(root, "nodes");
        var nodes = root.GetProperty("nodes").EnumerateArray().Select(item =>
        {
            RequireOnly(item, "nodeId", "structuralRole", "outlineBearing", "confidence", "reason");
            var bearing = item.GetProperty("outlineBearing");
            return new HdsaGlobalRoleNodeProposal(
                RequiredString(item, "nodeId"), RequiredString(item, "structuralRole"),
                bearing.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new FormatException("HDSA_GLOBAL_ROLE_OUTLINE_BEARING_INVALID"),
                },
                RequiredString(item, "confidence"), RequiredString(item, "reason"));
        }).ToArray();
        return new(nodes);
    }

    public static HdsaGlobalRoleClassificationValidation Validate(
        IReadOnlySet<string> knownNodeIds, HdsaGlobalRoleClassificationProposal proposal)
    {
        if (proposal.Nodes.Count != knownNodeIds.Count) return new(false, "NODE_COUNT_MISMATCH", []);
        if (proposal.Nodes.GroupBy(item => item.NodeId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return new(false, "DUPLICATE_NODE_ID", []);
        if (proposal.Nodes.Any(item => !knownNodeIds.Contains(item.NodeId)))
            return new(false, "UNKNOWN_NODE_ID", []);
        if (proposal.Nodes.Any(item => !HdsaGlobalSemanticNormalizationRoles.All.Contains(item.StructuralRole)))
            return new(false, "STRUCTURAL_ROLE_INVALID", []);
        return new(true, null, proposal.Nodes);
    }

    private static string RequiredString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new FormatException("HDSA_GLOBAL_ROLE_" + property.ToUpperInvariant() + "_MISSING");

    private static void RequireOnly(JsonElement root, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!set.Contains(property.Name)) throw new FormatException("HDSA_GLOBAL_ROLE_EXTRA_PROPERTY:" + property.Name);
        foreach (var property in allowed)
            if (!root.TryGetProperty(property, out _))
                throw new FormatException("HDSA_GLOBAL_ROLE_REQUIRED_PROPERTY:" + property);
    }
}

public static class HdsaGlobalIdentityRelationContract
{
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            relations = new
            {
                type = "array", minItems = 1,
                items = new
                {
                    type = "object", additionalProperties = false,
                    properties = new
                    {
                        pairId = new { type = "string", minLength = 1 },
                        left = new { type = "string", minLength = 1 },
                        right = new { type = "string", minLength = 1 },
                        relation = new { type = "string", @enum = HdsaGlobalIdentityRelationsV2.All.Order() },
                        direction = new { type = "string", minLength = 1 },
                        confidence = new { type = "string", minLength = 1 },
                        reason = new { type = "string", minLength = 1 },
                    },
                    required = new[] { "pairId", "left", "right", "relation", "direction", "confidence", "reason" },
                },
            },
        },
        required = new[] { "relations" },
    };

    public static HdsaGlobalIdentityRelationResponse Parse(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        RequireOnly(root, "relations");
        var relations = root.GetProperty("relations").EnumerateArray().Select(item =>
        {
            RequireOnly(item, "pairId", "left", "right", "relation", "direction", "confidence", "reason");
            return new HdsaGlobalIdentityRelationProposal(
                RequiredString(item, "pairId"), RequiredString(item, "left"),
                RequiredString(item, "right"), RequiredString(item, "relation"),
                RequiredString(item, "direction"), RequiredString(item, "confidence"),
                RequiredString(item, "reason"));
        }).ToArray();
        return new(relations);
    }

    public static HdsaGlobalIdentityRelationValidation Validate(
        HdsaGlobalIdentityRelationRequest request, HdsaGlobalIdentityRelationResponse response)
    {
        if (request.GoldDerivedInput) return new(false, "GOLD_DERIVED_INPUT", [], []);
        var pairs = request.Pairs.ToDictionary(item => item.PairId, StringComparer.Ordinal);
        if (response.Relations.Count != pairs.Count) return new(false, "PAIR_COUNT_MISMATCH", [], []);
        if (response.Relations.GroupBy(item => item.PairId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return new(false, "DUPLICATE_PAIR_ID", [], []);
        if (response.Relations.Any(item => !pairs.ContainsKey(item.PairId)))
            return new(false, "UNKNOWN_PAIR_ID", [], []);
        foreach (var relation in response.Relations)
        {
            var pair = pairs[relation.PairId];
            if (relation.Left != pair.Left.NodeId || relation.Right != pair.Right.NodeId)
                return new(false, "PAIR_ENDPOINT_MISMATCH", [], []);
            if (!HdsaGlobalIdentityRelationsV2.All.Contains(relation.Relation))
                return new(false, "RELATION_INVALID", [], []);
            if (relation.Relation == "CONTINUATION_OF" && relation.Direction != "RIGHT_TO_LEFT")
                return new(false, "CONTINUATION_DIRECTION_INVALID", [], []);
        }

        var positive = response.Relations.Where(item => item.Relation is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF").ToArray();
        var distinct = response.Relations.Where(item => item.Relation == "DISTINCT")
            .Select(item => (item.Left, item.Right)).ToHashSet();
        var nodes = request.Pairs.SelectMany(item => new[] { item.Left.NodeId, item.Right.NodeId })
            .ToHashSet(StringComparer.Ordinal);
        var dsu = new DisjointSet(nodes);
        foreach (var relation in positive) dsu.Union(relation.Left, relation.Right);
        foreach (var group in nodes.GroupBy(dsu.Find, StringComparer.Ordinal))
        {
            var members = group.Order(StringComparer.Ordinal).ToArray();
            if (members.Length > 1 && distinct.Any(pair =>
                members.Contains(pair.Left, StringComparer.Ordinal) &&
                members.Contains(pair.Right, StringComparer.Ordinal)))
                return new(false, "POSITIVE_DISTINCT_CONTRADICTION", [], []);
        }
        var components = nodes.GroupBy(dsu.Find, StringComparer.Ordinal)
            .Select(group => (IReadOnlyList<string>)group.Order(StringComparer.Ordinal).ToArray())
            .OrderBy(group => group[0], StringComparer.Ordinal).ToArray();
        return new(true, null, response.Relations, components);
    }

    private sealed class DisjointSet
    {
        private readonly Dictionary<string, string> _parent;
        public DisjointSet(IEnumerable<string> items) => _parent = items.ToDictionary(item => item, StringComparer.Ordinal);
        public string Find(string item)
        {
            if (_parent[item] == item) return item;
            return _parent[item] = Find(_parent[item]);
        }
        public void Union(string left, string right)
        {
            left = Find(left); right = Find(right);
            if (!string.Equals(left, right, StringComparison.Ordinal)) _parent[right] = left;
        }
    }

    private static string RequiredString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new FormatException("HDSA_GLOBAL_IDENTITY_" + property.ToUpperInvariant() + "_MISSING");

    private static void RequireOnly(JsonElement root, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!set.Contains(property.Name)) throw new FormatException("HDSA_GLOBAL_IDENTITY_EXTRA_PROPERTY:" + property.Name);
        foreach (var property in allowed)
            if (!root.TryGetProperty(property, out _))
                throw new FormatException("HDSA_GLOBAL_IDENTITY_REQUIRED_PROPERTY:" + property);
    }
}
