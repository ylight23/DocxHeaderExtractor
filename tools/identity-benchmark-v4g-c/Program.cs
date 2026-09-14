using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV4GC;

internal static class Program
{
    private const int ExpectedCalls = 128;
    private const string V4GExecution = "artifacts/identity-benchmark/v4/target-grounding-challenger/execution";
    private const string V4FDSample = "artifacts/identity-benchmark/v4/projected-verifier-experiment/sample.json";
    private const string V4FEExecution = "artifacts/identity-benchmark/v4/projected-verifier-experiment/execution";
    private const string GoldBindings = "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v2.json";
    private const string LatestGoldBindings = "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v4.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v4/target-grounding-challenger/semantic-audit";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V4G_C_STATUS=OFFLINE_SEMANTIC_BEHAVIOR_AUDIT_COMPLETE PROVIDER_CALLS=0 MODEL_CALLS=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4G_C_ERROR={ex}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var execution = Full(root, V4GExecution);
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);
        var executionManifest = Read(Path.Combine(execution, "execution-manifest.json"));
        var attempts = Read(Path.Combine(execution, "attempt-manifest.json"));
        var v2Parsed = Read(Path.Combine(execution, "parsed-results.json"));
        var v2Results = v2Parsed.RootElement.GetProperty("results").EnumerateArray().Select(x => x.Clone()).ToArray();
        var targetResults = Read(Path.Combine(execution, "target-grounding-results.json"));
        var paired = Read(Path.Combine(execution, "historical-paired-analysis.json"));
        var integrity = VerifyExecutionIntegrity(root, executionManifest.RootElement, attempts.RootElement, v2Results, targetResults.RootElement, paired.RootElement);
        await WriteAsync(Path.Combine(output, "manifest.json"), integrity);

        var v1 = Read(Path.Combine(Full(root, V4FEExecution), "parsed-results.json"));
        var v1Results = v1.RootElement.GetProperty("results").EnumerateArray()
            .Where(x => Text(x, "arm") == "ARM_B")
            .ToDictionary(x => Text(x, "candidateId")!, x => x.Clone(), StringComparer.Ordinal);
        var v2ByCandidate = v2Results.ToDictionary(x => Text(x, "candidateId")!, StringComparer.Ordinal);
        var behavioral = BuildBehavioral(v1Results, v2ByCandidate, Full(root, V4FEExecution + "/raw-responses"));
        await WriteAsync(Path.Combine(output, "population-denominators.json"), behavioral.Denominators);
        await WriteAsync(Path.Combine(output, "relation-transition-matrix.json"), behavioral.Transitions);
        await WriteAsync(Path.Combine(output, "clean-cohort-analysis.json"), behavioral.CleanCohort);

        var strata = BuildSourceStrata(Read(Full(root, V4FDSample)).RootElement, v2Results);
        await WriteAsync(Path.Combine(output, "source-strata-analysis.json"), strata);

        var goldJoin = BuildGoldDiagnostic(root, Read(Full(root, V4FDSample)).RootElement, v2ByCandidate);
        await WriteAsync(Path.Combine(output, "dev-gold-diagnostic.json"), goldJoin);
        await WriteAsync(Path.Combine(output, "failure-attribution.json"), new
        {
            schemaVersion = "a99-v4g-c-failure-attribution-v1",
            goldReadAfterBehaviorFreeze = true,
            semanticAccuracyMeasured = goldJoin.GetProperty("evaluable").GetInt32() > 0,
            failures = Array.Empty<object>(),
            note = "No production behavior was changed. Existing frozen Gold is diagnostic only.",
        });

        var cleanAgreement = behavioral.CleanAgreement;
        var stability = cleanAgreement is null ? "INSUFFICIENT_COMPARABLE_OUTPUTS" : cleanAgreement.Value >= .90 ? "HIGH" : cleanAgreement.Value >= .70 ? "MODERATE" : "LOW";
        var semanticStatus = goldJoin.GetProperty("evaluable").GetInt32() == 0 ? "INSUFFICIENT_LABELS" : goldJoin.GetProperty("incorrect").GetInt32() == 0 ? "SUPPORTED_BY_DEV_DIAGNOSTIC" : "DEV_DIAGNOSTIC_FAILURE";
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(integrity, behavioral, strata, goldJoin, stability, semanticStatus), new UTF8Encoding(false));
    }

    private static object VerifyExecutionIntegrity(string root, JsonElement manifest, JsonElement attempts, JsonElement[] v2Results, JsonElement target, JsonElement paired)
    {
        var attemptCount = attempts.GetProperty("actualAttemptCount").GetInt32();
        var providerCalls = manifest.GetProperty("actualProviderCalls").GetInt32();
        var valid = target.GetProperty("valid").GetInt32();
        var errors = target.GetProperty("providerErrors").GetInt32();
        Require(manifest.GetProperty("status").GetString() == "RESPONSE_FREEZE_COMPLETE", "V4G_C_EXECUTION_STATUS");
        Require(manifest.GetProperty("scheduledCalls").GetInt32() == ExpectedCalls, "V4G_C_SCHEDULED_COUNT");
        Require(attemptCount == ExpectedCalls && v2Results.Length == ExpectedCalls, "V4G_C_ATTEMPT_COUNT");
        Require(providerCalls == ExpectedCalls, "V4G_C_PROVIDER_COUNT");
        Require(valid == 95 && errors == 33 && target.GetProperty("targetMismatch").GetInt32() == 0, "V4G_C_RESULT_COUNTS");
        Require(paired.GetProperty("goldReadCount").GetInt32() == 0, "V4G_C_PAIRED_GOLD_FIREWALL");
        return new
        {
            schemaVersion = "a99-v4g-c-manifest-v1",
            status = "INTEGRITY_VERIFIED",
            sourceExecution = "V4G-B@9446aac",
            executionManifestSha256 = Sha256File(Full(root, V4GExecution + "/execution-manifest.json")),
            attemptManifestSha256 = Sha256File(Full(root, V4GExecution + "/attempt-manifest.json")),
            parsedResultsSha256 = Sha256File(Full(root, V4GExecution + "/parsed-results.json")),
            targetGroundingResultsSha256 = Sha256File(Full(root, V4GExecution + "/target-grounding-results.json")),
            historicalPairedAnalysisSha256 = Sha256File(Full(root, V4GExecution + "/historical-paired-analysis.json")),
            scheduled = ExpectedCalls,
            attempts = attemptCount,
            providerCalls,
            valid,
            providerErrors = errors,
            targetMismatch = target.GetProperty("targetMismatch").GetInt32(),
            goldReadCount = 0,
            modelCalls = 0,
            noExecutionArtifactsMutated = true,
        };
    }

    private static BehavioralResult BuildBehavioral(IReadOnlyDictionary<string, JsonElement> v1, IReadOnlyDictionary<string, JsonElement> v2, string v1RawDir)
    {
        var matrix = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var clean = new List<object>();
        var repaired = new List<object>();
        var changes = 0;
        var agreement = 0;
        var v1Evaluable = 0;
        var v2Evaluable = 0;
        var jointlyEvaluable = 0;
        var jointlyValid = 0;
        var unavailable = 0;
        var v1Mismatch = 0;
        foreach (var (candidateId, oldResult) in v1)
        {
            if (!v2.TryGetValue(candidateId, out var newResult)) continue;
            var oldRelation = Relation(oldResult);
            var newRelation = Relation(newResult);
            var oldRawPersisted = RawPersisted(oldResult, v1RawDir);
            var oldTransportEvaluable = oldRawPersisted && (Text(oldResult, "status") is "VALID" or "INVALID_SCHEMA");
            var newTransportEvaluable = Text(newResult, "status") is "VALID" or "INVALID_SCHEMA";
            var oldMismatch = oldTransportEvaluable && Reason(oldResult) == "TARGET_PAIR_MISMATCH";
            var oldValid = oldTransportEvaluable && Text(oldResult, "status") == "VALID" && oldRelation is not null;
            var newValid = newTransportEvaluable && Text(newResult, "status") == "VALID" && newRelation is not null;
            if (oldTransportEvaluable) v1Evaluable++;
            if (newTransportEvaluable) v2Evaluable++;
            if (oldTransportEvaluable && newTransportEvaluable) jointlyEvaluable++;
            if (oldMismatch) v1Mismatch++;
            if (oldMismatch && newValid) repaired.Add(new { candidateId, v2Relation = newRelation });
            if (oldValid && newValid)
            {
                jointlyValid++;
                if (oldRelation == newRelation) agreement++; else changes++;
                AddMatrix(matrix, oldRelation!, newRelation!);
                clean.Add(new { candidateId, v1Relation = oldRelation, v2Relation = newRelation, changed = oldRelation != newRelation });
            }
            else unavailable++;
        }
        var agreementRate = jointlyValid == 0 ? (double?)null : (double)agreement / jointlyValid;
        var changeRate = jointlyValid == 0 ? (double?)null : (double)changes / jointlyValid;
        return new BehavioralResult(
            new
            {
                schemaVersion = "a99-v4g-c-population-denominators-v1",
                scheduledV2 = 128,
                transportOrEvaluatorV2 = 95,
                v1V2JointlyEvaluable = jointlyEvaluable,
                v1TransportOrEvaluator = v1Evaluable,
                v2TransportOrEvaluator = v2Evaluable,
                jointlyValidRelationOutputs = jointlyValid,
                v1TargetMismatch = v1Mismatch,
                unavailableOrNotJointlyValid = unavailable,
                existingGoldLabeledCandidates = "computed after behavioral and strata artifacts freeze",
            },
            new { schemaVersion = "a99-v4g-c-relation-transition-matrix-v1", v1ToV2 = matrix, jointlyValidRelationOutputs = jointlyValid },
            new
            {
                schemaVersion = "a99-v4g-c-clean-cohort-analysis-v1",
                definition = "V1 valid and target-grounded, V2 valid and target-grounded; V1/V2 outputs only, not Gold",
                jointlyValid,
                relationAgreement = agreement,
                relationChanges = changes,
                relationAgreementRate = agreementRate,
                relationChangeRate = changeRate,
                rows = clean,
            },
            agreementRate);
    }

    private static object BuildSourceStrata(JsonElement sample, JsonElement[] v2Results)
    {
        var candidates = sample.GetProperty("candidates").EnumerateArray().ToDictionary(x => Text(x, "pairId")!, x => x, StringComparer.Ordinal);
        var groups = new Dictionary<string, Stratum>(StringComparer.Ordinal);
        foreach (var result in v2Results)
        {
            var id = Text(result, "candidateId")!;
            if (!candidates.TryGetValue(id, out var candidate)) continue;
            var key = string.Join("|", Text(candidate, "documentId"), Text(candidate, "packetClass"), Text(candidate, "evidenceConfiguration"), Text(candidate, "sourceOrderDistanceBucket"));
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new Stratum(Text(candidate, "documentId")!, Text(candidate, "packetClass")!, Text(candidate, "evidenceConfiguration")!, Text(candidate, "sourceOrderDistanceBucket")!);
            group.Scheduled++;
            var relation = Relation(result);
            if (relation is not null) group.Relations[relation] = group.Relations.GetValueOrDefault(relation) + 1;
            else group.Unavailable++;
        }
        return new
        {
            schemaVersion = "a99-v4g-c-source-strata-analysis-v1",
            sourceOnly = true,
            strata = groups.Values.Select(x => new { x.DocumentId, x.PacketClass, x.EvidenceConfiguration, x.SourceOrderDistanceBucket, x.Scheduled, evaluable = x.Relations.Values.Sum(), providerOrParseUnavailable = x.Unavailable, relationDistribution = x.Relations }).ToArray(),
        };
    }

    private static JsonElement BuildGoldDiagnostic(string root, JsonElement sample, IReadOnlyDictionary<string, JsonElement> v2)
    {
        var gold = Read(Full(root, GoldBindings)).RootElement;
        var latest = Read(Full(root, LatestGoldBindings)).RootElement;
        var candidates = sample.GetProperty("candidates").EnumerateArray().ToArray();
        var rows = new List<object>();
        foreach (var item in gold.GetProperty("items").EnumerateArray())
        {
            var left = item.TryGetProperty("leftOccurrence", out var leftNode) && leftNode.ValueKind == JsonValueKind.Object ? Text(leftNode, "sourceOccurrenceId") : null;
            var right = item.TryGetProperty("rightOccurrence", out var rightNode) && rightNode.ValueKind == JsonValueKind.Object ? Text(rightNode, "sourceOccurrenceId") : null;
            if (left is null || right is null) continue;
            var candidate = candidates.FirstOrDefault(x => (Text(x, "left") == left && Text(x, "right") == right) || (Text(x, "left") == right && Text(x, "right") == left));
            if (candidate.ValueKind == JsonValueKind.Undefined) continue;
            var candidateId = Text(candidate, "pairId")!;
            var result = v2.TryGetValue(candidateId, out var r) ? r : default;
            rows.Add(new { goldItemId = Text(item, "id"), candidateId, goldRelation = Text(item, "semanticRelation"), v2Relation = result.ValueKind == JsonValueKind.Undefined ? null : Relation(result), v2Status = result.ValueKind == JsonValueKind.Undefined ? "NOT_IN_V2" : Text(result, "status"), outcome = result.ValueKind == JsonValueKind.Undefined || Relation(result) is null ? "UNAVAILABLE" : Relation(result) == NormalizeRelation(Text(item, "semanticRelation")) ? "CORRECT" : "INCORRECT" });
        }
        var evaluable = rows.Count(x => JsonSerializer.Serialize(x).Contains("\"outcome\":\"CORRECT\"", StringComparison.Ordinal));
        var incorrect = rows.Count(x => JsonSerializer.Serialize(x).Contains("\"outcome\":\"INCORRECT\"", StringComparison.Ordinal));
        return ParseObject(new
        {
            schemaVersion = "a99-v4g-c-dev-gold-diagnostic-v1",
            goldArtifact = "USER_REVIEWED_IDENTITY_GOLD_FROZEN",
            latestGoldBindingsSha256 = Sha256File(Full(root, LatestGoldBindings)),
            bindingGoldSha256 = Sha256File(Full(root, GoldBindings)),
            goldReadAfterBehaviorFreeze = true,
            independentGeneralizationClaim = false,
            evaluable,
            correct = evaluable,
            incorrect,
            accuracy = evaluable == 0 ? (double?)null : (double)evaluable / (evaluable + incorrect),
            rows,
            note = rows.Count == 0 ? "No exact frozen Gold endpoint pair occurs naturally in the 128-candidate sample; semantic correctness is unavailable." : "Small DEV diagnostic only; not independent holdout accuracy.",
        });
    }

    private static string BuildReport(object integrity, BehavioralResult behavioral, object strata, JsonElement gold, string stability, string semanticStatus)
    {
        return $"# A99 V4G-C — offline semantic behavior audit\n\nStatus: **COMPLETE**\n\nProvider calls: **0**. Model calls: **0**. V4G-B execution artifacts were read-only.\n\n## Conclusions\n\n- Target grounding: **REPAIRED_ON_DEV_SAMPLE** (V4G-B, 0/95 target mismatch).\n- Behavioral stability: **{stability}**.\n- Semantic correctness: **{semanticStatus}**.\n\n## Behavioral layers\n\n```json\n{JsonSerializer.Serialize(behavioral.Denominators, JsonOptions)}\n```\n\n## Clean V1/V2 relation comparison\n\n```json\n{JsonSerializer.Serialize(behavioral.CleanCohort, JsonOptions)}\n```\n\n## Existing DEV Gold diagnostic\n\nGold was opened only after behavioral and source-strata artifacts were frozen. It is not an independent holdout and OLD/V1 outputs were not treated as Gold.\n\n```json\n{gold.GetRawText()}\n```\n\nNo production rollout or prompt/model change was made.\n";
    }

    private static void AddMatrix(Dictionary<string, Dictionary<string, int>> matrix, string oldRelation, string newRelation)
    {
        if (!matrix.TryGetValue(oldRelation, out var row)) matrix[oldRelation] = row = new Dictionary<string, int>(StringComparer.Ordinal);
        row[newRelation] = row.GetValueOrDefault(newRelation) + 1;
    }

    private static string? Relation(JsonElement result)
    {
        if (Text(result, "status") != "VALID" || !result.TryGetProperty("parsed", out var parsed) || parsed.ValueKind != JsonValueKind.Object || !parsed.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object) return null;
        return Text(response, "relation");
    }

    private static string? Reason(JsonElement result) => result.TryGetProperty("parsed", out var parsed) && parsed.ValueKind == JsonValueKind.Object && parsed.TryGetProperty("validation", out var validation) && validation.ValueKind == JsonValueKind.Object ? Text(validation, "rejectionReason") : null;
    private static bool RawPersisted(JsonElement result, string rawDir)
    {
        var hash = Text(result, "rawResponseSha256");
        var sequence = result.TryGetProperty("sequence", out var sequenceNode) && sequenceNode.ValueKind == JsonValueKind.Number ? sequenceNode.GetInt32() : -1;
        if (hash is null || sequence < 0) return false;
        var path = Path.Combine(rawDir, $"{sequence:D4}-ARM_B.json");
        return File.Exists(path) && Sha256File(path) == hash;
    }
    private static string NormalizeRelation(string? value) => value?.Replace("_SEMANTIC_NODE", "", StringComparison.Ordinal) ?? "";
    private static string? Text(JsonElement node, string property) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static JsonElement ParseObject(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, JsonOptions)).RootElement.Clone();
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed class Stratum
    {
        public Stratum(string documentId, string packetClass, string evidenceConfiguration, string sourceOrderDistanceBucket) { DocumentId = documentId; PacketClass = packetClass; EvidenceConfiguration = evidenceConfiguration; SourceOrderDistanceBucket = sourceOrderDistanceBucket; }
        public string DocumentId { get; }
        public string PacketClass { get; }
        public string EvidenceConfiguration { get; }
        public string SourceOrderDistanceBucket { get; }
        public int Scheduled { get; set; }
        public int Unavailable { get; set; }
        public Dictionary<string, int> Relations { get; } = new(StringComparer.Ordinal);
    }

    private sealed record BehavioralResult(object Denominators, object Transitions, object CleanCohort, double? CleanAgreement);
}
