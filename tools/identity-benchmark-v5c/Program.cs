using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV5C;

internal static class Program
{
    private const int ExpectedCount = 128;
    private const string V5AClusters = "artifacts/identity-benchmark/v5/source-only-clusters/freeze/clusters.json";
    private const string V5BRequests = "artifacts/identity-benchmark/v5/semantic-node-induction/preflight/requests.json";
    private const string V5BSummary = "artifacts/identity-benchmark/v5/semantic-node-induction/execution/prediction-summary.json";
    private const string V5BAttempts = "artifacts/identity-benchmark/v5/semantic-node-induction/execution/attempt-manifest.json";
    private const string GoldRelative = "artifacts/identity-benchmark/v4/semantic-adjudication/results/human-adjudication.frozen.v1.json";
    private const string BaselineSummary = "artifacts/identity-benchmark/v4/semantic-adjudication/evaluation/summary.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v5/semantic-node-induction/evaluation-v2";
    private const string GoldSha = "1b4c10fd20e0840a272611065bfcff524335542ea5d5b71cfe16bae692eaae16";
    private static readonly string[] Labels = ["SAME_SEMANTIC_REPEAT", "CONTINUATION_OF", "DISTINCT"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V5C_STATUS=OFFLINE_GRAPH_TO_PAIR_EVALUATION_COMPLETE MODEL_CALLS=0 PROVIDER_CALLS=0 GOLD_READ_AFTER_PREDICTION_FREEZE=true");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V5C_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);

        // Phase 1: intentionally does not open Gold. It freezes only the V5B projection.
        var clusters = Read(Full(root, V5AClusters));
        var requests = Read(Full(root, V5BRequests));
        var summary = Read(Full(root, V5BSummary));
        var attempts = Read(Full(root, V5BAttempts));
        ValidateV5B(summary.RootElement, attempts.RootElement, clusters.RootElement, requests.RootElement);
        var frozen = BuildPredictionFreeze(clusters.RootElement, requests.RootElement, attempts.RootElement, root);
        await WriteAsync(Path.Combine(output, "prediction-freeze.json"), frozen);

        // Phase 2: Gold is opened only after prediction-freeze.json exists.
        var gold = Read(Full(root, GoldRelative));
        Require(Sha256File(Full(root, GoldRelative)).Equals(GoldSha, StringComparison.OrdinalIgnoreCase), "V5C_GOLD_SHA_DRIFT");
        ValidateGold(gold.RootElement);
        var evaluated = Evaluate(frozen, gold.RootElement, root);
        var baseline = Read(Full(root, BaselineSummary));

        await WriteAsync(Path.Combine(output, "manifest.json"), evaluated.Manifest);
        await WriteAsync(Path.Combine(output, "summary.json"), evaluated.Summary);
        await WriteAsync(Path.Combine(output, "confusion-matrix.json"), evaluated.Confusion);
        await WriteAsync(Path.Combine(output, "per-document.json"), evaluated.PerDocument);
        await WriteAsync(Path.Combine(output, "per-item.json"), evaluated.PerItem);
        await WriteAsync(Path.Combine(output, "failure-ownership.json"), evaluated.FailureOwnership);
        await WriteAsync(Path.Combine(output, "baseline-comparison.json"), new { v4hC = baseline.RootElement, v5c = evaluated.Summary });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(evaluated.Summary, evaluated.Confusion, evaluated.FailureOwnership), new UTF8Encoding(false));
    }

    private static object BuildPredictionFreeze(JsonElement clusters, JsonElement requests, JsonElement attempts, string root)
    {
        var requestByCluster = requests.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("clusterId").GetString()!, StringComparer.Ordinal);
        var attemptByCluster = attempts.GetProperty("attempts").EnumerateArray().ToDictionary(x => x.GetProperty("clusterId").GetString()!, StringComparer.Ordinal);
        var candidates = new List<object>();
        foreach (var cluster in clusters.GetProperty("clusters").EnumerateArray())
        {
            var clusterId = cluster.GetProperty("clusterId").GetString()!;
            var attempt = attemptByCluster[clusterId];
            var valid = attempt.GetProperty("status").GetString() == "VALID";
            var assignment = new Dictionary<string, string>(StringComparer.Ordinal);
            var edges = new List<object>();
            if (valid)
            {
                var response = attempt.GetProperty("response").GetProperty("response");
                foreach (var item in response.GetProperty("occurrenceAssignments").EnumerateArray())
                    assignment[item.GetProperty("occurrenceId").GetString()!] = item.GetProperty("semanticNodeLocalId").GetString()!;
                foreach (var edge in response.GetProperty("continuationEdges").EnumerateArray())
                    edges.Add(new { fromOccurrenceId = edge.GetProperty("fromOccurrenceId").GetString()!, toOccurrenceId = edge.GetProperty("toOccurrenceId").GetString()! });
            }
            foreach (var candidateId in cluster.GetProperty("candidateIds").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal))
            {
                candidates.Add(new
                {
                    candidateId,
                    clusterId,
                    clusterStatus = attempt.GetProperty("status").GetString(),
                    requestHash = attempt.GetProperty("requestHash").GetString(),
                    rawResponseSha256 = attempt.TryGetProperty("rawResponseSha256", out var raw) ? raw.GetString() : null,
                    parsedResponseSha256 = attempt.TryGetProperty("parsedResponseSha256", out var parsed) ? parsed.GetString() : null,
                    assignments = assignment,
                    continuationEdges = edges,
                });
            }
        }
        Require(candidates.Count == ExpectedCount, "V5C_CANDIDATE_COUNT");
        return new
        {
            schemaVersion = "a99-v5c-prediction-freeze-v1",
            status = "V5C_PREDICTIONS_FROZEN_BEFORE_GOLD",
            sourceClusters = Relative(root, Full(root, V5AClusters)),
            sourceRequests = Relative(root, Full(root, V5BRequests)),
            sourceAttempts = Relative(root, Full(root, V5BAttempts)),
            candidateCount = candidates.Count,
            goldReadCount = 0,
            goldEvaluationOpened = false,
            pairLabelsDerived = false,
            modelCalls = 0,
            providerCalls = 0,
            candidates,
        };
    }

    private static Evaluation Evaluate(object frozenObject, JsonElement gold, string root)
    {
        using var frozenDoc = JsonDocument.Parse(JsonSerializer.Serialize(frozenObject));
        var byCandidate = frozenDoc.RootElement.GetProperty("candidates").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var rows = new List<Row>();
        foreach (var g in gold.GetProperty("responses").EnumerateArray())
        {
            var candidateId = g.GetProperty("candidateId").GetString()!;
            Require(byCandidate.TryGetValue(candidateId, out var p), $"V5C_MISSING_CANDIDATE:{candidateId}");
            var goldLabel = NormalizeGold(g.GetProperty("relation").GetString()!);
            var goldFrom = g.TryGetProperty("continuedFromOccurrenceId", out var goldFromElement) ? goldFromElement.GetString() : null;
            var goldTo = g.TryGetProperty("continuationSourceOccurrenceId", out var goldToElement) ? goldToElement.GetString() : null;
            var status = p.GetProperty("clusterStatus").GetString()!;
            var predicted = "INVALID_PREDICTION";
            string? predictedFrom = null;
            string? predictedTo = null;
            var ownership = status == "PROVIDER_ERROR" ? "INVALID_PROVIDER" : "INVALID_CLUSTER_VALIDATION";
            if (status == "VALID")
            {
                var left = g.GetProperty("leftOccurrenceId").GetString()!;
                var right = g.GetProperty("rightOccurrenceId").GetString()!;
                var assignments = p.GetProperty("assignments");
                var hasLeft = assignments.TryGetProperty(left, out var leftNode);
                var hasRight = assignments.TryGetProperty(right, out var rightNode);
                if (!hasLeft || !hasRight)
                {
                    ownership = "INVALID_CLUSTER_VALIDATION";
                }
                else if (leftNode.GetString() != rightNode.GetString())
                {
                    predicted = "DISTINCT";
                    ownership = RelationMatches(goldLabel, goldFrom, goldTo, predicted, null, null) ? "CORRECT" : FailureOwnership(goldLabel, predicted, null, null, goldFrom, goldTo);
                }
                else if (TryGetEdge(p.GetProperty("continuationEdges"), left, right, out var edgeFrom, out var edgeTo))
                {
                    predicted = "CONTINUATION_OF";
                    predictedFrom = edgeFrom;
                    predictedTo = edgeTo;
                    ownership = RelationMatches(goldLabel, goldFrom, goldTo, predicted, predictedFrom, predictedTo) ? "CORRECT" : FailureOwnership(goldLabel, predicted, predictedFrom, predictedTo, goldFrom, goldTo);
                }
                else
                {
                    predicted = "SAME_SEMANTIC_REPEAT";
                    ownership = RelationMatches(goldLabel, goldFrom, goldTo, predicted, null, null) ? "CORRECT" : FailureOwnership(goldLabel, predicted, null, null, goldFrom, goldTo);
                }
            }
            rows.Add(new Row(g.GetProperty("reviewId").GetString()!, candidateId, g.GetProperty("leftOccurrenceId").GetString()!, g.GetProperty("rightOccurrenceId").GetString()!, goldLabel, predicted, ownership, status, goldFrom, goldTo, predictedFrom, predictedTo));
        }
        return BuildEvaluation(rows, root);
    }

    private static Evaluation BuildEvaluation(IReadOnlyList<Row> rows, string root)
    {
        var valid = rows.Count(x => x.Predicted != "INVALID_PREDICTION");
        var correct = rows.Count(x => x.IsCorrect);
        var falseMerge = rows.Count(x => x.Gold == "DISTINCT" && x.Predicted is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF");
        var falseSplit = rows.Count(x => x.Gold is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF" && x.Predicted == "DISTINCT");
        var perRelation = Labels.Select(label =>
        {
            var tp = rows.Count(x => x.Gold == label && x.IsCorrect);
            var fp = rows.Count(x => x.Predicted == label && !x.IsCorrect);
            var fn = rows.Count(x => x.Gold == label && !x.IsCorrect);
            var precision = Divide(tp, tp + fp);
            var recall = Divide(tp, tp + fn);
            return new { relation = label, tp, fp, fn, precision, recall, f1 = Divide(2 * precision * recall, precision + recall) };
        }).ToArray();
        var summary = new
        {
            schemaVersion = "a99-v5c-semantic-node-evaluation-summary-v2",
            phase = "V5C_GRAPH_TO_PAIR_EVALUATION_V2",
            gold = new { itemCount = rows.Count, authority = "USER_FINAL_SOURCE_BACKED_ADJUDICATION", independentHumanGold = false },
            prediction = new { validCount = valid, invalidCount = rows.Count - valid, coverage = Divide(valid, rows.Count), source = "FROZEN_V5B_CLUSTER_INDUCTION" },
            overall = new { correct, total = rows.Count, effectiveCorrectness = Divide(correct, rows.Count), validPredictionAccuracy = Divide(correct, valid) },
            falseMerge, falseSplit,
            continuationOnlyErrors = rows.Count(x => x.Ownership == "WRONG_CONTINUATION_EDGE"),
            nodeConstraint = new { correct = rows.Count(x => x.Predicted != "INVALID_PREDICTION" && x.NodeConstraintCorrect), validPairs = valid, accuracy = Divide(rows.Count(x => x.Predicted != "INVALID_PREDICTION" && x.NodeConstraintCorrect), valid) },
            derivationPolicy = "INVALID cluster first; DISTINCT for different nodes; CONTINUATION_OF for an exact edge in either direction; otherwise SAME_SEMANTIC_REPEAT. occurrenceRole alone is never used.",
            perRelation,
            modelCalls = 0, providerCalls = 0, goldReadBeforePredictionFreeze = false,
        };
        var confusionColumns = Labels.Append("INVALID_PREDICTION").ToArray();
        var confusion = new { rows = Labels.ToDictionary(g => g, g => confusionColumns.ToDictionary(p => p, p => rows.Count(x => x.Gold == g && x.Predicted == p), StringComparer.Ordinal), StringComparer.Ordinal), invalidPredictionCount = rows.Count(x => x.Predicted == "INVALID_PREDICTION") };
        var perDocument = rows.GroupBy(x => x.CandidateId.Split(':', 2)[0], StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => new { documentId = g.Key, total = g.Count(), validPredictions = g.Count(x => x.Predicted != "INVALID_PREDICTION"), correct = g.Count(x => x.IsCorrect), falseMerge = g.Count(x => x.Gold == "DISTINCT" && x.Predicted is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF"), falseSplit = g.Count(x => x.Gold is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF" && x.Predicted == "DISTINCT"), continuationOnlyErrors = g.Count(x => x.Ownership == "WRONG_CONTINUATION_EDGE") }).ToArray();
        var ownership = rows.GroupBy(x => x.Ownership, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => new { category = g.Key, count = g.Count(), items = g.Select(x => x.ReviewId).OrderBy(x => x, StringComparer.Ordinal).ToArray() }).ToArray();
        var perItem = rows.Select(x => new { reviewId = x.ReviewId, candidateId = x.CandidateId, leftOccurrenceId = x.Left, rightOccurrenceId = x.Right, goldRelation = x.Gold, predictedRelation = x.Predicted, goldContinuationFrom = x.GoldFrom, goldContinuationTo = x.GoldTo, predictedContinuationFrom = x.PredictedFrom, predictedContinuationTo = x.PredictedTo, predictionStatus = x.Status, predictionValid = x.Predicted != "INVALID_PREDICTION", failureOwnership = x.Ownership, nodeConstraintCorrect = x.NodeConstraintCorrect, correct = x.IsCorrect }).ToArray();
        var manifest = new { schemaVersion = "a99-v5c-evaluation-manifest-v2", status = "V5C_V2_COMPLETE", goldReadAfterPredictionFreeze = true, predictionFreezeStatus = "V5C_PREDICTIONS_FROZEN_BEFORE_GOLD", itemCount = rows.Count, exactCandidateJoin = rows.Count, modelCalls = 0, providerCalls = 0, pairLabelsDerived = true, directionAgnosticEdgeDerivation = true, source = "FROZEN_V5B_PRIMARY_ATTEMPTS", priorEvaluationPreserved = "artifacts/identity-benchmark/v5/semantic-node-induction/evaluation" };
        return new Evaluation(manifest, summary, confusion, perDocument, perItem, ownership);
    }

    private static void ValidateV5B(JsonElement summary, JsonElement attempts, JsonElement clusters, JsonElement requests)
    {
        Require(summary.GetProperty("status").GetString() == "PREDICTIONS_FROZEN_BEFORE_GOLD", "V5C_V5B_NOT_FROZEN");
        Require(summary.GetProperty("goldReadCount").GetInt32() == 0, "V5C_V5B_GOLD_FIREWALL");
        Require(attempts.GetProperty("actualModelCalls").GetInt32() == 102 && attempts.GetProperty("actualProviderCalls").GetInt32() == 102, "V5C_V5B_CALL_COUNT");
        Require(attempts.GetProperty("transientRequestRetries").GetInt32() == 0, "V5C_V5B_RETRY_POLICY");
        Require(clusters.GetProperty("clusters").GetArrayLength() == 102 && requests.GetProperty("requests").GetArrayLength() == 102, "V5C_V5B_CLUSTER_COUNT");
    }

    private static void ValidateGold(JsonElement gold) => Require(gold.GetProperty("status").GetString() == "BLINDED_SOURCE_BACKED_USER_ADJUDICATION_FROZEN" && gold.GetProperty("itemCount").GetInt32() == ExpectedCount, "V5C_GOLD_NOT_FROZEN");
    private static bool TryGetEdge(JsonElement edges, string left, string right, out string? from, out string? to)
    {
        foreach (var edge in edges.EnumerateArray())
        {
            var edgeFrom = edge.GetProperty("fromOccurrenceId").GetString();
            var edgeTo = edge.GetProperty("toOccurrenceId").GetString();
            if ((edgeFrom == left && edgeTo == right) || (edgeFrom == right && edgeTo == left))
            {
                from = edgeFrom;
                to = edgeTo;
                return true;
            }
        }
        from = null;
        to = null;
        return false;
    }
    private static bool RelationMatches(string gold, string? goldFrom, string? goldTo, string predicted, string? predictedFrom, string? predictedTo) =>
        gold == predicted && (gold != "CONTINUATION_OF" || (goldFrom == predictedFrom && goldTo == predictedTo));
    private static string FailureOwnership(string gold, string predicted, string? predictedFrom, string? predictedTo, string? goldFrom, string? goldTo) =>
        gold == "DISTINCT" && predicted is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF" ? "WRONG_NODE_MERGE" :
        gold is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF" && predicted == "DISTINCT" ? "WRONG_NODE_SPLIT" :
        "WRONG_CONTINUATION_EDGE";
    private static string NormalizeGold(string label) => label == "DISTINCT_SEMANTIC_NODE" ? "DISTINCT" : label;
    private static double Divide(double a, double b) => b == 0 ? 0 : a / b;
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private sealed record Row(string ReviewId, string CandidateId, string Left, string Right, string Gold, string Predicted, string Ownership, string Status, string? GoldFrom, string? GoldTo, string? PredictedFrom, string? PredictedTo)
    {
        public bool IsCorrect => RelationMatches(Gold, GoldFrom, GoldTo, Predicted, PredictedFrom, PredictedTo);
        public bool NodeConstraintCorrect => Gold == "DISTINCT" ? Predicted == "DISTINCT" : Predicted is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF";
    }
    private sealed record Evaluation(object Manifest, object Summary, object Confusion, object PerDocument, object PerItem, object FailureOwnership);
    private static string BuildReport(object summary, object confusion, object ownership) => "# A99 V5C — graph to pair evaluation\n\n" + "V5C is an offline evaluation of the immutable V5B primary prediction freeze. Pair labels are derived deterministically from node assignments and exact directional continuation edges; occurrenceRole alone is never used. Gold was opened only after prediction-freeze.json was written.\n\n" + "## Summary\n\n```json\n" + JsonSerializer.Serialize(summary, JsonOptions) + "\n```\n\n## Failure ownership\n\n```json\n" + JsonSerializer.Serialize(ownership, JsonOptions) + "\n```\n\n## Confusion matrix\n\n```json\n" + JsonSerializer.Serialize(confusion, JsonOptions) + "\n```\n";
}
