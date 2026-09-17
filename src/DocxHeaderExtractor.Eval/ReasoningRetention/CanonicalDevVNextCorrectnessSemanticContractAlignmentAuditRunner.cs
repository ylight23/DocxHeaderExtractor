using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Audits the semantic contract of the frozen DOC-0116 prediction without reading Gold,
/// calling a provider, or rewriting the frozen prediction.
/// </summary>
public static class CanonicalDevVNextCorrectnessSemanticContractAlignmentAuditRunner
{
    private const string PreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-preflight-v2-1";
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1/gold-scoring-v1/semantic-contract-alignment-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var requests = Enumerable.Range(1, 7)
            .Select(i => Full(repoRoot, $"{PreflightRoot}/materialized-request-{i:000}.v1.json"))
            .ToArray();
        var parsed = Enumerable.Range(1, 7)
            .Select(i => Full(repoRoot, $"{ExecutionRoot}/segment-{i:000}.parsed.v1.json"))
            .ToArray();
        var freezePath = Full(repoRoot, $"{ExecutionRoot}/prediction-freeze.v1.json");
        if (requests.Any(path => !File.Exists(path)) || parsed.Any(path => !File.Exists(path)) || !File.Exists(freezePath))
            return Blocked(repoRoot, "SEMANTIC_ALIGNMENT_INPUT_MISSING");

        var systemPromptHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var schemaHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemPrompts = new List<string>();
        var schemas = new List<string>();
        foreach (var path in requests)
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            var root = doc.RootElement;
            var systemPrompt = root.GetProperty("systemPrompt").GetString() ?? string.Empty;
            var schema = root.GetProperty("schema").GetRawText();
            systemPrompts.Add(systemPrompt);
            schemas.Add(schema);
            systemPromptHashes.Add(Sha256(Encoding.UTF8.GetBytes(systemPrompt)));
            schemaHashes.Add(Sha256(Encoding.UTF8.GetBytes(schema)));
        }

        var roleCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var parsedProposalCount = 0;
        foreach (var path in parsed)
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            if (!doc.RootElement.TryGetProperty("proposals", out var proposals) || proposals.ValueKind != JsonValueKind.Array)
                continue;
            parsedProposalCount += proposals.GetArrayLength();
            foreach (var proposal in proposals.EnumerateArray())
            {
                if (!proposal.TryGetProperty("role", out var role)) continue;
                var value = role.GetString() ?? "";
                roleCounts[value] = roleCounts.TryGetValue(value, out var count) ? count + 1 : 1;
            }
        }

        using var freeze = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
        var freezeRoot = freeze.RootElement;
        var runtimeSourceAliasCount = ReadInt(freezeRoot, "canonicalOccurrenceCount");
        var frozenCanonicalGraphOccurrences = ReadInt(freezeRoot, "canonicalGraphOccurrenceCount");
        var providerCalls = ReadInt(freezeRoot, "providerCalls");

        var broadPhrase = systemPrompts.Any(p => p.Contains("heading or structural label", StringComparison.OrdinalIgnoreCase));
        var trueHeadingField = schemas.Any(s => s.Contains("isTrueHeading", StringComparison.OrdinalIgnoreCase) ||
                                               s.Contains("headingDecision", StringComparison.OrdinalIgnoreCase));
        var broadStructuralRole = schemas.Any(s => s.Contains("OTHER_STRUCTURAL_LABEL", StringComparison.Ordinal));
        var sourceField = schemas.All(s => s.Contains("\"source\"", StringComparison.Ordinal));
        var textField = schemas.All(s => s.Contains("\"text\"", StringComparison.Ordinal));
        var roleField = schemas.All(s => s.Contains("\"role\"", StringComparison.Ordinal));

        // No Gold-independent operation in the frozen artifacts authorizes a projection from
        // the broad structural-label output to ALL TRUE HEADING OCCURRENCES.
        const bool deterministicGoldIndependentProjectionAvailable = false;
        var frozenCompatibility = !broadPhrase && trueHeadingField && deterministicGoldIndependentProjectionAvailable
            ? "PASS"
            : "FAIL";
        var outputDir = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(outputDir);

        var audit = new
        {
            schemaVersion = "a99-doc0116-semantic-contract-alignment-audit-v1",
            documentId = "DOC-0116",
            status = "FROZEN_PREDICTION_SEMANTIC_COMPATIBILITY_FAIL",
            frozenPredictionSemanticCompatibility = frozenCompatibility,
            officialScoring = false,
            metricsUsableAsOfficialAccuracy = false,
            futureContractAlignment = "READY_FOR_SEPARATE_FUTURE_BASELINE",
            predictionImmutable = true,
            predictionFreezeSha256 = Sha256File(freezePath),
            requestCount = requests.Length,
            requestHashes = requests.Select(Sha256File).ToArray(),
            systemPromptHashes = systemPromptHashes.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            schemaHashes = schemaHashes.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            frozenContract = new
            {
                broadHeadingOrStructuralLabelPhrase = broadPhrase,
                hasIndependentTrueHeadingField = trueHeadingField,
                permitsOtherStructuralLabelRole = broadStructuralRole,
                hasSourceField = sourceField,
                hasTextField = textField,
                hasRoleField = roleField,
                deterministicGoldIndependentProjectionAvailable,
            },
            predictionForensics = new
            {
                parsedProposalCount,
                runtimeSourceAliasCount,
                frozenCanonicalGraphOccurrences,
                providerCalls,
                roleCounts,
            },
            firewall = new
            {
                goldReads = 0,
                providerCalls = 0,
                scoring = false,
                predictionMutation = false,
                derivedPredictionCreated = false,
                goldUsedToClassifyPrediction = false,
            },
            createdAtUtc = DateTimeOffset.UtcNow,
        };
        var auditPath = Path.Combine(outputDir, "semantic-contract-alignment-audit.v1.json");
        await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(audit, JsonOptions) + Environment.NewLine, ct);

        var futureContract = new
        {
            schemaVersion = "a99-doc0116-future-true-heading-contract-v1",
            status = "FUTURE_CONTRACT_ONLY",
            target = "ALL TRUE HEADING OCCURRENCES",
            requiredDecisionField = "headingDecision",
            allowedDecisionValues = new[] { "TRUE_HEADING", "NOT_TRUE_HEADING" },
            semanticRoleIsSeparateFromHeadingDecision = true,
            structuralLabelMetadataMayBeRetained = true,
            currentFrozenPredictionCompatible = false,
            providerCalls = 0,
            goldReads = 0,
            note = "This is a successor contract specification only; it does not rewrite or re-score the frozen 289 prediction."
        };
        var futurePath = Path.Combine(outputDir, "future-contract.v1.json");
        await File.WriteAllTextAsync(futurePath, JsonSerializer.Serialize(futureContract, JsonOptions) + Environment.NewLine, ct);

        var report = $"# DOC-0116 semantic contract alignment audit\n\n" +
            "Status: **FROZEN_PREDICTION_SEMANTIC_COMPATIBILITY_FAIL**\n\n" +
            "Official scoring remains blocked. The frozen provider contract asks for every structurally real heading or structural label, while the Gold target is ALL TRUE HEADING OCCURRENCES. The frozen schema has no independent true-heading decision field and permits OTHER_STRUCTURAL_LABEL. No deterministic Gold-independent projection is present in the frozen artifacts.\n\n" +
            $"- Runtime source aliases: **{runtimeSourceAliasCount}**\n" +
            $"- Frozen canonical graph occurrences: **{frozenCanonicalGraphOccurrences}**\n" +
            $"- Parsed proposals inspected: **{parsedProposalCount}**\n" +
            $"- Provider calls (historical frozen run): **{providerCalls}**\n" +
            $"- Prompt broad phrase present: **{broadPhrase}**\n" +
            $"- Independent true-heading field present: **{trueHeadingField}**\n" +
            $"- OTHER_STRUCTURAL_LABEL permitted: **{broadStructuralRole}**\n" +
            $"- Gold-independent projection available: **{deterministicGoldIndependentProjectionAvailable}**\n\n" +
            "The 289 frozen prediction and its forensic artifacts remain immutable. No Gold was read, no provider was called, and no derived prediction successor was created. A future contract draft is recorded separately for a new baseline.\n";
        var reportPath = Path.Combine(outputDir, "report.md");
        await File.WriteAllTextAsync(reportPath, report, Encoding.UTF8, ct);

        var manifest = new
        {
            schemaVersion = "a99-doc0116-semantic-contract-alignment-manifest-v1",
            status = "FROZEN_PREDICTION_SEMANTIC_COMPATIBILITY_FAIL",
            officialScoring = false,
            auditSha256 = Sha256File(auditPath),
            futureContractSha256 = Sha256File(futurePath),
            reportSha256 = Sha256File(reportPath),
            predictionFreezeSha256 = Sha256File(freezePath),
            goldReads = 0,
            providerCalls = 0,
            scoring = false,
            predictionMutation = false,
            derivedPredictionCreated = false,
        };
        var manifestPath = Path.Combine(outputDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, ct);

        Console.WriteLine("STATUS=FROZEN_PREDICTION_SEMANTIC_COMPATIBILITY_FAIL");
        Console.WriteLine($"PARSED_PROPOSALS={parsedProposalCount}");
        Console.WriteLine($"RUNTIME_SOURCE_ALIASES={runtimeSourceAliasCount}");
        Console.WriteLine($"FROZEN_CANONICAL_GRAPH_OCCURRENCES={frozenCanonicalGraphOccurrences}");
        Console.WriteLine("OFFICIAL_SCORING=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return 0;
    }

    private static int ReadInt(JsonElement root, string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static int Blocked(string repoRoot, string reason)
    {
        Console.WriteLine($"STATUS=BLOCKED:{reason}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return 2;
    }
}
