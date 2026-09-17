using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Proves the deterministic representation bridge from the 1,921 runtime aliases to the
/// 1,896 normalized source-container identities. This is not semantic scoring or Gold binding.
/// </summary>
public static class CanonicalDevVNextCorrectnessGoldOccurrenceUniverseBridgeAuditRunner
{
    private const string SourceUniversePath = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-preflight/source-universe.v1.json";
    private const string SourceAuthorityPath = "artifacts/authority-audit/canonical-batch-v3.3/canonical-exhaustive-heading-occurrence-v3.3/DOC-0116/source-authority.json";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1/gold-scoring-v1/occurrence-universe-bridge-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var universePath = Full(repoRoot, SourceUniversePath);
        var authorityPath = Full(repoRoot, SourceAuthorityPath);
        if (!File.Exists(universePath) || !File.Exists(authorityPath)) return Blocked("BRIDGE_INPUT_MISSING");

        using var universe = JsonDocument.Parse(await File.ReadAllTextAsync(universePath, ct));
        using var authority = JsonDocument.Parse(await File.ReadAllTextAsync(authorityPath, ct));
        var root = universe.RootElement;
        var sourceAuthority = authority.RootElement;
        var identities = root.GetProperty("sourceIdentity").EnumerateArray().ToArray();
        var aliases = identities.Select(item => item.GetProperty("alias").GetString()!).ToArray();
        var sourceIds = identities.Select(item => item.GetProperty("sourceId").GetString()!).ToArray();
        var groups = identities
            .GroupBy(item => NormalizeContainer(item.GetProperty("sourceId").GetString()!), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var mapping = groups.Select(group => new
        {
            normalizedContainerId = group.Key,
            aliasCount = group.Count(),
            hasDirectAlias = group.Any(item => string.Equals(item.GetProperty("sourceId").GetString(), group.Key, StringComparison.Ordinal)),
            aliases = group.Select(item => new
            {
                alias = item.GetProperty("alias").GetString(),
                sourceId = item.GetProperty("sourceId").GetString(),
                sourceOrdinal = item.GetProperty("sourceOrdinal").GetInt32(),
                textSha256 = item.GetProperty("textSha256").GetString(),
            }).ToArray(),
        }).ToArray();
        var expectedContainers = sourceAuthority.GetProperty("sourceContainerCount").GetInt32();
        var sourceSha = root.GetProperty("sourceSha256").GetString() ?? "";
        var authoritySourceSha = sourceAuthority.GetProperty("sourceSha256").GetString() ?? "";
        var aliasUnique = aliases.Distinct(StringComparer.Ordinal).Count() == aliases.Length;
        var sourceIdUnique = sourceIds.Distinct(StringComparer.Ordinal).Count() == sourceIds.Length;
        var normalizedCount = mapping.Length;
        var extraAliases = mapping.Sum(item => item.aliasCount - 1);
        var grouped = mapping.Where(item => item.aliasCount > 1).ToArray();
        var status = sourceSha == authoritySourceSha && aliases.Length == 1921 && aliasUnique && sourceIdUnique &&
                     normalizedCount == 1896 && expectedContainers == 1896 && extraAliases == 25 && grouped.Length == 3
            ? "BRIDGE_PASS"
            : "BRIDGE_BLOCKED";

        var outputDir = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(outputDir);
        var mappingPath = Path.Combine(outputDir, "alias-to-normalized-container.v1.json");
        await File.WriteAllTextAsync(mappingPath, JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-doc0116-runtime-alias-to-normalized-container-map-v1",
            documentId = "DOC-0116",
            normalizationRule = "sourceId prefix before first /txbxContent segment; direct sourceId remains unchanged",
            aliasCount = aliases.Length,
            normalizedContainerCount = normalizedCount,
            groups = mapping,
        }, JsonOptions) + Environment.NewLine, ct);
        var audit = new
        {
            schemaVersion = "a99-doc0116-occurrence-universe-bridge-audit-v1",
            documentId = "DOC-0116",
            status,
            bridgeMeaning = "DETERMINISTIC_SOURCE_REPRESENTATION_BRIDGE_ONLY",
            semanticEquivalenceClaim = false,
            goldCoverageClaim = false,
            sourceUniverseSha256 = Sha256File(universePath),
            sourceAuthoritySha256 = Sha256File(authorityPath),
            sourceSha256 = sourceSha,
            authoritySourceSha256 = authoritySourceSha,
            runtimeAliasCount = aliases.Length,
            distinctRuntimeAliasCount = aliases.Distinct(StringComparer.Ordinal).Count(),
            normalizedContainerCount = normalizedCount,
            expectedNormalizedContainerCount = expectedContainers,
            aliasSurplusBeyondNormalizedContainers = extraAliases,
            groupedContainerCount = grouped.Length,
            maxGroupSize = mapping.Max(item => item.aliasCount),
            directAliaslessGroupCount = mapping.Count(item => !item.hasDirectAlias),
            allAliasesUnique = aliasUnique,
            allSourceIdsUnique = sourceIdUnique,
            sourceShaMatchesAuthority = string.Equals(sourceSha, authoritySourceSha, StringComparison.OrdinalIgnoreCase),
            aliasToContainerMappingSha256 = Sha256File(mappingPath),
            groupedContainers = grouped,
            providerCalls = 0,
            modelCalls = 0,
            predictionRead = false,
            goldReads = 0,
            scoring = false,
            createdAtUtc = DateTimeOffset.UtcNow,
        };
        var auditPath = Path.Combine(outputDir, "bridge-audit.v1.json");
        await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(audit, JsonOptions) + Environment.NewLine, ct);
        var reportPath = Path.Combine(outputDir, "report.md");
        var report = $"# DOC-0116 occurrence-universe bridge audit\n\n" +
            $"Status: `{status}`\n\n" +
            "This proves only a deterministic source-representation bridge; it does not assert semantic identity, Gold coverage, or prediction correctness.\n\n" +
            $"- Runtime aliases: **{aliases.Length}**\n" +
            $"- Normalized containers: **{normalizedCount}/{expectedContainers}**\n" +
            $"- Alias surplus: **{extraAliases}**\n" +
            $"- Grouped containers: **{grouped.Length}**\n" +
            $"- Max group size: **{mapping.Max(item => item.aliasCount)}**\n" +
            $"- Source SHA match: **{string.Equals(sourceSha, authoritySourceSha, StringComparison.OrdinalIgnoreCase)}**\n" +
            "- Prediction read: **false**\n" +
            "- Provider calls: **0**\n" +
            "- Gold reads: **0**\n";
        await File.WriteAllTextAsync(reportPath, report, Encoding.UTF8, ct);
        var manifest = new
        {
            schemaVersion = "a99-doc0116-occurrence-universe-bridge-manifest-v1",
            status,
            auditSha256 = Sha256File(auditPath),
            mappingSha256 = Sha256File(mappingPath),
            reportSha256 = Sha256File(reportPath),
            providerCalls = 0,
            goldReads = 0,
            predictionRead = false,
            semanticContractAlignment = "NOT_RUN",
            officialScoring = false,
        };
        await File.WriteAllTextAsync(Path.Combine(outputDir, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, ct);
        Console.WriteLine($"STATUS={status}");
        Console.WriteLine($"RUNTIME_ALIASES={aliases.Length}");
        Console.WriteLine($"NORMALIZED_CONTAINERS={normalizedCount}");
        Console.WriteLine($"ALIAS_SURPLUS={extraAliases}");
        Console.WriteLine($"GROUPED_CONTAINERS={grouped.Length}");
        Console.WriteLine("PREDICTION_READ=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return status == "BRIDGE_PASS" ? 0 : 2;
    }

    private static string NormalizeContainer(string sourceId)
    {
        var marker = sourceId.IndexOf("/txbxContent", StringComparison.Ordinal);
        return marker >= 0 ? sourceId[..marker] : sourceId;
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
