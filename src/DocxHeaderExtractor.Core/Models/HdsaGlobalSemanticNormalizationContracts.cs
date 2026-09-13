using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public static class HdsaGlobalSemanticNormalizationRoles
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "DOCUMENT_FRAMING",
        "OUTLINE_ROOT",
        "OUTLINE_HEADING",
        "SECTION_HEADING",
        "CONTENT_LABEL",
        "REPEAT",
        "CONTINUATION",
        "NON_OUTLINE_LABEL",
        "UNRESOLVED",
    };

    public static readonly IReadOnlySet<string> OutlineBearing = new HashSet<string>(StringComparer.Ordinal)
    {
        "OUTLINE_ROOT",
        "OUTLINE_HEADING",
        "SECTION_HEADING",
    };
}

public static class HdsaGlobalSemanticNormalizationRelations
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "PRIMARY",
        "REPEAT",
        "CONTINUATION",
        "DISTINCT",
        "UNRESOLVED",
    };
}

public sealed record HdsaGlobalSemanticNormalizationNodeProposal(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("structuralRole")] string StructuralRole,
    [property: JsonPropertyName("outlineBearing")] bool OutlineBearing,
    [property: JsonPropertyName("identityGroup")] string IdentityGroup,
    [property: JsonPropertyName("identityRelation")] string IdentityRelation,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record HdsaGlobalSemanticNormalizationProposal(
    [property: JsonPropertyName("nodes")] IReadOnlyList<HdsaGlobalSemanticNormalizationNodeProposal> Nodes);

public sealed record HdsaGlobalSemanticNormalizationValidation(
    bool Accepted,
    string? RejectionReason,
    IReadOnlyList<HdsaGlobalSemanticNormalizationNodeProposal> NormalizedNodes,
    IReadOnlyList<IReadOnlyList<string>> AcceptedGroups);

public static class HdsaGlobalSemanticNormalizationContract
{
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            nodes = new
            {
                type = "array",
                minItems = 1,
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        nodeId = new { type = "string", minLength = 1 },
                        structuralRole = new { type = "string", @enum = HdsaGlobalSemanticNormalizationRoles.All.Order() },
                        outlineBearing = new { type = "boolean" },
                        identityGroup = new { type = "string", minLength = 1 },
                        identityRelation = new { type = "string", @enum = HdsaGlobalSemanticNormalizationRelations.All.Order() },
                        confidence = new { type = "string", minLength = 1 },
                        reason = new { type = "string", minLength = 1 },
                    },
                    required = new[]
                    {
                        "nodeId", "structuralRole", "outlineBearing", "identityGroup",
                        "identityRelation", "confidence", "reason",
                    },
                },
            },
        },
        required = new[] { "nodes" },
    };

    public static HdsaGlobalSemanticNormalizationProposal Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        RequireOnly(root, "nodes");
        var nodes = root.GetProperty("nodes").EnumerateArray().Select(item =>
        {
            RequireOnly(item, "nodeId", "structuralRole", "outlineBearing", "identityGroup", "identityRelation", "confidence", "reason");
            return new HdsaGlobalSemanticNormalizationNodeProposal(
                RequiredString(item, "nodeId"),
                RequiredString(item, "structuralRole"),
                item.GetProperty("outlineBearing").ValueKind == JsonValueKind.True
                    ? true
                    : item.GetProperty("outlineBearing").ValueKind == JsonValueKind.False
                        ? false
                        : throw new FormatException("HDSA_GLOBAL_NORMALIZATION_OUTLINE_BEARING_INVALID"),
                RequiredString(item, "identityGroup"),
                RequiredString(item, "identityRelation"),
                RequiredString(item, "confidence"),
                RequiredString(item, "reason"));
        }).ToArray();
        return new(nodes);
    }

    public static HdsaGlobalSemanticNormalizationValidation Validate(
        IReadOnlySet<string> knownNodeIds,
        HdsaGlobalSemanticNormalizationProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(knownNodeIds);
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Nodes.Count != knownNodeIds.Count) return Reject("NODE_COUNT_MISMATCH");
        if (proposal.Nodes.GroupBy(item => item.NodeId, StringComparer.Ordinal).Any(item => item.Count() != 1))
            return Reject("DUPLICATE_NODE_ID");
        if (!proposal.Nodes.All(item => knownNodeIds.Contains(item.NodeId))) return Reject("UNKNOWN_NODE_ID");
        if (proposal.Nodes.Any(item => !HdsaGlobalSemanticNormalizationRoles.All.Contains(item.StructuralRole)))
            return Reject("STRUCTURAL_ROLE_INVALID");
        if (proposal.Nodes.Any(item => !HdsaGlobalSemanticNormalizationRelations.All.Contains(item.IdentityRelation)))
            return Reject("IDENTITY_RELATION_INVALID");
        if (proposal.Nodes.Any(item => string.IsNullOrWhiteSpace(item.IdentityGroup)))
            return Reject("IDENTITY_GROUP_MISSING");

        var groups = proposal.Nodes.GroupBy(item => item.IdentityGroup, StringComparer.Ordinal).ToArray();
        var accepted = new List<IReadOnlyList<string>>();
        foreach (var group in groups)
        {
            var members = group.Select(item => item.NodeId).Order(StringComparer.Ordinal).ToArray();
            if (members.Length == 1)
            {
                accepted.Add(members);
                continue;
            }

            // A model-only positive relation is accepted only when every member agrees on a
            // positive identity relation. DISTINCT/UNRESOLVED cannot collapse a group.
            if (group.Any(item => item.IdentityRelation is "DISTINCT" or "UNRESOLVED") ||
                group.Any(item => item.IdentityRelation is not ("REPEAT" or "CONTINUATION")))
                return Reject("AMBIGUOUS_IDENTITY_GROUP");
            if (group.Select(item => item.IdentityRelation).Distinct(StringComparer.Ordinal).Count() != 1)
                return Reject("MIXED_IDENTITY_RELATION_GROUP");
            accepted.Add(members);
        }

        return new(true, null, proposal.Nodes, accepted);
    }

    private static HdsaGlobalSemanticNormalizationValidation Reject(string reason) =>
        new(false, reason, [], []);

    private static string RequiredString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException("HDSA_GLOBAL_NORMALIZATION_" + property.ToUpperInvariant() + "_MISSING");

    private static void RequireOnly(JsonElement root, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!set.Contains(property.Name)) throw new FormatException("HDSA_GLOBAL_NORMALIZATION_EXTRA_PROPERTY:" + property.Name);
        foreach (var property in allowed)
            if (!root.TryGetProperty(property, out _))
                throw new FormatException("HDSA_GLOBAL_NORMALIZATION_REQUIRED_PROPERTY:" + property);
    }
}
