using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV6DR;

internal static class Program
{
    private const int ExpectedCount = 128;
    private const int ExpectedOccurrences = 226;
    private const string V6DExecutionRelative = "artifacts/identity-benchmark/v6/semantic-node-induction/execution-v1-owner-conditioned";
    private const string V6DPreflightRelative = "artifacts/identity-benchmark/v6/semantic-node-induction/preflight-v1-owner-conditioned";
    private const string GoldRelative = "artifacts/identity-benchmark/v4/semantic-adjudication/results/human-adjudication.frozen.v1.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v6/semantic-node-induction/evaluation-v6d-r";
    private const string GoldSha = "1b4c10fd20e0840a272611065bfcff524335542ea5d5b71cfe16bae692eaae16";
    private const string V6DCommit = "b3df34e";
    private static readonly string[] Labels = [V6DRContracts.Same, V6DRContracts.Continuation, V6DRContracts.Distinct];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try { await RunAsync(root); Console.WriteLine("V6D_R_STATUS=OFFLINE_DEV_REGRESSION_COMPLETE MODEL_CALLS=0 PROVIDER_CALLS=0"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"V6D_R_ERROR={ex}"); return 2; }
    }

    private static async Task RunAsync(string root)
    {
        var execution = Full(root, V6DExecutionRelative);
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);
        using var attemptsDoc = Read(Path.Combine(execution, "attempt-manifest.json"));
        using var executionManifest = Read(Path.Combine(execution, "execution-manifest.json"));
        using var requestsDoc = Read(Path.Combine(Full(root, V6DPreflightRelative), "requests.json"));
        var attempts = attemptsDoc.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
        ValidateV6DFreeze(executionManifest.RootElement, attempts);
        var handleMaps = LoadHandleMaps(requestsDoc.RootElement);

        // Freeze the V6D projection before opening the 128-case authority.
        var freeze = BuildPredictionFreeze(attempts, handleMaps, root, execution);
        await WriteAsync(Path.Combine(output, "prediction-freeze.json"), freeze);

        using var gold = Read(Full(root, GoldRelative));
        Require(Sha256File(Full(root, GoldRelative)).Equals(GoldSha, StringComparison.OrdinalIgnoreCase), "V6D_R_GOLD_SHA_DRIFT");
        ValidateGold(gold.RootElement);
        var evaluated = Evaluate(freeze, gold.RootElement, root);
        await WriteAsync(Path.Combine(output, "pair-results.json"), evaluated.PairResults);
        await WriteAsync(Path.Combine(output, "failure-ownership.json"), evaluated.FailureOwnership);
        await WriteAsync(Path.Combine(output, "summary.json"), evaluated.Summary);
        await WriteAsync(Path.Combine(output, "comparison.json"), BaselineComparison(evaluated.Summary));
        await WriteAsync(Path.Combine(output, "manifest.json"), evaluated.Manifest);
        using (var pairDocument = JsonDocument.Parse(JsonSerializer.Serialize(evaluated.PairResults, JsonOptions)))
            await WriteAsync(Path.Combine(output, "forensic-v1.json"), BuildForensic(root, pairDocument.RootElement));
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(evaluated.Summary, evaluated.FailureOwnership), new UTF8Encoding(false));
    }

    private static object BuildPredictionFreeze(JsonElement[] attempts, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> handleMaps, string root, string execution)
    {
        var docs = attempts.OrderBy(x => x.GetProperty("documentId").GetString(), StringComparer.Ordinal).Select(attempt =>
        {
            var status = attempt.GetProperty("status").GetString()!;
            var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
            var edges = new List<object>();
            if (status == "VALID")
            {
                var response = attempt.GetProperty("response").GetProperty("response");
                var map = handleMaps[attempt.GetProperty("documentId").GetString()!];
                foreach (var item in response.GetProperty("assignments").EnumerateArray())
                {
                    var reference = item.GetProperty("ref").GetString()!;
                    assignments[map[reference]] = item.GetProperty("node").GetString()!;
                }
                foreach (var edge in response.GetProperty("continuationEdges").EnumerateArray())
                {
                    var from = edge.GetProperty("from").GetString()!;
                    var to = edge.GetProperty("to").GetString()!;
                    edges.Add(new { from = map[from], to = map[to] });
                }
            }
            return new
            {
                documentId = attempt.GetProperty("documentId").GetString(), status,
                requestHash = attempt.GetProperty("requestHash").GetString(),
                rawResponseSha256 = attempt.TryGetProperty("rawResponseSha256", out var raw) ? raw.GetString() : null,
                parsedResponseSha256 = attempt.TryGetProperty("parsedResponseSha256", out var parsed) ? parsed.GetString() : null,
                assignments, continuationEdges = edges,
                error = attempt.TryGetProperty("error", out var error) ? error.GetString() : null,
            };
        }).ToArray();
        Require(docs.Length == 3, "V6D_R_FREEZE_DOCUMENT_COUNT");
        return new
        {
            schemaVersion = "a99-v6d-r-prediction-freeze-v1",
            status = "V6D_R_PREDICTIONS_FROZEN_BEFORE_GOLD",
            sourceCommit = V6DCommit,
            sourceExecution = Relative(root, execution),
            sourceExecutionManifestSha256 = Sha256File(Path.Combine(execution, "execution-manifest.json")),
            sourceAttemptManifestSha256 = Sha256File(Path.Combine(execution, "attempt-manifest.json")),
            goldReadCount = 0,
            providerCallsDuringEvaluation = 0,
            predictionMutations = 0,
            generalizationClaim = false,
            targetOccurrenceDenominator = ExpectedOccurrences,
            derivationPolicy = "Invalid document first; different validated nodes => DISTINCT; exact directional edge in either direction => CONTINUATION_OF; otherwise SAME_SEMANTIC_REPEAT. Role alone never derives continuation.",
            documents = docs,
        };
    }

    private static Evaluation Evaluate(object freezeObject, JsonElement gold, string root)
    {
        using var freeze = JsonDocument.Parse(JsonSerializer.Serialize(freezeObject));
        var byDoc = freeze.RootElement.GetProperty("documents").EnumerateArray().ToDictionary(x => x.GetProperty("documentId").GetString()!, StringComparer.Ordinal);
        var rows = new List<Row>();
        foreach (var g in gold.GetProperty("responses").EnumerateArray())
        {
            var left = g.GetProperty("leftOccurrenceId").GetString()!;
            var right = g.GetProperty("rightOccurrenceId").GetString()!;
            var doc = left.Split(':', 2)[0];
            Require(byDoc.ContainsKey(doc), $"V6D_R_UNKNOWN_DOCUMENT:{doc}");
            var p = byDoc[doc];
            var goldLabel = NormalizeGold(g.GetProperty("relation").GetString()!);
            var goldFrom = g.TryGetProperty("continuedFromOccurrenceId", out var gf) ? gf.GetString() : null;
            var goldTo = g.TryGetProperty("continuationSourceOccurrenceId", out var gt) ? gt.GetString() : null;
            var assignments = p.GetProperty("assignments").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal);
            var edges = p.GetProperty("continuationEdges").EnumerateArray().Select(x => (From: x.GetProperty("from").GetString()!, To: x.GetProperty("to").GetString()!)).ToHashSet();
            var direction = (From: (string?)null, To: (string?)null);
            var predicted = V6DRContracts.Derive(p.GetProperty("status").GetString()!, assignments, edges, left, right, out direction);
            var ownership = predicted == V6DRContracts.Invalid
                ? (p.GetProperty("status").GetString() == "PROVIDER_ERROR" ? "INVALID_PROVIDER" : "INVALID_V6D_DOCUMENT_VALIDATION")
                : V6DRContracts.FailureOwnership(goldLabel, predicted);
            rows.Add(new Row(g.GetProperty("reviewId").GetString()!, g.GetProperty("candidateId").GetString()!, doc, left, right, goldLabel, predicted, ownership, p.GetProperty("status").GetString()!, goldFrom, goldTo, direction.From, direction.To));
        }
        Require(rows.Count == ExpectedCount, "V6D_R_GOLD_COUNT");
        return BuildEvaluation(rows, root);
    }

    private static Evaluation BuildEvaluation(IReadOnlyList<Row> rows, string root)
    {
        var valid = rows.Count(x => x.Predicted != V6DRContracts.Invalid);
        var correct = rows.Count(x => x.IsCorrect);
        var falseMerge = rows.Count(x => x.Gold == V6DRContracts.Distinct && x.Predicted is V6DRContracts.Same or V6DRContracts.Continuation);
        var falseSplit = rows.Count(x => x.Gold is V6DRContracts.Same or V6DRContracts.Continuation && x.Predicted == V6DRContracts.Distinct);
        var perRelation = Labels.Select(label =>
        {
            var tp = rows.Count(x => x.Gold == label && x.IsCorrect);
            var fp = rows.Count(x => x.Predicted == label && !x.IsCorrect);
            var fn = rows.Count(x => x.Gold == label && !x.IsCorrect);
            var precision = Divide(tp, tp + fp); var recall = Divide(tp, tp + fn);
            return new { relation = label, tp, fp, fn, precision, recall, f1 = Divide(2 * precision * recall, precision + recall) };
        }).ToArray();
        var nodeCorrect = rows.Count(x => x.NodeConstraintCorrect && x.Predicted != V6DRContracts.Invalid);
        var ownership = rows.GroupBy(x => x.Ownership, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => new { category = g.Key, count = g.Count(), reviewIds = g.Select(x => x.ReviewId).OrderBy(x => x, StringComparer.Ordinal).ToArray() }).ToArray();
        var perDocument = rows.GroupBy(x => x.DocumentId, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => new { documentId = g.Key, total = g.Count(), validCoverage = g.Count(x => x.Predicted != V6DRContracts.Invalid), correct = g.Count(x => x.IsCorrect), falseMerge = g.Count(x => x.Gold == V6DRContracts.Distinct && x.Predicted is V6DRContracts.Same or V6DRContracts.Continuation), falseSplit = g.Count(x => x.Gold is V6DRContracts.Same or V6DRContracts.Continuation && x.Predicted == V6DRContracts.Distinct), continuationOnlyErrors = g.Count(x => x.Ownership == "WRONG_CONTINUATION_EDGE") }).ToArray();
        var summary = new
        {
            schemaVersion = "a99-v6d-r-summary-v1", phase = "V6D_R_OWNER_CONDITIONED_SEMANTIC_NODE_DEV_REGRESSION",
            generalizationClaim = false, total = rows.Count, validCoverage = new { count = valid, denominator = rows.Count, fraction = Divide(valid, rows.Count) },
            correct, effectiveCorrectness = Divide(correct, rows.Count), accuracyOnValid = Divide(correct, valid), falseMerge, falseSplit,
            continuationOnlyErrors = rows.Count(x => x.Ownership == "WRONG_CONTINUATION_EDGE"),
            nodeConstraint = new { correct = nodeCorrect, total = valid, accuracy = Divide(nodeCorrect, valid) }, perRelation, perDocument,
            modelCalls = 0, providerCalls = 0, predictionMutations = 0,
        };
        var pairResults = rows.Select(x => new { x.ReviewId, x.CandidateId, x.DocumentId, x.Left, x.Right, goldRelation = x.Gold, predictedRelation = x.Predicted, x.Ownership, predictionStatus = x.Status, predictionValid = x.Predicted != V6DRContracts.Invalid, goldContinuationFrom = x.GoldFrom, goldContinuationTo = x.GoldTo, predictedContinuationFrom = x.PredictedFrom, predictedContinuationTo = x.PredictedTo, nodeConstraintCorrect = x.NodeConstraintCorrect, correct = x.IsCorrect }).ToArray();
        var manifest = new { schemaVersion = "a99-v6d-r-manifest-v1", status = "V6D_R_COMPLETE", generalizationClaim = false, goldReadAfterPredictionFreeze = true, goldSha256 = Sha256File(Full(root, GoldRelative)), goldAuthority = "USER_FINAL_SOURCE_BACKED_ADJUDICATION", independentHumanGold = false, itemCount = rows.Count, providerCalls = 0, modelCalls = 0, predictionMutations = 0, sourceV6DCommit = V6DCommit };
        return new Evaluation(manifest, summary, pairResults, ownership);
    }

    private static object BaselineComparison(object v6d) => new
    {
        v4hC = new { validCoverage = 95d / 128, correct = 71, effectiveCorrectness = 71d / 128, accuracyOnValid = 71d / 95, falseMerge = 23, falseSplit = 1, sameF1 = .6285714285714286, continuationF1 = .2857142857142857, distinctF1 = .6575342465753425 },
        v5c = new { validCoverage = 103d / 128, correct = 46, effectiveCorrectness = 46d / 128, accuracyOnValid = 46d / 103, falseMerge = 55, falseSplit = 1, nodeConstraintAccuracy = 47d / 103 },
        v6dR = v6d,
        baselinePolicy = "Comparison only; no baseline artifact or frozen V6D prediction was modified.",
    };

    private static void ValidateV6DFreeze(JsonElement manifest, JsonElement[] attempts)
    {
        Require(manifest.GetProperty("status").GetString() == "COMPLETE_SEMANTIC_NODE_PREDICTION_FREEZE", "V6D_R_FREEZE_STATUS");
        Require(manifest.GetProperty("requestSetSha256").GetString() == "710452c4bd3b7a695f8078018e8f5b287730ff6f9f9300ca5efcc7c7eeea6637", "V6D_R_REQUEST_SET_DRIFT");
        Require(manifest.GetProperty("completedCalls").GetInt32() == 3 && manifest.GetProperty("transientRequestRetries").GetInt32() == 0, "V6D_R_CALL_COUNT");
        Require(manifest.GetProperty("goldReadCount").GetInt32() == 0 && manifest.GetProperty("v5cReadCount").GetInt32() == 0 && manifest.GetProperty("v6aReadCount").GetInt32() == 0 && manifest.GetProperty("v5PredictionReadCount").GetInt32() == 0, "V6D_R_SOURCE_FIREWALL");
        Require(attempts.Length == 3 && attempts.Select(x => x.GetProperty("documentId").GetString()).SequenceEqual(["DOC-0123", "DOC-0133", "DOC-0252"], StringComparer.Ordinal), "V6D_R_DOCUMENTS");
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadHandleMaps(JsonElement root)
    {
        Require(root.GetProperty("requestSetSha256").GetString() == "710452c4bd3b7a695f8078018e8f5b287730ff6f9f9300ca5efcc7c7eeea6637", "V6D_R_PREFLIGHT_REQUEST_SET_DRIFT");
        return root.GetProperty("records").EnumerateArray().ToDictionary(
            record => record.GetProperty("documentId").GetString()!,
            record => (IReadOnlyDictionary<string, string>)record.GetProperty("request").GetProperty("occurrences").EnumerateArray().ToDictionary(
                occurrence => occurrence.GetProperty("ref").GetString()!,
                occurrence => occurrence.GetProperty("sourceOccurrenceId").GetString()!,
                StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private static void ValidateGold(JsonElement gold) => Require(gold.GetProperty("status").GetString() == "BLINDED_SOURCE_BACKED_USER_ADJUDICATION_FROZEN" && gold.GetProperty("itemCount").GetInt32() == ExpectedCount, "V6D_R_GOLD_NOT_FROZEN");

    private static object BuildForensic(string root, JsonElement pairResults)
    {
        using var requests = Read(Path.Combine(Full(root, V6DPreflightRelative), "requests.json"));
        var ownerByOccurrence = new Dictionary<string, string>(StringComparer.Ordinal);
        var scopeByOccurrence = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in requests.RootElement.GetProperty("records").EnumerateArray())
        {
            var request = record.GetProperty("request");
            var sourceByRef = request.GetProperty("occurrences").EnumerateArray().ToDictionary(x => x.GetProperty("ref").GetString()!, x => x.GetProperty("sourceOccurrenceId").GetString()!, StringComparer.Ordinal);
            var scopeByRef = request.GetProperty("occurrences").EnumerateArray().ToDictionary(x => x.GetProperty("ref").GetString()!, x => x.GetProperty("sourceContainerIdentity").GetString()!, StringComparer.Ordinal);
            foreach (var assignment in request.GetProperty("ownerAssignments").EnumerateArray()) ownerByOccurrence[sourceByRef[assignment.GetProperty("ref").GetString()!]] = assignment.GetProperty("owner").GetString()!;
            foreach (var item in scopeByRef) scopeByOccurrence[sourceByRef[item.Key]] = item.Value;
        }
        var rows = pairResults.EnumerateArray().Where(x => x.GetProperty("predictionValid").GetBoolean()).Select(x =>
        {
            var left = x.GetProperty("left").GetString()!; var right = x.GetProperty("right").GetString()!;
            return new
            {
                reviewId = x.GetProperty("reviewId").GetString(), documentId = x.GetProperty("documentId").GetString(),
                goldRelation = x.GetProperty("goldRelation").GetString(), predictedRelation = x.GetProperty("predictedRelation").GetString(),
                failureOwnership = x.GetProperty("ownership").GetString(),
                ownerRelation = ownerByOccurrence.GetValueOrDefault(left) == ownerByOccurrence.GetValueOrDefault(right) ? "SAME_OWNER" : "CROSS_OWNER",
                scopeRelation = scopeByOccurrence.GetValueOrDefault(left) == scopeByOccurrence.GetValueOrDefault(right) ? "SAME_SCOPE" : "CROSS_SCOPE",
            };
        }).ToArray();
        var falseMerges = rows.Where(x => x.failureOwnership == "WRONG_NODE_MERGE").ToArray();
        var falseSplits = rows.Where(x => x.failureOwnership == "WRONG_NODE_SPLIT").ToArray();
        var continuationErrors = rows.Where(x => x.failureOwnership == "WRONG_CONTINUATION_EDGE").ToArray();
        return new
        {
            schemaVersion = "a99-v6d-r-forensic-v1",
            phase = "V6D_R_OFFLINE_FAILURE_FORENSIC",
            source = "FROZEN_V6D_R_PAIR_RESULTS",
            providerCalls = 0,
            predictionMutations = 0,
            goldDerivedRulesInstalled = false,
            validPairRows = rows.Length,
            falseMergeMechanisms = falseMerges.GroupBy(x => new { x.ownerRelation, x.scopeRelation }).Select(g => new { ownerRelation = g.Key.ownerRelation, scopeRelation = g.Key.scopeRelation, count = g.Count() }).OrderBy(x => x.ownerRelation).ThenBy(x => x.scopeRelation).ToArray(),
            falseSplitMechanisms = falseSplits.GroupBy(x => new { x.ownerRelation, x.scopeRelation }).Select(g => new { ownerRelation = g.Key.ownerRelation, scopeRelation = g.Key.scopeRelation, count = g.Count() }).OrderBy(x => x.ownerRelation).ThenBy(x => x.scopeRelation).ToArray(),
            continuationMechanisms = continuationErrors.GroupBy(x => new { x.ownerRelation, x.scopeRelation }).Select(g => new { ownerRelation = g.Key.ownerRelation, scopeRelation = g.Key.scopeRelation, count = g.Count() }).OrderBy(x => x.ownerRelation).ThenBy(x => x.scopeRelation).ToArray(),
            cases = new { falseMerges, falseSplits, continuationErrors },
            conclusion = "V6D owner conditioning did not prevent semantic over-grouping: all observed false merges are within the same owner and parser scope among valid pairs. This is diagnostic only; no owner-veto rule or Gold-derived rule is installed.",
        };
    }
    private static string NormalizeGold(string label) => label == "DISTINCT_SEMANTIC_NODE" ? V6DRContracts.Distinct : label;
    private static double Divide(double a, double b) => b == 0 ? 0 : a / b;
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static string BuildReport(object summary, object ownership) => "# A99 V6D-R — owner-conditioned semantic-node dev regression\n\nThis is a DEV-EXPOSED offline evaluation of frozen V6D predictions. `generalizationClaim=false`; no provider calls or prediction mutations occurred. DOC-0252 is fail-closed at document validation and no partial response is consumed.\n\n## Summary\n\n```json\n" + JsonSerializer.Serialize(summary, JsonOptions) + "\n```\n\n## Failure ownership\n\n```json\n" + JsonSerializer.Serialize(ownership, JsonOptions) + "\n```\n";

    private sealed record Row(string ReviewId, string CandidateId, string DocumentId, string Left, string Right, string Gold, string Predicted, string Ownership, string Status, string? GoldFrom, string? GoldTo, string? PredictedFrom, string? PredictedTo)
    {
        public bool IsCorrect => Gold == Predicted && (Gold != V6DRContracts.Continuation || (GoldFrom == PredictedFrom && GoldTo == PredictedTo));
        public bool NodeConstraintCorrect => V6DRContracts.NodeConstraint(Gold, Predicted);
    }
    private sealed record Evaluation(object Manifest, object Summary, object PairResults, object FailureOwnership);
}
