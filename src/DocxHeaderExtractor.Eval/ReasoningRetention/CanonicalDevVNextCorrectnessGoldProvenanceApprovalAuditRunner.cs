using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Audits whether the raw-source-faithful DOC-0116 binding successor has an explicit,
/// successor-specific user approval. This runner never promotes Gold authority.
/// </summary>
public static class CanonicalDevVNextCorrectnessGoldProvenanceApprovalAuditRunner
{
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1";
    private const string AuthorityRoot = "artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0116";
    private const string RepairRoot = ExecutionRoot + "/gold-scoring-v1/gold-binding-repair-v1";
    private const string OutputRoot = ExecutionRoot + "/gold-scoring-v1/gold-provenance-approval-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var successorPath = Full(repoRoot, RepairRoot + "/binding-repair-successor.v1.json");
        var successorManifestPath = Full(repoRoot, RepairRoot + "/manifest.json");
        var authorityPath = Full(repoRoot, AuthorityRoot + "/authority-freeze-manifest.json");
        if (new[] { successorPath, successorManifestPath, authorityPath }.Any(path => !File.Exists(path)))
            return Blocked("PROVENANCE_INPUT_MISSING");

        using var successor = JsonDocument.Parse(await File.ReadAllTextAsync(successorPath, ct));
        using var successorManifest = JsonDocument.Parse(await File.ReadAllTextAsync(successorManifestPath, ct));
        using var authority = JsonDocument.Parse(await File.ReadAllTextAsync(authorityPath, ct));

        var successorRoot = successor.RootElement;
        var manifestRoot = successorManifest.RootElement;
        var authorityRoot = authority.RootElement;
        var sourcePathValue = authorityRoot.GetProperty("sourcePath").GetString()!;
        var sourcePath = Full(repoRoot, sourcePathValue);
        if (!File.Exists(sourcePath)) return Blocked("SOURCE_DOCUMENT_MISSING");

        var sourceSha = Sha256File(sourcePath);
        var successorSha = Sha256File(successorPath);
        var successorManifestSha = manifestRoot.GetProperty("successorSha256").GetString() ?? "";
        var successorSourceSha = successorRoot.GetProperty("sourceSha256").GetString() ?? "";
        var expectedSourceSha = authorityRoot.GetProperty("sourceSha256").GetString() ?? "";
        var rows = successorRoot.GetProperty("rows").EnumerateArray().ToArray();
        var rawFaithful = rows.All(row =>
        {
            if (!row.TryGetProperty("repaired", out var repaired) || repaired.ValueKind == JsonValueKind.Null) return false;
            var sourceText = row.GetProperty("sourceText").GetString() ?? "";
            var start = repaired.GetProperty("start").GetInt32();
            var end = repaired.GetProperty("end").GetInt32();
            var exact = repaired.GetProperty("exactText").GetString() ?? "";
            return start >= 0 && end >= start && end <= sourceText.Length &&
                   string.Equals(sourceText[start..end], exact, StringComparison.Ordinal);
        });

        var oldAuthorityBindingsSha = authorityRoot.GetProperty("artifactHashes")
            .EnumerateArray()
            .FirstOrDefault(item => string.Equals(item.GetProperty("artifact").GetString(), "exactBindings", StringComparison.OrdinalIgnoreCase))
            .GetProperty("sha256").GetString() ?? "";
        var successorSpecificApproval = authorityRoot.TryGetProperty("approvedSuccessorSha256", out var approvedSuccessor) &&
            string.Equals(approvedSuccessor.GetString(), successorSha, StringComparison.OrdinalIgnoreCase);
        var legacyApprovalOnly = authorityRoot.TryGetProperty("explicitUserApproval", out var explicitApproval) && explicitApproval.GetBoolean() &&
            !successorSpecificApproval;

        var gates = new
        {
            sourceShaMatches = string.Equals(sourceSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase),
            successorSourceShaMatches = string.Equals(sourceSha, successorSourceSha, StringComparison.OrdinalIgnoreCase),
            successorManifestShaMatches = string.Equals(successorSha, successorManifestSha, StringComparison.OrdinalIgnoreCase),
            successorBindingCount = rows.Length,
            expectedBindingCount = 120,
            successorBindingCountMatches = rows.Length == 120,
            repairedBindingCount = successorRoot.GetProperty("repairedBindingCount").GetInt32(),
            unresolvedBindingCount = successorRoot.GetProperty("unresolvedBindingCount").GetInt32(),
            allBindingsSourceFaithful = rawFaithful,
            documentIdMatches = string.Equals(successorRoot.GetProperty("documentId").GetString(), "DOC-0116", StringComparison.Ordinal),
            explicitUserApprovalForThisSuccessor = successorSpecificApproval,
            legacyFreezeExplicitApproval = authorityRoot.TryGetProperty("explicitUserApproval", out var oldApproval) && oldApproval.GetBoolean(),
            legacyFreezeBindsOldArtifactOnly = string.Equals(oldAuthorityBindingsSha,
                successorRoot.GetProperty("existingGoldBindingsSha256").GetString(), StringComparison.OrdinalIgnoreCase),
            successorPromotionStillForbidden = string.Equals(manifestRoot.GetProperty("authorityPromotion").GetString(),
                "FORBIDDEN_UNTIL_REVIEW_AND_COMPATIBILITY_GATES_PASS", StringComparison.Ordinal),
        };

        var provenancePass = gates.sourceShaMatches && gates.successorSourceShaMatches && gates.successorManifestShaMatches &&
            gates.successorBindingCountMatches && gates.repairedBindingCount == 120 && gates.unresolvedBindingCount == 0 &&
            gates.allBindingsSourceFaithful && gates.documentIdMatches && gates.explicitUserApprovalForThisSuccessor;
        var outputDir = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(outputDir);
        var payload = new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-provenance-approval-audit-v1",
            documentId = "DOC-0116",
            status = provenancePass ? "GOLD_PROVENANCE_PASS" : "GOLD_PROVENANCE_UNRESOLVED",
            authorityPromotion = provenancePass ? "ELIGIBLE_FOR_SEPARATE_PROMOTION_REVIEW" : "BLOCKED",
            officialScoring = false,
            gates,
            approvalEvidence = successorSpecificApproval ? "SUCCESSOR_SPECIFIC_AUTHORITY_FIELD" : "NONE_FOUND",
            legacyApprovalNotUsed = legacyApprovalOnly,
            sourcePath = sourcePathValue,
            sourceSha256 = sourceSha,
            successorSha256 = successorSha,
            successorManifestSha256 = Sha256File(successorManifestPath),
            oldAuthorityManifestSha256 = Sha256File(authorityPath),
            oldAuthorityBindingsSha256 = oldAuthorityBindingsSha,
            successorExistingGoldBindingsSha256 = successorRoot.GetProperty("existingGoldBindingsSha256").GetString(),
            predictionRead = false,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 1,
            scoring = false,
            createdAtUtc = DateTimeOffset.UtcNow,
        };
        var jsonPath = Path.Combine(outputDir, "provenance-approval-audit.v1.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine, ct);
        var reportPath = Path.Combine(outputDir, "report.md");
        var report = $"# DOC-0116 Gold provenance approval audit\n\n" +
            $"Status: `{payload.status}`\n\n" +
            "This audit does not promote the repaired successor and does not score prediction.\n\n" +
            $"- Successor SHA: `{successorSha}`\n" +
            $"- Source SHA: `{sourceSha}`\n" +
            $"- Successor rows: **{rows.Length}/120**\n" +
            $"- Source-faithful rows: **{(rawFaithful ? "120/120" : "FAIL")}**\n" +
            $"- Successor-specific user approval: **{(successorSpecificApproval ? "FOUND" : "NOT FOUND")}**\n" +
            $"- Legacy freeze approval reused: **NO**\n" +
            "- Provider calls: **0**\n" +
            "- Prediction read: **false**\n\n" +
            (provenancePass
                ? "All provenance gates passed; authority promotion remains a separate explicit action.\n"
                : "The successor is source-valid, but it has no independent successor-specific user approval artifact. Authority promotion and official scoring remain blocked.\n");
        await File.WriteAllTextAsync(reportPath, report, Encoding.UTF8, ct);
        var manifest = new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-provenance-approval-audit-manifest-v1",
            status = payload.status,
            auditSha256 = Sha256File(jsonPath),
            reportSha256 = Sha256File(reportPath),
            successorSha256 = successorSha,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 1,
            authorityPromotion = payload.authorityPromotion,
            officialScoring = false,
        };
        await File.WriteAllTextAsync(Path.Combine(outputDir, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, ct);

        Console.WriteLine($"STATUS={payload.status}");
        Console.WriteLine($"SUCCESSOR_ROWS={rows.Length}");
        Console.WriteLine($"SOURCE_FAITHFUL={(rawFaithful ? 120 : 0)}/120");
        Console.WriteLine($"SUCCESSOR_APPROVAL={(successorSpecificApproval ? "FOUND" : "NOT_FOUND")}");
        Console.WriteLine("AUTHORITY_PROMOTION=BLOCKED");
        Console.WriteLine("OFFICIAL_SCORING=false");
        Console.WriteLine("PREDICTION_READ=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        return provenancePass ? 0 : 2;
    }

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static int Blocked(string reason)
    {
        Console.WriteLine($"STATUS=BLOCKED:{reason}");
        Console.WriteLine("PROVIDER_CALLS=0");
        return 2;
    }
}
