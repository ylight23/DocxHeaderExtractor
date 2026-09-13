using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Evidence visible to a relation reasoner. These fields are evidence, never tree rules.</summary>
public sealed record HdsaRelationEvidence(
    [property: JsonPropertyName("documentOrder")] string DocumentOrder,
    [property: JsonPropertyName("numbering")] string? Numbering,
    [property: JsonPropertyName("styles")] string? Styles,
    [property: JsonPropertyName("semanticRoles")] string? SemanticRoles,
    [property: JsonPropertyName("localContext")] string? LocalContext,
    [property: JsonPropertyName("globalContext")] string? GlobalContext = null);

public sealed record HdsaParentUniverseEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("documentOrder")] int DocumentOrder,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("semanticRole")] string? SemanticRole = null);

/// <summary>
/// Request for one model relation decision. CandidateParents is an attention shortlist only;
/// the validator still accepts any source-backed parent so the shortlist cannot become a recall gate.
/// </summary>
public sealed record HdsaRelationReasoningRequest(
    [property: JsonPropertyName("child")] string Child,
    [property: JsonPropertyName("candidateParents")] IReadOnlyList<string> CandidateParents,
    [property: JsonPropertyName("authoritativeParentUniverse")] IReadOnlyList<HdsaParentUniverseEntry> AuthoritativeParentUniverse,
    [property: JsonPropertyName("evidence")] HdsaRelationEvidence Evidence,
    [property: JsonPropertyName("candidateParentsAreAttentionOnly")] bool CandidateParentsAreAttentionOnly = true);

public enum HdsaParentDecision
{
    [JsonStringEnumMemberName("SELECT_PARENT")]
    SelectParent,

    [JsonStringEnumMemberName("ROOT")]
    Root,

    [JsonStringEnumMemberName("UNRESOLVED")]
    Unresolved,
}

/// <summary>Model output for relation reasoning. It contains no level and no invented coordinates.</summary>
public sealed record HdsaRelationReasoningDecision(
    [property: JsonPropertyName("child")] string Child,
    [property: JsonPropertyName("decision")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HdsaParentDecision Decision,
    [property: JsonPropertyName("parent")] string? Parent = null);

public sealed record HdsaRelationDecisionValidation(
    bool Accepted,
    string? RejectionReason,
    bool ParentWasOutsideAttentionHints);

public static class HdsaRelationReasoningContract
{
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            child = new { type = "string", minLength = 1 },
            decision = new { type = "string", @enum = new[] { "SELECT_PARENT", "ROOT", "UNRESOLVED" } },
            parent = new { type = new[] { "string", "null" }, minLength = 1 },
        },
        required = new[] { "child", "decision", "parent" },
    };

    public static HdsaRelationDecisionValidation Validate(
        HdsaRelationReasoningRequest request,
        HdsaRelationReasoningDecision decision,
        IReadOnlySet<string> knownOccurrenceIds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(knownOccurrenceIds);

        if (!knownOccurrenceIds.Contains(decision.Child))
            return new(false, "UNKNOWN_CHILD", false);
        if (!string.Equals(request.Child, decision.Child, StringComparison.Ordinal))
            return new(false, "CHILD_MISMATCH", false);

        switch (decision.Decision)
        {
            case HdsaParentDecision.Root:
            case HdsaParentDecision.Unresolved:
                return decision.Parent is null
                    ? new(true, null, false)
                    : new(false, "PARENT_NOT_ALLOWED_FOR_DECISION", false);
            case HdsaParentDecision.SelectParent:
                if (decision.Parent is null) return new(false, "PARENT_REQUIRED", false);
                if (string.Equals(decision.Child, decision.Parent, StringComparison.Ordinal))
                    return new(false, "SELF_PARENT", false);
                if (!knownOccurrenceIds.Contains(decision.Parent))
                    return new(false, "UNKNOWN_PARENT", false);
                return new(true, null, !request.CandidateParents.Contains(decision.Parent, StringComparer.Ordinal));
            default:
                return new(false, "UNSUPPORTED_DECISION", false);
        }
    }

    public static HdsaRelationReasoningDecision Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("HDSA_RELATION_RESPONSE_NOT_OBJECT");
        var allowed = new HashSet<string>(["child", "decision", "parent"], StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw new FormatException("HDSA_RELATION_RESPONSE_EXTRA_PROPERTY");

        var child = RequiredString(root, "child");
        var decision = RequiredString(root, "decision") switch
        {
            "SELECT_PARENT" => HdsaParentDecision.SelectParent,
            "ROOT" => HdsaParentDecision.Root,
            "UNRESOLVED" => HdsaParentDecision.Unresolved,
            _ => throw new FormatException("HDSA_RELATION_RESPONSE_DECISION_INVALID"),
        };
        if (!root.TryGetProperty("parent", out var parent) ||
            (parent.ValueKind != JsonValueKind.Null && parent.ValueKind != JsonValueKind.String))
            throw new FormatException("HDSA_RELATION_RESPONSE_PARENT_INVALID");
        return new(child, decision, parent.ValueKind == JsonValueKind.Null ? null : parent.GetString());
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException($"HDSA_RELATION_RESPONSE_{name.ToUpperInvariant()}_MISSING");
}

/// <summary>
/// Deterministic primary parent shortlist. It is deliberately an attention artifact, not an
/// eligibility set; a later fallback/global pass may return any known preceding occurrence.
/// </summary>
public static class HdsaParentAttentionCandidates
{
    public static IReadOnlyList<string> PrimaryPreceding(
        IReadOnlyList<string> documentOrderOccurrenceIds,
        string child,
        int window = 8)
    {
        ArgumentNullException.ThrowIfNull(documentOrderOccurrenceIds);
        if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
        var childIndex = -1;
        for (var index = 0; index < documentOrderOccurrenceIds.Count; index++)
        {
            if (!string.Equals(documentOrderOccurrenceIds[index], child, StringComparison.Ordinal)) continue;
            childIndex = index;
            break;
        }
        if (childIndex < 0) return [];
        return documentOrderOccurrenceIds
            .Take(childIndex)
            .TakeLast(window)
            .Reverse()
            .ToArray();
    }

    public static bool IsAttentionOnly(HdsaRelationReasoningRequest request) =>
        request.CandidateParentsAreAttentionOnly;
}
