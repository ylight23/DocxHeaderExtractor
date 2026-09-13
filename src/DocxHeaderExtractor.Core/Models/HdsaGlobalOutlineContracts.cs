using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>One source-backed node in the whole-outline request.</summary>
public sealed record HdsaGlobalOutlineNodeContext(
    [property: JsonPropertyName("semanticNodeId")] string SemanticNodeId,
    [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("documentOrder")] int DocumentOrder,
    [property: JsonPropertyName("localContext")] string? LocalContext = null,
    [property: JsonPropertyName("styleLayoutEvidence")] string? StyleLayoutEvidence = null,
    [property: JsonPropertyName("containerEvidence")] string? ContainerEvidence = null);

/// <summary>Single whole-document hierarchy request. Gold and level are not inputs.</summary>
public sealed record HdsaGlobalOutlineHierarchyRequest(
    [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
    [property: JsonPropertyName("nodes")] IReadOnlyList<HdsaGlobalOutlineNodeContext> Nodes,
    [property: JsonPropertyName("acceptedIdentityRelations")] IReadOnlyList<HdsaSemanticIdentityRelation> AcceptedIdentityRelations,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

/// <summary>One model proposal in the whole-outline response. proposedLevel is diagnostic only.</summary>
public sealed record HdsaGlobalOutlineNodeProposal(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("parent")] string Parent,
    [property: JsonPropertyName("proposedLevel")] int? ProposedLevel,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record HdsaGlobalOutlineHierarchyProposal(
    [property: JsonPropertyName("nodes")] IReadOnlyList<HdsaGlobalOutlineNodeProposal> Nodes);

public sealed record HdsaGlobalOutlineValidation(
    bool Accepted,
    string? RejectionReason,
    IReadOnlyList<HdsaRelationProposal> ParentRelations,
    HdsaGraphValidationResult GraphValidation,
    HdsaTree Tree,
    IReadOnlyList<HdsaGlobalOutlineNodeProposal> NormalizedNodes);

/// <summary>Strict JSON contract for the one-call whole-outline shadow probe.</summary>
public static class HdsaGlobalOutlineHierarchyContract
{
    public const string Root = "ROOT";
    public const string Unresolved = "UNRESOLVED";

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
                        parent = new { type = "string", minLength = 1 },
                        proposedLevel = new { type = new[] { "integer", "null" }, minimum = 1 },
                        confidence = new { type = "string", minLength = 1 },
                        reason = new { type = "string", minLength = 1 },
                    },
                    required = new[] { "nodeId", "parent", "proposedLevel", "confidence", "reason" },
                },
            },
        },
        required = new[] { "nodes" },
    };

    public static HdsaGlobalOutlineHierarchyProposal Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("HDSA_GLOBAL_OUTLINE_RESPONSE_NOT_OBJECT");
        RequireOnly(root, "nodes");
        var array = root.GetProperty("nodes");
        if (array.ValueKind != JsonValueKind.Array) throw new FormatException("HDSA_GLOBAL_OUTLINE_NODES_NOT_ARRAY");
        var nodes = array.EnumerateArray().Select(ParseNode).ToArray();
        return new(nodes);
    }

    public static HdsaGlobalOutlineValidation Validate(
        HdsaGlobalOutlineHierarchyRequest request,
        HdsaGlobalOutlineHierarchyProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        var known = request.Nodes.Select(item => item.SemanticNodeId).ToHashSet(StringComparer.Ordinal);
        if (request.GoldDerivedInput) return Invalid("GOLD_DERIVED_INPUT", known);
        if (proposal.Nodes.Count != request.Nodes.Count) return Invalid("NODE_COUNT_MISMATCH", known);
        if (proposal.Nodes.GroupBy(item => item.NodeId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return Invalid("DUPLICATE_NODE_ID", known);
        if (!proposal.Nodes.All(item => known.Contains(item.NodeId))) return Invalid("UNKNOWN_NODE_ID", known);
        if (proposal.Nodes.Any(item => item.ProposedLevel is <= 0)) return Invalid("INVALID_PROPOSED_LEVEL", known);
        if (proposal.Nodes.Any(item => !string.Equals(item.Parent, Root, StringComparison.Ordinal) &&
                                       !string.Equals(item.Parent, Unresolved, StringComparison.Ordinal) &&
                                       !known.Contains(item.Parent)))
            return Invalid("UNKNOWN_PARENT_ID", known);
        if (proposal.Nodes.Any(item => !string.Equals(item.Parent, Root, StringComparison.Ordinal) &&
                                       !string.Equals(item.Parent, Unresolved, StringComparison.Ordinal) &&
                                       string.Equals(item.Parent, item.NodeId, StringComparison.Ordinal)))
            return Invalid("SELF_PARENT", known);

        var relations = proposal.Nodes
            .Where(item => !string.Equals(item.Parent, Root, StringComparison.Ordinal) &&
                           !string.Equals(item.Parent, Unresolved, StringComparison.Ordinal))
            .Select(item => new HdsaRelationProposal(item.Parent, item.NodeId, HdsaRelationType.ParentOf))
            .ToArray();
        var normalized = HdsaRelationNormalizer.Normalize(known, relations);
        var graph = HdsaGraphValidator.Validate(known, normalized);
        var tree = HdsaTreeConstructor.Build(request.Nodes.Select(item => item.SemanticNodeId), graph);
        if (!graph.IsValid || !tree.IsValid)
            return new(false, graph.Errors.Concat(tree.Errors).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault() ?? "INVALID_TREE",
                relations, graph, tree, proposal.Nodes);
        return new(true, null, relations, graph, tree, proposal.Nodes);
    }

    private static HdsaGlobalOutlineValidation Invalid(string reason, IReadOnlySet<string> known)
    {
        var graph = new HdsaGraphValidationResult([], [], [reason]);
        return new(false, reason, [], graph, new([], [reason]), []);
    }

    private static HdsaGlobalOutlineNodeProposal ParseNode(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new FormatException("HDSA_GLOBAL_OUTLINE_NODE_NOT_OBJECT");
        RequireOnly(item, "nodeId", "parent", "proposedLevel", "confidence", "reason");
        var nodeId = RequiredString(item, "nodeId");
        var parent = RequiredString(item, "parent");
        var levelElement = item.GetProperty("proposedLevel");
        int? level = levelElement.ValueKind == JsonValueKind.Null
            ? null
            : levelElement.ValueKind == JsonValueKind.Number && levelElement.TryGetInt32(out var value) && value > 0
                ? value
                : throw new FormatException("HDSA_GLOBAL_OUTLINE_PROPOSED_LEVEL_INVALID");
        return new(nodeId, parent, level, RequiredString(item, "confidence"), RequiredString(item, "reason"));
    }

    private static string RequiredString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException("HDSA_GLOBAL_OUTLINE_" + property.ToUpperInvariant() + "_MISSING");

    private static void RequireOnly(JsonElement root, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!set.Contains(property.Name)) throw new FormatException("HDSA_GLOBAL_OUTLINE_EXTRA_PROPERTY:" + property.Name);
        foreach (var property in allowed)
            if (!root.TryGetProperty(property, out _)) throw new FormatException("HDSA_GLOBAL_OUTLINE_REQUIRED_PROPERTY:" + property);
    }
}
