using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Eval.Accuracy99;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;
using System.Security.Cryptography;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public sealed record ReasoningGoldEligibility(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("eligible")] bool Eligible,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("occurrenceCount")] int OccurrenceCount,
    [property: JsonPropertyName("roleEvaluable")] bool RoleEvaluable,
    [property: JsonPropertyName("hierarchyEvaluable")] bool HierarchyEvaluable);

public sealed record ReasoningGoldEligibilityMetadata(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("eligible")] bool Eligible,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("occurrenceCount")] int OccurrenceCount,
    [property: JsonPropertyName("occurrenceEvaluable")] bool OccurrenceEvaluable,
    [property: JsonPropertyName("characterSpanEvaluable")] bool CharacterSpanEvaluable);

/// <summary>Single authority gate for exact Gold scoring. Semantic totals never pass this gate.</summary>
public static class ReasoningGoldEligibilityEvaluator
{
    /// <summary>Eligibility-only gate for cohort selection. It reads authority metadata and
    /// lineage hashes, but never materializes or inspects an approved Gold heading row. The
    /// exact occurrence artifact is loaded only after a prediction freeze by the scorer.</summary>
    public static ReasoningGoldEligibilityMetadata EvaluateMetadataOnly(string repoRoot, string documentId)
    {
        var v4Path = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-v4", $"{documentId}.strict-gold-v4.json");
        var occurrencePath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{documentId}.occurrence-gold-v1.json");
        if (!File.Exists(v4Path) || !File.Exists(occurrencePath)) return MetadataBlocked(documentId, "CANONICAL_ARTIFACT_MISSING");
        using var v4 = JsonDocument.Parse(File.ReadAllText(v4Path));
        var root = v4.RootElement;
        var v4Eligible = root.GetProperty("goldStatus").GetString() == A99StrictGoldAuthorityRules.StrictGold &&
            root.GetProperty("finalAuthority").GetString() == A99StrictGoldAuthorityRules.UserFinalAuthority &&
            root.GetProperty("userFinalApproval").GetBoolean() &&
            root.GetProperty("reviewedEntireDocument").GetBoolean() &&
            root.GetProperty("headingSetExhaustive").GetBoolean() &&
            !root.GetProperty("unresolvedSemanticUncertainty").GetBoolean() &&
            root.GetProperty("exactApprovedHeadingListMaterialized").GetBoolean();
        if (!v4Eligible) return MetadataBlocked(documentId, "STRICT_GOLD_AUTHORITY_INCOMPLETE");

        using var occurrence = JsonDocument.Parse(File.ReadAllText(occurrencePath));
        var occurrenceRoot = occurrence.RootElement;
        var capabilities = occurrenceRoot.GetProperty("capabilities");
        var occurrenceEvaluable = capabilities.GetProperty("occurrenceEvaluable").GetBoolean();
        var characterSpanEvaluable = capabilities.GetProperty("characterSpanEvaluable").GetBoolean();
        var materialized = occurrenceRoot.GetProperty("exactApprovedHeadingListMaterialized").GetBoolean() &&
            occurrenceRoot.GetProperty("status").GetString() == "PASS" &&
            occurrenceRoot.GetProperty("materializedOccurrenceCount").GetInt32() == occurrenceRoot.GetProperty("semanticHeadingTotal").GetInt32() &&
            occurrenceEvaluable && characterSpanEvaluable;
        if (!materialized) return MetadataBlocked(documentId, "OCCURRENCE_AUTHORITY_METADATA_INCOMPLETE");
        if (!StrictGoldOccurrenceMaterializer.SourcePaths.TryGetValue(documentId, out var sourceRelative))
            return MetadataBlocked(documentId, "SOURCE_LINEAGE_UNKNOWN");
        var sourcePath = Path.Combine(repoRoot, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
        var sourceSha = occurrenceRoot.GetProperty("sourceSha256").GetString();
        if (!File.Exists(sourcePath) || string.IsNullOrWhiteSpace(sourceSha) || !string.Equals(sourceSha, Sha256(sourcePath), StringComparison.OrdinalIgnoreCase))
            return MetadataBlocked(documentId, "SOURCE_HASH_LINEAGE_MISMATCH");
        return new ReasoningGoldEligibilityMetadata(documentId, true, "EXACT_APPROVED_SOURCE_BACKED_GOLD_METADATA", occurrenceRoot.GetProperty("semanticHeadingTotal").GetInt32(), true, true);
    }

    public static ReasoningGoldEligibility Evaluate(string repoRoot, string documentId)
    {
        var v4Path = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-v4", $"{documentId}.strict-gold-v4.json");
        var occurrencePath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{documentId}.occurrence-gold-v1.json");
        if (!File.Exists(v4Path) || !File.Exists(occurrencePath)) return Blocked(documentId, "CANONICAL_ARTIFACT_MISSING");
        using var v4 = JsonDocument.Parse(File.ReadAllText(v4Path));
        var root = v4.RootElement;
        var authority = root.GetProperty("finalAuthority").GetString();
        var eligible = root.GetProperty("goldStatus").GetString() == A99StrictGoldAuthorityRules.StrictGold &&
            authority == A99StrictGoldAuthorityRules.UserFinalAuthority &&
            root.GetProperty("userFinalApproval").GetBoolean() &&
            root.GetProperty("headingSetExhaustive").GetBoolean() &&
            root.GetProperty("exactApprovedHeadingListMaterialized").GetBoolean();
        if (!eligible) return Blocked(documentId, "SEMANTIC_TOTAL_OR_UNAPPROVED_GOLD");

        var artifact = JsonSerializer.Deserialize<StrictGoldOccurrenceArtifact>(File.ReadAllText(occurrencePath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (artifact is null || artifact.Status != "PASS" || !artifact.ExactApprovedHeadingListMaterialized ||
            artifact.Bindings.Count != artifact.SemanticHeadingTotal || artifact.SourceSha256.Length == 0 ||
            artifact.Bindings.Any(binding => !binding.ExactRawSubstringVerified || binding.SourceReferenceSha256.Length == 0))
            return Blocked(documentId, "OCCURRENCE_AUTHORITY_INCOMPLETE");
        if (!StrictGoldOccurrenceMaterializer.SourcePaths.TryGetValue(documentId, out var sourceRelative))
            return Blocked(documentId, "SOURCE_LINEAGE_UNKNOWN");
        var sourcePath = Path.Combine(repoRoot, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath) || !string.Equals(artifact.SourceSha256, Sha256(sourcePath), StringComparison.OrdinalIgnoreCase))
            return Blocked(documentId, "SOURCE_HASH_LINEAGE_MISMATCH");
        var roles = artifact.Bindings.Select(x => x.SemanticRole).ToArray();
        return new ReasoningGoldEligibility(documentId, true, "EXACT_APPROVED_SOURCE_BACKED_GOLD", artifact.Bindings.Count,
            roles.All(IsRoleEvaluable), artifact.Bindings.All(x => x.ParentHeadingOccurrenceId is not null));
    }

    private static ReasoningGoldEligibility Blocked(string documentId, string reason) =>
        new(documentId, false, reason, 0, false, false);

    private static ReasoningGoldEligibilityMetadata MetadataBlocked(string documentId, string reason) =>
        new(documentId, false, reason, 0, false, false);

    private static bool IsRoleEvaluable(string? role) =>
        !string.IsNullOrWhiteSpace(role) && !string.Equals(role, "heading", StringComparison.OrdinalIgnoreCase);

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
