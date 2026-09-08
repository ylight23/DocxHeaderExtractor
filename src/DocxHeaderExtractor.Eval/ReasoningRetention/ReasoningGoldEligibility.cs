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

/// <summary>Single authority gate for exact Gold scoring. Semantic totals never pass this gate.</summary>
public static class ReasoningGoldEligibilityEvaluator
{
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

    private static bool IsRoleEvaluable(string? role) =>
        !string.IsNullOrWhiteSpace(role) && !string.Equals(role, "heading", StringComparison.OrdinalIgnoreCase);

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
