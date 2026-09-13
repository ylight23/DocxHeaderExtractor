using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public sealed record HdsaIdentityCandidatePair(
    [property: JsonPropertyName("pairId")] string PairId,
    [property: JsonPropertyName("left")] string Left,
    [property: JsonPropertyName("right")] string Right);

public sealed record HdsaIdentityRoleNodeInput(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("documentOrder")] int DocumentOrder,
    [property: JsonPropertyName("structuralRole")] string StructuralRole,
    [property: JsonPropertyName("outlineBearing")] bool OutlineBearing);

public sealed record HdsaIdentityCandidateDiscoveryRequest(
    [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
    [property: JsonPropertyName("nodes")] IReadOnlyList<HdsaIdentityRoleNodeInput> Nodes,
    [property: JsonPropertyName("pairUniverse")] IReadOnlyList<HdsaIdentityCandidatePair> PairUniverse,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

public sealed record HdsaIdentityCandidateDiscoveryResponse(
    [property: JsonPropertyName("candidatePairIds")] IReadOnlyList<string> CandidatePairIds);

public sealed record HdsaIdentityCandidateDiscoveryValidation(
    bool Accepted,
    string? RejectionReason,
    IReadOnlyList<string> CandidatePairIds,
    int MaxCandidatePairCount);

public sealed record HdsaIdentityPairVerificationRequest(
    [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
    [property: JsonPropertyName("nodes")] IReadOnlyList<HdsaIdentityRoleNodeInput> Nodes,
    [property: JsonPropertyName("targetPair")] HdsaIdentityCandidatePair TargetPair,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

public sealed record HdsaIdentityPairVerificationResponse(
    [property: JsonPropertyName("pairId")] string PairId,
    [property: JsonPropertyName("left")] string Left,
    [property: JsonPropertyName("right")] string Right,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("direction")] string Direction);

public sealed record HdsaIdentityPairVerificationValidation(
    bool Accepted,
    string? RejectionReason,
    HdsaIdentityPairVerificationResponse? Response);

public static class HdsaGlobalIdentityRetrieveVerifyContract
{
    public const string Version = "hdsa-global-identity-retrieve-verify-v1";

    public static object DiscoverySchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            candidatePairIds = new
            {
                type = "array",
                items = new { type = "string", minLength = 1 },
            },
        },
        required = new[] { "candidatePairIds" },
    };

    public static object VerificationSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            pairId = new { type = "string", minLength = 1 },
            left = new { type = "string", minLength = 1 },
            right = new { type = "string", minLength = 1 },
            relation = new
            {
                type = "string",
                @enum = new[] { "CONTINUATION_OF", "SAME_SEMANTIC_REPEAT", "DISTINCT", "UNRESOLVED" },
            },
            direction = new
            {
                type = "string",
                @enum = new[] { "RIGHT_TO_LEFT", "NONE" },
            },
        },
        required = new[] { "pairId", "left", "right", "relation", "direction" },
    };

    public static HdsaIdentityCandidateDiscoveryResponse ParseDiscovery(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        RequireOnly(root, "candidatePairIds");
        var element = root.GetProperty("candidatePairIds");
        if (element.ValueKind != JsonValueKind.Array)
            throw new FormatException("HDSA_IDENTITY_DISCOVERY_IDS_NOT_ARRAY");
        var ids = element.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new FormatException("HDSA_IDENTITY_DISCOVERY_PAIR_ID_INVALID");
            return item.GetString()!;
        }).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new FormatException("HDSA_IDENTITY_DISCOVERY_DUPLICATE_PAIR_ID");
        return new(ids);
    }

    public static HdsaIdentityCandidateDiscoveryValidation ValidateDiscovery(
        HdsaIdentityCandidateDiscoveryRequest request,
        HdsaIdentityCandidateDiscoveryResponse response)
    {
        if (request.Nodes.Count == 0)
            return new(false, "EMPTY_NODE_UNIVERSE", [], 0);
        var max = Math.Max(1, (request.PairUniverse.Count + request.Nodes.Count - 1) / request.Nodes.Count);
        if (request.GoldDerivedInput)
            return new(false, "GOLD_DERIVED_INPUT", [], max);
        if (response.CandidatePairIds.Count > max)
            return new(false, "CANDIDATE_DISCOVERY_NOT_SPARSE", [], max);
        var known = request.PairUniverse.Select(item => item.PairId)
            .ToHashSet(StringComparer.Ordinal);
        if (response.CandidatePairIds.Any(item => !known.Contains(item)))
            return new(false, "UNKNOWN_PAIR_ID", [], max);
        if (response.CandidatePairIds.Distinct(StringComparer.Ordinal).Count() != response.CandidatePairIds.Count)
            return new(false, "DUPLICATE_PAIR_ID", [], max);
        return new(true, null, response.CandidatePairIds.Order(StringComparer.Ordinal).ToArray(), max);
    }

    public static HdsaIdentityPairVerificationResponse ParseVerification(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        RequireOnly(root, "pairId", "left", "right", "relation", "direction");
        return new(
            RequiredString(root, "pairId"),
            RequiredString(root, "left"),
            RequiredString(root, "right"),
            RequiredString(root, "relation"),
            RequiredString(root, "direction"));
    }

    public static HdsaIdentityPairVerificationValidation ValidateVerification(
        HdsaIdentityPairVerificationRequest request,
        HdsaIdentityPairVerificationResponse response)
    {
        if (request.GoldDerivedInput)
            return new(false, "GOLD_DERIVED_INPUT", null);
        var target = request.TargetPair;
        if (!string.Equals(response.PairId, target.PairId, StringComparison.Ordinal) ||
            !string.Equals(response.Left, target.Left, StringComparison.Ordinal) ||
            !string.Equals(response.Right, target.Right, StringComparison.Ordinal))
            return new(false, "TARGET_PAIR_MISMATCH", null);
        if (!HdsaGlobalIdentityRelationsV2.All.Contains(response.Relation))
            return new(false, "RELATION_INVALID", null);
        if (response.Relation == "CONTINUATION_OF" && response.Direction != "RIGHT_TO_LEFT")
            return new(false, "CONTINUATION_DIRECTION_INVALID", null);
        if (response.Relation != "CONTINUATION_OF" && response.Direction != "NONE")
            return new(false, "NON_CONTINUATION_DIRECTION_INVALID", null);
        return new(true, null, response);
    }

    private static string RequiredString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new FormatException("HDSA_IDENTITY_VERIFY_" + property.ToUpperInvariant() + "_MISSING");

    private static void RequireOnly(JsonElement root, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!set.Contains(property.Name))
                throw new FormatException("HDSA_IDENTITY_RETRIEVE_VERIFY_EXTRA_PROPERTY:" + property.Name);
        foreach (var property in allowed)
            if (!root.TryGetProperty(property, out _))
                throw new FormatException("HDSA_IDENTITY_RETRIEVE_VERIFY_REQUIRED_PROPERTY:" + property);
    }
}
