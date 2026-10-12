using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Exact F1 for DIRECT-F1 (D4). Scores a set of predicted SELECT aliases against the approved Gold of one authority. It refuses to score
/// unless the registry names the authority, the registry and the Gold document both record a USER final approval, the Gold file's bytes
/// match the registry's goldSha256, and the claim count equals the approved total. It never creates, edits or self-approves Gold, and it
/// never reads a folder to discover Gold: the registry is the only authority.
/// The run's own evidence must also match: the source SHA and the candidate universe SHA the run was made on must equal the registry's source
/// and the Gold occurrence universe; the run's alias coverage must be complete; and the Gold occurrence and semantic claims must name the
/// same alias set, so the Direct-F1 decision unit (one alias per occurrence) is the Gold unit.
/// F1 = 2TP / (2TP + FP + FN), returned as an exact fraction and as a double for display only.
/// </summary>
internal static class P7F1V6DirectScorer
{
    /// <summary>What the run must show before it can be scored.</summary>
    public sealed record RunEvidence(string SourceSha256, string UniverseSha256, IReadOnlyCollection<string> PredictedSelected,
        IReadOnlyCollection<string> CoverageMissing, IReadOnlyCollection<string> CoverageUnexpected);

    public sealed record Result(bool Scored, string Status, string? Reason, int? TruePositive, int? FalsePositive, int? FalseNegative,
        string? F1Exact, double? F1, IReadOnlyList<string> PredictedOutsideGold);

    static Result Refuse(string reason) => new(false, "REFUSED", reason, null, null, null, null, null, Array.Empty<string>());

    public static Result Score(string repoRoot, string registryRelativePath, string authorityId, RunEvidence run)
    {
        var registryPath = Path.Combine(repoRoot, registryRelativePath);
        if (!File.Exists(registryPath)) return Refuse("REGISTRY_MISSING");
        using var reg = JsonDocument.Parse(File.ReadAllBytes(registryPath));
        var authority = reg.RootElement.GetProperty("authorities").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("authorityId").GetString() == authorityId);
        if (authority.ValueKind != JsonValueKind.Object) return Refuse("NO_APPROVED_GOLD_FOR_AUTHORITY");
        if (authority.GetProperty("approvalAuthority").GetString() != "USER" || !authority.GetProperty("userFinalApproval").GetBoolean())
            return Refuse("REGISTRY_APPROVAL_NOT_USER_FINAL");
        if (!authority.GetProperty("semanticClaimsEvaluable").GetBoolean()) return Refuse("SEMANTIC_CLAIMS_NOT_EVALUABLE");
        if (!authority.GetProperty("occurrenceEvaluable").GetBoolean()) return Refuse("OCCURRENCE_NOT_EVALUABLE");
        if (run.SourceSha256 != authority.GetProperty("sourceSha256").GetString()) return Refuse("SOURCE_SHA_MISMATCH");

        var goldRel = authority.GetProperty("canonicalGoldPath").GetString() ?? "";
        var goldPath = Path.GetFullPath(Path.Combine(repoRoot, goldRel));
        var goldRoot = Path.GetFullPath(Path.Combine(repoRoot, "eval", "a99-closed-loop", "gold-current", "documents")) + Path.DirectorySeparatorChar;
        if (!goldPath.StartsWith(goldRoot, StringComparison.OrdinalIgnoreCase)) return Refuse("GOLD_PATH_OUTSIDE_AUTHORITY_FOLDER");
        if (!File.Exists(goldPath)) return Refuse("GOLD_FILE_MISSING");

        var goldBytes = File.ReadAllBytes(goldPath);
        var sha = Convert.ToHexStringLower(SHA256.HashData(goldBytes));
        if (sha != authority.GetProperty("goldSha256").GetString()) return Refuse("GOLD_SHA_MISMATCH");

        using var gold = JsonDocument.Parse(goldBytes);
        var g = gold.RootElement;
        if (g.GetProperty("authorityId").GetString() != authorityId) return Refuse("GOLD_AUTHORITY_DIFFERS");
        if (!g.GetProperty("approval").GetProperty("userFinalApproval").GetBoolean()) return Refuse("GOLD_APPROVAL_NOT_USER_FINAL");
        var semantic = g.GetProperty("semantic");
        if (!semantic.GetProperty("headingSetExhaustive").GetBoolean()) return Refuse("GOLD_NOT_EXHAUSTIVE");
        var claims = semantic.GetProperty("claims").EnumerateArray().Select(c => c.GetProperty("sourceAlias").GetString()!).ToList();
        if (claims.Count != semantic.GetProperty("approvedSemanticTotal").GetInt32()) return Refuse("CLAIM_COUNT_MISMATCH");
        if (claims.Distinct(StringComparer.Ordinal).Count() != claims.Count) return Refuse("GOLD_DUPLICATE_ALIAS");
        if (claims.Count == 0) return Refuse("GOLD_EMPTY");

        var occurrence = g.GetProperty("occurrence");
        if (!occurrence.GetProperty("occurrenceEvaluable").GetBoolean()) return Refuse("OCCURRENCE_NOT_EVALUABLE");
        if (occurrence.GetProperty("sourceUniverseSha256").GetString() != run.UniverseSha256) return Refuse("UNIVERSE_SHA_MISMATCH");
        var occurrenceAliases = occurrence.GetProperty("claims").EnumerateArray().Select(c => c.GetProperty("sourceAlias").GetString()!).ToList();
        if (occurrenceAliases.Count != occurrenceAliases.Distinct(StringComparer.Ordinal).Count()) return Refuse("GOLD_OCCURRENCE_DUPLICATE_ALIAS");
        if (!occurrenceAliases.ToHashSet(StringComparer.Ordinal).SetEquals(claims)) return Refuse("UNIT_MISMATCH_OCCURRENCE_VS_SEMANTIC");

        if (run.CoverageMissing.Count > 0 || run.CoverageUnexpected.Count > 0) return Refuse("COVERAGE_INCOMPLETE");

        var gold1 = new HashSet<string>(claims, StringComparer.Ordinal);
        var pred = new HashSet<string>(run.PredictedSelected, StringComparer.Ordinal);
        var tp = pred.Count(gold1.Contains);
        var fp = pred.Count - tp;
        var fn = gold1.Count - tp;
        var outside = pred.Where(p => !gold1.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var denom = 2L * tp + fp + fn;
        if (denom == 0) return Refuse("F1_UNDEFINED");
        var num = 2L * tp;
        return new Result(true, "SCORED", null, tp, fp, fn, $"{num}/{denom}", (double)num / denom, outside);
    }
}
