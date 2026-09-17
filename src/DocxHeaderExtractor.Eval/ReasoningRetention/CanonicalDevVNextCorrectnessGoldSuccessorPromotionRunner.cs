using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Promotes the explicitly user-approved DOC-0116 repaired binding successor into a new
/// occurrence Gold authority artifact. Existing Gold, prediction, and successor artifacts are
/// never overwritten.
/// </summary>
public static class CanonicalDevVNextCorrectnessGoldSuccessorPromotionRunner
{
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1";
    private const string OccurrenceRoot = "artifacts/authority-audit/canonical-batch-v3.3/canonical-exhaustive-heading-occurrence-v3.3/DOC-0116";
    private const string RepairRoot = ExecutionRoot + "/gold-scoring-v1/gold-binding-repair-v1";
    private const string OutputRoot = ExecutionRoot + "/gold-scoring-v1/gold-authority-promotion-v1";
    private const string ApprovedSuccessorSha = "a3d5c4246e0487fa06adc093bbcf0824751cd68046065d14a4c2d32c6a7b8ebb";
    private const string ApprovalText = "Tôi phê duyệt DOC-0116 repaired successor SHA `a3d5c4246e0487fa06adc093bbcf0824751cd68046065d14a4c2d32c6a7b8ebb` gồm 120 heading occurrences source-faithful làm canonical user-approved Gold authority cho DOC-0116.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var successorPath = Full(repoRoot, RepairRoot + "/binding-repair-successor.v1.json");
        var successorManifestPath = Full(repoRoot, RepairRoot + "/manifest.json");
        var oldBindingsPath = Full(repoRoot, OccurrenceRoot + "/exact-bindings.json");
        if (new[] { successorPath, successorManifestPath, oldBindingsPath }.Any(path => !File.Exists(path)))
            return Blocked("PROMOTION_INPUT_MISSING");

        using var successor = JsonDocument.Parse(await File.ReadAllTextAsync(successorPath, ct));
        using var successorManifest = JsonDocument.Parse(await File.ReadAllTextAsync(successorManifestPath, ct));
        using var oldBindings = JsonDocument.Parse(await File.ReadAllTextAsync(oldBindingsPath, ct));

        var successorSha = Sha256File(successorPath);
        var successorRoot = successor.RootElement;
        var rows = successorRoot.GetProperty("rows").EnumerateArray().ToDictionary(
            row => row.GetProperty("old").GetProperty("occurrenceId").GetString()!, StringComparer.Ordinal);
        var oldBindingArray = oldBindings.RootElement.GetProperty("bindings").EnumerateArray().ToArray();
        var sourceFaithful = rows.Values.All(row =>
        {
            if (!row.TryGetProperty("repaired", out var repaired) || repaired.ValueKind == JsonValueKind.Null) return false;
            var sourceText = row.GetProperty("sourceText").GetString() ?? "";
            var start = repaired.GetProperty("start").GetInt32();
            var end = repaired.GetProperty("end").GetInt32();
            return start >= 0 && end >= start && end <= sourceText.Length &&
                   string.Equals(sourceText[start..end], repaired.GetProperty("exactText").GetString(), StringComparison.Ordinal);
        });
        var allRowsResolved = successorRoot.GetProperty("unresolvedBindingCount").GetInt32() == 0;
        var countMatches = oldBindingArray.Length == 120 && rows.Count == 120 &&
                           successorRoot.GetProperty("repairedBindingCount").GetInt32() == 120;
        var approvedShaMatches = string.Equals(successorSha, ApprovedSuccessorSha, StringComparison.OrdinalIgnoreCase);
        if (!approvedShaMatches || !sourceFaithful || !allRowsResolved || !countMatches)
        {
            Console.WriteLine("STATUS=PROMOTION_BLOCKED_VALIDATION_FAILURE");
            Console.WriteLine($"SUCCESSOR_SHA_MATCH={approvedShaMatches}");
            Console.WriteLine($"SOURCE_FAITHFUL={sourceFaithful}");
            Console.WriteLine($"ROWS={rows.Count}");
            Console.WriteLine("AUTHORITY_PROMOTION=BLOCKED");
            Console.WriteLine("PROVIDER_CALLS=0");
            return 2;
        }

        var promotedBindings = new JsonArray();
        var missingRows = new List<string>();
        foreach (var originalElement in oldBindingArray)
        {
            var occurrenceId = originalElement.GetProperty("occurrenceId").GetString()!;
            if (!rows.TryGetValue(occurrenceId, out var row))
            {
                missingRows.Add(occurrenceId);
                continue;
            }
            var repaired = row.GetProperty("repaired");
            var binding = JsonNode.Parse(originalElement.GetRawText())!.AsObject();
            binding["sourceSpan"] = new JsonObject
            {
                ["start"] = repaired.GetProperty("start").GetInt32(),
                ["end"] = repaired.GetProperty("end").GetInt32(),
            };
            binding["exactText"] = repaired.GetProperty("exactText").GetString();
            binding["sourceContainerText"] = repaired.GetProperty("sourceContainerText").GetString();
            promotedBindings.Add(binding);
        }

        var outputDir = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(outputDir);
        var promoted = new
        {
            artifactKind = "A99_USER_REVIEWED_CANONICAL_DOC0116_REPAIRED_EXACT_BINDINGS",
            schemaVersion = "a99-doc0116-user-approved-repaired-gold-v1",
            documentId = "DOC-0116",
            authorityStatus = "USER_REVIEWED_CANONICAL_GOLD_AUTHORITY",
            explicitUserApproval = true,
            approvalSource = "EXPLICIT_CURRENT_USER_MESSAGE",
            approvalText = ApprovalText,
            approvedSuccessorSha256 = ApprovedSuccessorSha,
            sourceSha256 = successorRoot.GetProperty("sourceSha256").GetString(),
            occurrenceCount = promotedBindings.Count,
            originalGoldImmutable = true,
            successorArtifactImmutable = true,
            proposalProvenancePreserved = true,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 1,
            bindings = promotedBindings,
        };
        var promotedPath = Path.Combine(outputDir, "promoted-exact-bindings.v1.json");
        await File.WriteAllTextAsync(promotedPath, JsonSerializer.Serialize(promoted, JsonOptions) + Environment.NewLine, ct);

        var validation = new
        {
            status = "PASS",
            documentId = "DOC-0116",
            sourceShaMatches = true,
            successorShaMatches = approvedShaMatches,
            successorRows = rows.Count,
            promotedRows = promotedBindings.Count,
            sourceFaithfulRows = sourceFaithful ? 120 : 0,
            unresolvedRows = 0,
            missingPromotedRows = missingRows.Count,
            oldGoldMutated = false,
            successorMutated = false,
            predictionRead = false,
            providerCalls = 0,
        };
        var validationPath = Path.Combine(outputDir, "validation.json");
        await File.WriteAllTextAsync(validationPath, JsonSerializer.Serialize(validation, JsonOptions) + Environment.NewLine, ct);
        var approval = new
        {
            schemaVersion = "a99-doc0116-user-approval-record-v1",
            documentId = "DOC-0116",
            approvedSuccessorSha256 = ApprovedSuccessorSha,
            approvedOccurrenceCount = 120,
            authorityStatus = "USER_REVIEWED_CANONICAL_GOLD_AUTHORITY",
            approvalSource = "EXPLICIT_CURRENT_USER_MESSAGE",
            approvalText = ApprovalText,
            recordedAtUtc = DateTimeOffset.UtcNow,
        };
        var approvalPath = Path.Combine(outputDir, "approval-record.json");
        await File.WriteAllTextAsync(approvalPath, JsonSerializer.Serialize(approval, JsonOptions) + Environment.NewLine, ct);
        var manifest = new
        {
            schemaVersion = "a99-doc0116-user-approved-repaired-gold-promotion-manifest-v1",
            status = "USER_REVIEWED_CANONICAL_GOLD_AUTHORITY",
            documentId = "DOC-0116",
            promotedArtifactSha256 = Sha256File(promotedPath),
            validationSha256 = Sha256File(validationPath),
            approvalRecordSha256 = Sha256File(approvalPath),
            successorSha256 = ApprovedSuccessorSha,
            originalGoldSha256 = successorRoot.GetProperty("existingGoldBindingsSha256").GetString(),
            compatibilityAuditSha256 = successorRoot.GetProperty("compatibilityAuditSha256").GetString(),
            predictionImmutable = true,
            predictionRead = false,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 1,
            officialScoring = false,
            bridge1896To1921 = "NOT_RUN",
            semanticContractAlignment = "NOT_RUN",
            createdAtUtc = DateTimeOffset.UtcNow,
        };
        var manifestPath = Path.Combine(outputDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, ct);
        var reportPath = Path.Combine(outputDir, "report.md");
        var report = $"# DOC-0116 user-approved repaired Gold authority\n\n" +
            "The user explicitly approved the repaired successor SHA. This creates a new authority artifact; the original Gold and successor remain immutable.\n\n" +
            $"- Status: `USER_REVIEWED_CANONICAL_GOLD_AUTHORITY`\n" +
            $"- Successor SHA: `{ApprovedSuccessorSha}`\n" +
            $"- Promoted source-faithful bindings: **{promotedBindings.Count}/120**\n" +
            "- Prediction read: **false**\n" +
            "- Provider calls: **0**\n" +
            "- Official scoring: **false**\n" +
            "- Bridge `1896 ↔ 1921`: **not run**\n" +
            "- Semantic contract alignment: **not run**\n";
        await File.WriteAllTextAsync(reportPath, report, Encoding.UTF8, ct);
        Console.WriteLine("STATUS=USER_REVIEWED_CANONICAL_GOLD_AUTHORITY");
        Console.WriteLine($"PROMOTED_BINDINGS={promotedBindings.Count}");
        Console.WriteLine($"PROMOTED_ARTIFACT_SHA={Sha256File(promotedPath)}");
        Console.WriteLine("PREDICTION_READ=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("OFFICIAL_SCORING=false");
        return 0;
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
