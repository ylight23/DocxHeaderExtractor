using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Conservative disposition for an identity relation before component collapse.</summary>
public enum HdsaSemanticIdentityPromotionAction
{
    [JsonStringEnumMemberName("MODEL_PROPOSED")]
    ModelProposed,

    [JsonStringEnumMemberName("ACCEPTED_FOR_IDENTITY_COLLAPSE")]
    AcceptedForIdentityCollapse,

    [JsonStringEnumMemberName("KEEP_SPLIT")]
    KeepSplit,
}

public sealed record HdsaSemanticIdentityPromotionResult(
    [property: JsonPropertyName("action")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HdsaSemanticIdentityPromotionAction Action,
    [property: JsonPropertyName("collapseAuthorized")] bool CollapseAuthorized,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// Promotion boundary for semantic identity relations. Model-only positive relations remain
/// evidence until an independent promotion mechanism corroborates them; parser-owned positive
/// relations may collapse only when their source evidence is explicit and structurally compatible.
/// DISTINCT, UNRESOLVED, Gold, legacy, and malformed provenance always keep components split.
/// </summary>
public static class HdsaSemanticIdentityPromotionPolicy
{
    public const string Version = "semantic-identity-promotion-v1";

    public static HdsaSemanticIdentityPromotionResult Evaluate(
        HdsaSemanticIdentityInferenceDecision decision,
        string inferenceSource,
        string? parserEvidenceHash,
        bool structuralBoundaryCompatible,
        bool goldUsed = false,
        bool legacyUsed = false)
    {
        if (goldUsed || legacyUsed)
            return KeepSplit("GOLD_OR_LEGACY_PROVENANCE");

        if (!string.Equals(inferenceSource, "MODEL", StringComparison.Ordinal) &&
            !string.Equals(inferenceSource, "PARSER", StringComparison.Ordinal))
            return KeepSplit("INFERENCE_SOURCE_INVALID");

        if (decision is HdsaSemanticIdentityInferenceDecision.Distinct or
            HdsaSemanticIdentityInferenceDecision.Unresolved)
            return KeepSplit("NON_MERGE_DECISION");

        if (decision != HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat &&
            decision != HdsaSemanticIdentityInferenceDecision.ContinuationOf)
            return KeepSplit("IDENTITY_DECISION_INVALID");

        if (string.Equals(inferenceSource, "MODEL", StringComparison.Ordinal))
            return new(HdsaSemanticIdentityPromotionAction.ModelProposed, false, "MODEL_ONLY_RELATION_REQUIRES_CORROBORATION");

        if (string.IsNullOrWhiteSpace(parserEvidenceHash))
            return KeepSplit("PARSER_EVIDENCE_MISSING");
        if (!structuralBoundaryCompatible)
            return KeepSplit("STRUCTURAL_BOUNDARY_INCOMPATIBLE");

        return new(HdsaSemanticIdentityPromotionAction.AcceptedForIdentityCollapse, true, "PARSER_EVIDENCE_ACCEPTED");
    }

    private static HdsaSemanticIdentityPromotionResult KeepSplit(string reason) =>
        new(HdsaSemanticIdentityPromotionAction.KeepSplit, false, reason);
}
