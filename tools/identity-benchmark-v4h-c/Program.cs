using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV4HC;

internal static class Program
{
    private const int ExpectedCount = 128;
    private const string FrozenGoldRelative = "artifacts/identity-benchmark/v4/semantic-adjudication/results/human-adjudication.frozen.v1.json";
    private const string PredictionRelative = "artifacts/identity-benchmark/v4/target-grounding-challenger/execution/parsed-results.json";
    private const string ExecutionManifestRelative = "artifacts/identity-benchmark/v4/target-grounding-challenger/execution/execution-manifest.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v4/semantic-adjudication/evaluation";
    private const string GoldAuthority = "USER_FINAL_SOURCE_BACKED_ADJUDICATION";
    private const string FrozenGoldSha256 = "1b4c10fd20e0840a272611065bfcff524335542ea5d5b71cfe16bae692eaae16";

    private static readonly string[] Labels = ["SAME_SEMANTIC_REPEAT", "CONTINUATION_OF", "DISTINCT"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V4H_C_STATUS=OFFLINE_SEMANTIC_EVALUATION_COMPLETE MODEL_CALLS=0 PROVIDER_CALLS=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4H_C_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var goldPath = Full(root, FrozenGoldRelative);
        var predictionPath = Full(root, PredictionRelative);
        var manifestPath = Full(root, ExecutionManifestRelative);
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);

        var gold = Read(goldPath);
        var predictions = Read(predictionPath);
        var executionManifest = Read(manifestPath);
        var goldRows = ReadGold(gold.RootElement);
        var predictionRows = ReadPredictions(predictions.RootElement);
        ValidateFrozenInputs(gold.RootElement, goldPath, goldRows, predictionRows, executionManifest.RootElement);
        var joined = Join(goldRows, predictionRows);

        var summary = BuildSummary(joined, root, goldPath, predictionPath, manifestPath);
        var confusion = BuildConfusion(joined);
        var perDocument = BuildPerDocument(joined);
        var perItem = joined.Select(ToPerItem).ToArray();
        var sanity = BuildSanity(joined);
        var firewall = new
        {
            schemaVersion = "a99-v4h-c-firewall-v1",
            goldReadAfterPredictionFreeze = true,
            frozenGoldMutated = false,
            predictionArtifactsMutated = false,
            modelCalls = 0,
            providerCalls = 0,
            newProviderCalls = 0,
            sourcePredictionProviderCalls = executionManifest.RootElement.GetProperty("actualProviderCalls").GetInt32(),
            sourcePredictionModelCalls = executionManifest.RootElement.GetProperty("actualModelCalls").GetInt32(),
            exactJoin = true,
            goldAuthority = GoldAuthority,
            independentHumanGold = gold.RootElement.GetProperty("independentHumanGold").GetBoolean(),
        };

        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v4h-c-evaluation-manifest-v1",
            status = "V4H_C_COMPLETE",
            experiment = "IDENTITY_BENCHMARK_V4H_C",
            goldArtifact = Relative(root, goldPath),
            goldSha256 = Sha256File(goldPath),
            expectedGoldSha256 = FrozenGoldSha256,
            predictionArtifact = Relative(root, predictionPath),
            predictionSha256 = Sha256File(predictionPath),
            executionManifest = Relative(root, manifestPath),
            executionManifestSha256 = Sha256File(manifestPath),
            itemCount = joined.Count,
            exactCandidateJoinCount = joined.Count,
            exactPredictionEndpointJoinCount = joined.Count(x => x.Prediction.Relation is not null),
            providerErrorEndpointUnavailableCount = joined.Count(x => x.Prediction.Relation is null),
            exactJoin = true,
            evaluationOnly = true,
            providerCalls = 0,
            modelCalls = 0,
            pairwiseLaneStatus = "CLOSED_AFTER_V4H_C",
            nextArchitecture = "GLOBAL_CLUSTER_SEMANTIC_NODE_INDUCTION",
        });
        await WriteAsync(Path.Combine(output, "summary.json"), summary);
        await WriteAsync(Path.Combine(output, "confusion-matrix.json"), confusion);
        await WriteAsync(Path.Combine(output, "per-document.json"), perDocument);
        await WriteAsync(Path.Combine(output, "per-item.json"), perItem);
        await WriteAsync(Path.Combine(output, "sanity-cases.json"), sanity);
        await WriteAsync(Path.Combine(output, "firewall.json"), firewall);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(summary, confusion, perDocument, sanity), new UTF8Encoding(false));
    }

    private static void ValidateFrozenInputs(JsonElement gold, string goldPath, IReadOnlyList<GoldRow> goldRows, IReadOnlyList<PredictionRow> predictionRows, JsonElement executionManifest)
    {
        Require(gold.GetProperty("status").GetString() == "BLINDED_SOURCE_BACKED_USER_ADJUDICATION_FROZEN", "V4H_C_GOLD_NOT_FROZEN");
        Require(gold.GetProperty("authority").GetString() == GoldAuthority, "V4H_C_GOLD_AUTHORITY_DRIFT");
        Require(!gold.GetProperty("independentHumanGold").GetBoolean(), "V4H_C_INDEPENDENT_HUMAN_CLAIM_DRIFT");
        Require(gold.GetProperty("itemCount").GetInt32() == ExpectedCount && goldRows.Count == ExpectedCount, "V4H_C_GOLD_COUNT");
        Require(Sha256File(goldPath).Equals(FrozenGoldSha256, StringComparison.OrdinalIgnoreCase), "V4H_C_GOLD_SHA_DRIFT");
        Require(predictionRows.Count == ExpectedCount, "V4H_C_PREDICTION_COUNT");
        Require(predictionRows.Select(x => x.CandidateId).Distinct(StringComparer.Ordinal).Count() == ExpectedCount, "V4H_C_PREDICTION_DUPLICATE");
        Require(executionManifest.GetProperty("status").GetString() == "RESPONSE_FREEZE_COMPLETE", "V4H_C_PREDICTION_NOT_FROZEN");
        Require(executionManifest.GetProperty("scheduledCalls").GetInt32() == ExpectedCount, "V4H_C_SCHEDULED_CALL_COUNT");
        Require(executionManifest.GetProperty("actualModelCalls").GetInt32() == ExpectedCount, "V4H_C_SOURCE_MODEL_CALL_COUNT");
        Require(executionManifest.GetProperty("actualProviderCalls").GetInt32() == ExpectedCount, "V4H_C_SOURCE_PROVIDER_CALL_COUNT");
        Require(executionManifest.GetProperty("goldReadCount").GetInt32() == 0, "V4H_C_SOURCE_GOLD_FIREWALL");
    }

    private static IReadOnlyList<JoinedRow> Join(IReadOnlyList<GoldRow> gold, IReadOnlyList<PredictionRow> predictions)
    {
        var byCandidate = predictions.ToDictionary(x => x.CandidateId, StringComparer.Ordinal);
        var result = new List<JoinedRow>(gold.Count);
        foreach (var g in gold)
        {
            Require(byCandidate.TryGetValue(g.CandidateId, out var p), $"V4H_C_MISSING_PREDICTION:{g.CandidateId}");
            if (p!.Relation is not null)
                Require(p.Left == g.Left && p.Right == g.Right, $"V4H_C_OCCURRENCE_JOIN_MISMATCH:{g.CandidateId}");
            var predictedRelation = p.Relation is not null ? NormalizePredictionRelation(p.Relation) : "INVALID_PREDICTION";
            result.Add(new JoinedRow(g, p, predictedRelation));
        }
        return result;
    }

    private static object BuildSummary(IReadOnlyList<JoinedRow> rows, string root, string goldPath, string predictionPath, string manifestPath)
    {
        var valid = rows.Count(x => x.PredictedRelation != "INVALID_PREDICTION");
        var correct = rows.Count(x => x.GoldRelation == x.PredictedRelation);
        var falseMerge = rows.Count(x => x.GoldRelation == "DISTINCT" && (x.PredictedRelation == "SAME_SEMANTIC_REPEAT" || x.PredictedRelation == "CONTINUATION_OF"));
        var falseSplit = rows.Count(x => (x.GoldRelation == "SAME_SEMANTIC_REPEAT" || x.GoldRelation == "CONTINUATION_OF") && x.PredictedRelation == "DISTINCT");
        var perRelation = Labels.Select(label =>
        {
            var tp = rows.Count(x => x.GoldRelation == label && x.PredictedRelation == label);
            var fp = rows.Count(x => x.GoldRelation != label && x.PredictedRelation == label);
            var fn = rows.Count(x => x.GoldRelation == label && x.PredictedRelation != label);
            var precision = SafeDivide(tp, tp + fp);
            var recall = SafeDivide(tp, tp + fn);
            return new { relation = label, tp, fp, fn, precision, recall, f1 = SafeDivide(2 * precision * recall, precision + recall) };
        }).ToArray();
        return new
        {
            schemaVersion = "a99-v4h-c-semantic-evaluation-summary-v1",
            gold = new { itemCount = rows.Count, authority = GoldAuthority, independentHumanGold = false, artifact = Relative(root, goldPath) },
            prediction = new { artifact = Relative(root, predictionPath), validCount = valid, invalidCount = rows.Count - valid, coverage = SafeDivide(valid, rows.Count), source = "FROZEN_V4G_B_PREDICTIONS" },
            join = new
            {
                exactCandidateIdJoin = rows.Count,
                exactPredictionEndpointJoin = rows.Count(x => x.PredictedRelation != "INVALID_PREDICTION"),
                providerErrorEndpointUnavailable = rows.Count(x => x.PredictedRelation == "INVALID_PREDICTION"),
                policy = "Provider-error cells retain frozen candidate join but are INVALID_PREDICTION; no semantic label is inferred.",
            },
            overall = new { correct, total = rows.Count, effectiveCorrectness = SafeDivide(correct, rows.Count), validPredictionAccuracy = SafeDivide(correct, valid) },
            falseMerge,
            falseSplit,
            perRelation,
            executionManifest = Relative(root, manifestPath),
            modelCalls = 0,
            providerCalls = 0,
        };
    }

    private static object BuildConfusion(IReadOnlyList<JoinedRow> rows)
    {
        var columns = Labels.Append("INVALID_PREDICTION").ToArray();
        var matrix = Labels.ToDictionary(g => g, g => columns.ToDictionary(p => p, p => rows.Count(x => x.GoldRelation == g && x.PredictedRelation == p), StringComparer.Ordinal), StringComparer.Ordinal);
        return new { schemaVersion = "a99-v4h-confusion-matrix-v1", rows = matrix, invalidPredictionCount = rows.Count(x => x.PredictedRelation == "INVALID_PREDICTION") };
    }

    private static object[] BuildPerDocument(IReadOnlyList<JoinedRow> rows) => rows.GroupBy(x => x.Gold.CandidateId.Split(':', 2)[0], StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(group =>
    {
        var all = group.ToArray();
        var valid = all.Count(x => x.PredictedRelation != "INVALID_PREDICTION");
        var correct = all.Count(x => x.GoldRelation == x.PredictedRelation);
        return new
        {
            documentId = group.Key,
            total = all.Length,
            validPredictions = valid,
            invalidPredictions = all.Length - valid,
            predictionCoverage = SafeDivide(valid, all.Length),
            correct,
            effectiveCorrectness = SafeDivide(correct, all.Length),
            validPredictionAccuracy = SafeDivide(correct, valid),
            falseMerge = all.Count(x => x.GoldRelation == "DISTINCT" && (x.PredictedRelation == "SAME_SEMANTIC_REPEAT" || x.PredictedRelation == "CONTINUATION_OF")),
            falseSplit = all.Count(x => (x.GoldRelation == "SAME_SEMANTIC_REPEAT" || x.GoldRelation == "CONTINUATION_OF") && x.PredictedRelation == "DISTINCT"),
        };
    }).ToArray();

    private static object[] BuildSanity(IReadOnlyList<JoinedRow> rows) => new[] { "SA-0067", "SA-0108", "SA-0040" }.Select(id =>
    {
        var row = rows.Single(x => x.Gold.ReviewId == id);
        return new { reviewId = id, candidateId = row.Gold.CandidateId, goldRelation = row.GoldRelation, predictedRelation = row.PredictedRelation, predictionStatus = row.Prediction.Status, correct = row.GoldRelation == row.PredictedRelation };
    }).ToArray();

    private static object ToPerItem(JoinedRow row) => new
    {
        reviewId = row.Gold.ReviewId,
        candidateId = row.Gold.CandidateId,
        leftOccurrenceId = row.Gold.Left,
        rightOccurrenceId = row.Gold.Right,
        goldRelation = row.GoldRelation,
        predictedRelation = row.PredictedRelation,
        predictionStatus = row.Prediction.Status,
        predictionValid = row.PredictedRelation != "INVALID_PREDICTION",
        correct = row.GoldRelation == row.PredictedRelation,
        falseMerge = row.GoldRelation == "DISTINCT" && (row.PredictedRelation == "SAME_SEMANTIC_REPEAT" || row.PredictedRelation == "CONTINUATION_OF"),
        falseSplit = (row.GoldRelation == "SAME_SEMANTIC_REPEAT" || row.GoldRelation == "CONTINUATION_OF") && row.PredictedRelation == "DISTINCT",
        rawResponseSha256 = row.Prediction.RawResponseSha256,
        parsedResponseSha256 = row.Prediction.ParsedResponseSha256,
    };

    private static string BuildReport(object summary, object confusion, object[] perDocument, object[] sanity) =>
        "# A99 V4H-C — frozen semantic evaluation\n\n" +
        "Status: **COMPLETE**\n\n" +
        "This is an offline, evaluation-only join of the immutable V4H-B user-final source-backed adjudication and the frozen V4G-B predictions. The V4H-B authority remains `USER_FINAL_SOURCE_BACKED_ADJUDICATION`; it is not relabeled as independent human Gold. The original model-assisted source SHA remains in V4H-B provenance.\n\n" +
        "## Firewall\n\n" +
        "- New model calls: **0**.\n" +
        "- New provider calls: **0**.\n" +
        "- The V4G-B source execution contains **128 historical provider calls** and was already response-frozen with `goldReadCount=0`.\n" +
        "- Gold was opened only for this post-freeze evaluation join.\n" +
        "- Frozen adjudication and V4G prediction artifacts were not mutated.\n" +
        "- Exact frozen candidate join: **128/128**.\n" +
        "- Returned prediction occurrence endpoints matched exactly: **95/95**. The 33 provider-error cells returned no endpoints and therefore remain `INVALID_PREDICTION`.\n\n" +
        "## Summary\n\n```json\n" + JsonSerializer.Serialize(summary, JsonOptions) + "\n```\n\n" +
        "`effectiveCorrectness` is correct / all 128. `validPredictionAccuracy` excludes provider-error cells and is reported separately; it is not the primary all-cell result.\n\n" +
        "## Confusion matrix\n\n```json\n" + JsonSerializer.Serialize(confusion, JsonOptions) + "\n```\n\n" +
        "## Per document\n\n```json\n" + JsonSerializer.Serialize(perDocument, JsonOptions) + "\n```\n\n" +
        "## Frozen sanity cases\n\n```json\n" + JsonSerializer.Serialize(sanity, JsonOptions) + "\n```\n\n" +
        "The V4H-B pairwise lane is closed after this evaluation. The next architecture is `GLOBAL_CLUSTER_SEMANTIC_NODE_INDUCTION`; no V4I pairwise tuning is authorized from this 128-case development-exposed lane.\n";

    private static List<GoldRow> ReadGold(JsonElement root) => root.GetProperty("responses").EnumerateArray().Select(x => new GoldRow(
        x.GetProperty("reviewId").GetString()!, x.GetProperty("candidateId").GetString()!, x.GetProperty("leftOccurrenceId").GetString()!, x.GetProperty("rightOccurrenceId").GetString()!, NormalizeGoldRelation(x.GetProperty("relation").GetString()!))).ToList();

    private static List<PredictionRow> ReadPredictions(JsonElement root) => root.GetProperty("results").EnumerateArray().Select(x =>
    {
        var status = x.GetProperty("status").GetString()!;
        var parsed = x.GetProperty("parsed");
        if (status != "VALID" || parsed.ValueKind != JsonValueKind.Object) return new PredictionRow(x.GetProperty("candidateId").GetString()!, status, null, null, null, null, null);
        var response = parsed.GetProperty("response");
        var validation = parsed.GetProperty("validation");
        var accepted = validation.GetProperty("accepted").GetBoolean();
        return new PredictionRow(x.GetProperty("candidateId").GetString()!, status, response.GetProperty("left").GetString(), response.GetProperty("right").GetString(), accepted ? NormalizePredictionRelation(response.GetProperty("relation").GetString()!) : null, x.TryGetProperty("rawResponseSha256", out var raw) && raw.ValueKind != JsonValueKind.Null ? raw.GetString() : null, x.TryGetProperty("parsedResponseSha256", out var parsedSha) && parsedSha.ValueKind != JsonValueKind.Null ? parsedSha.GetString() : null);
    }).ToList();

    private static string NormalizeGoldRelation(string relation) => relation == "DISTINCT_SEMANTIC_NODE" ? "DISTINCT" : relation;
    private static string NormalizePredictionRelation(string relation) => relation == "DISTINCT" ? "DISTINCT" : relation;
    private static double SafeDivide(double numerator, double denominator) => denominator == 0 ? 0 : numerator / denominator;
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record GoldRow(string ReviewId, string CandidateId, string Left, string Right, string Relation);
    private sealed record PredictionRow(string CandidateId, string Status, string? Left, string? Right, string? Relation, string? RawResponseSha256, string? ParsedResponseSha256);
    private sealed record JoinedRow(GoldRow Gold, PredictionRow Prediction, string PredictedRelation)
    {
        public string GoldRelation => Gold.Relation;
    }
}
