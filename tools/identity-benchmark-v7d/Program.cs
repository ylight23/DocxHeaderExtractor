using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV7D;

internal static class Program
{
    private const string V7CRelative = "artifacts/identity-benchmark/v7/conservative-clustering/preflight-v1-clique-equivalence";
    private const string GoldRelative = "artifacts/identity-benchmark/v4/semantic-adjudication/results/human-adjudication.frozen.v1.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v7/conservative-clustering/evaluation-v1-dev-regression";
    private const string V7BRequestSetSha256 = "5af2dd92a27ff109a4bf929685f336fe9496d6c12602adfd1d3c6fe62e70cf45";
    private const string GoldSha256 = "1b4c10fd20e0840a272611065bfcff524335542ea5d5b71cfe16bae692eaae16";
    private const int ExpectedGoldCount = 128;
    private static readonly string[] Labels = ["SAME_SEMANTIC_REPEAT", "CONTINUATION_OF", "DISTINCT"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V7D_STATUS=OFFLINE_DEV_REGRESSION_COMPLETE MODEL_CALLS=0 PROVIDER_CALLS=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V7D_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var v7c = Full(root, V7CRelative); var output = Full(root, OutputRelative);
        Require(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(), "V7D_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");
        using var v7cManifest = Read(Path.Combine(v7c, "manifest.json"));
        using var v7cSummary = Read(Path.Combine(v7c, "summary.json"));
        using var v7cClusters = Read(Path.Combine(v7c, "clusters.json"));
        using var v7cRequests = Read(Path.Combine(root, "artifacts/identity-benchmark/v7/identity-verification/preflight-v1-independent-positive-edge/requests.json"));
        ValidateV7C(v7cManifest.RootElement, v7cSummary.RootElement);
        var assignments = BuildAssignments(v7cRequests.RootElement, v7cClusters.RootElement);
        Directory.CreateDirectory(output);
        var projection = new
        {
            schemaVersion = "a99-v7d-cluster-to-pair-projection-v1",
            status = "V7D_PROJECTION_FROZEN_BEFORE_GOLD_JOIN",
            rule = "same cluster => SAME_SEMANTIC_REPEAT; different cluster => DISTINCT_SEMANTIC_NODE",
            continuationPredictionCount = 0,
            continuationInferenceForbidden = true,
            invalidVerifierDocumentIsKeepSplit = true,
            goldReadCount = 0,
            providerCalls = 0,
            assignments,
            v7cSummarySha256 = Sha256File(Path.Combine(v7c, "summary.json")),
            v7cClustersSha256 = Sha256File(Path.Combine(v7c, "clusters.json")),
        };
        await WriteAsync(Path.Combine(output, "projection-freeze.json"), projection);

        var goldPath = Full(root, GoldRelative);
        using var gold = Read(goldPath);
        Require(gold.RootElement.GetProperty("status").GetString() == "BLINDED_SOURCE_BACKED_USER_ADJUDICATION_FROZEN", "V7D_GOLD_NOT_FROZEN");
        Require(gold.RootElement.GetProperty("authority").GetString() == "USER_FINAL_SOURCE_BACKED_ADJUDICATION", "V7D_GOLD_AUTHORITY_DRIFT");
        Require(!gold.RootElement.GetProperty("independentHumanGold").GetBoolean(), "V7D_INDEPENDENT_HUMAN_CLAIM_DRIFT");
        Require(gold.RootElement.GetProperty("itemCount").GetInt32() == ExpectedGoldCount && gold.RootElement.GetProperty("responses").GetArrayLength() == ExpectedGoldCount, "V7D_GOLD_COUNT");
        Require(Sha256File(goldPath).Equals(GoldSha256, StringComparison.OrdinalIgnoreCase), "V7D_GOLD_SHA_DRIFT");
        var rows = gold.RootElement.GetProperty("responses").EnumerateArray().Select(x =>
        {
            var doc = x.GetProperty("candidateId").GetString()!.Split(':', 2)[0];
            var left = x.GetProperty("leftOccurrenceId").GetString()!; var right = x.GetProperty("rightOccurrenceId").GetString()!;
            var leftCluster = assignments.Single(a => a.DocumentId == doc && a.SourceOccurrenceId == left).ClusterId;
            var rightCluster = assignments.Single(a => a.DocumentId == doc && a.SourceOccurrenceId == right).ClusterId;
            var goldRelation = NormalizeGold(x.GetProperty("relation").GetString()!);
            var predicted = leftCluster == rightCluster ? "SAME_SEMANTIC_REPEAT" : "DISTINCT";
            return new Row(x.GetProperty("reviewId").GetString()!, x.GetProperty("candidateId").GetString()!, doc, left, right, goldRelation, predicted, leftCluster == rightCluster);
        }).ToArray();
        var summary = BuildSummary(rows, root, goldPath, v7c);
        await WriteAsync(Path.Combine(output, "summary.json"), summary);
        await WriteAsync(Path.Combine(output, "confusion-matrix.json"), BuildConfusion(rows));
        await WriteAsync(Path.Combine(output, "per-document.json"), BuildPerDocument(rows));
        await WriteAsync(Path.Combine(output, "per-item.json"), rows);
        await WriteAsync(Path.Combine(output, "positive-edge-utility.json"), BuildPositiveEdgeUtility(root, rows));
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v7d-offline-dev-regression-manifest-v1",
            status = "V7D_COMPLETE_PROJECTION_FROZEN_BEFORE_GOLD_JOIN",
            projectionArtifact = "projection-freeze.json",
            goldArtifact = GoldRelative,
            goldSha256 = Sha256File(goldPath),
            goldReadAfterProjectionFreeze = true,
            goldAuthority = "USER_FINAL_SOURCE_BACKED_ADJUDICATION",
            independentHumanGold = false,
            itemCount = rows.Length,
            exactOccurrenceJoin = true,
            providerCalls = 0,
            modelCalls = 0,
            generalizationClaim = false,
            lane = "DEV_EXPOSED_V7D_REGRESSION",
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(summary), new UTF8Encoding(false));
    }

    private static IReadOnlyList<Assignment> BuildAssignments(JsonElement requests, JsonElement clusters)
    {
        var source = new Dictionary<string, (string DocumentId, string SourceOccurrenceId)>(StringComparer.Ordinal);
        foreach (var record in requests.GetProperty("requests").EnumerateArray())
        {
            var request = record.GetProperty("request"); var doc = request.GetProperty("documentId").GetString()!;
            foreach (var occurrence in request.GetProperty("occurrences").EnumerateArray()) source[$"{doc}:{occurrence.GetProperty("ref").GetString()}"] = (doc, occurrence.GetProperty("sourceOccurrenceId").GetString()!);
        }
        var result = new List<Assignment>();
        foreach (var cluster in clusters.GetProperty("clusters").EnumerateArray())
        {
            var doc = cluster.GetProperty("documentId").GetString()!; var clusterId = cluster.GetProperty("clusterId").GetString()!;
            foreach (var member in cluster.GetProperty("members").EnumerateArray())
            {
                var key = $"{doc}:{member.GetString()}"; Require(source.TryGetValue(key, out var occurrence), $"V7D_UNKNOWN_CLUSTER_MEMBER:{key}");
                result.Add(new Assignment(doc, member.GetString()!, occurrence.SourceOccurrenceId, clusterId));
            }
        }
        Require(result.Count == 226 && result.Select(x => $"{x.DocumentId}:{x.Ref}").Distinct(StringComparer.Ordinal).Count() == 226, "V7D_ASSIGNMENT_COVERAGE");
        return result;
    }

    private static object BuildSummary(IReadOnlyList<Row> rows, string root, string goldPath, string v7c)
    {
        var correct = rows.Count(x => x.GoldRelation == x.PredictedRelation);
        var falseMerge = rows.Count(x => x.GoldRelation == "DISTINCT" && x.PredictedSameCluster);
        var falseSplit = rows.Count(x => (x.GoldRelation is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF") && !x.PredictedSameCluster);
        var wrongWithin = rows.Count(x => x.GoldRelation == "CONTINUATION_OF" && x.PredictedRelation == "SAME_SEMANTIC_REPEAT");
        var nodeCorrect = rows.Count(x => x.GoldRelation == "DISTINCT" ? !x.PredictedSameCluster : x.PredictedSameCluster);
        var perRelation = Labels.Select(label =>
        {
            var tp = rows.Count(x => x.GoldRelation == label && x.PredictedRelation == label);
            var fp = rows.Count(x => x.GoldRelation != label && x.PredictedRelation == label);
            var fn = rows.Count(x => x.GoldRelation == label && x.PredictedRelation != label);
            var p = Divide(tp, tp + fp); var r = Divide(tp, tp + fn);
            return new { relation = label, tp, fp, fn, precision = p, recall = r, f1 = Divide(2 * p * r, p + r) };
        }).ToArray();
        return new
        {
            schemaVersion = "a99-v7d-offline-dev-regression-summary-v1",
            lane = "DEV_EXPOSED_V7D_REGRESSION",
            gold = new { count = rows.Count, artifact = Relative(root, goldPath), sha256 = Sha256File(goldPath), authority = "USER_FINAL_SOURCE_BACKED_ADJUDICATION", independentHumanGold = false },
            projection = new { sameCluster = "SAME_SEMANTIC_REPEAT", differentCluster = "DISTINCT", continuationPredictionCount = 0, continuationInference = "FORBIDDEN" },
            correct, total = rows.Count, effectiveCorrectness = Divide(correct, rows.Count),
            nodeConstraintCorrect = nodeCorrect, nodeConstraintAccuracy = Divide(nodeCorrect, rows.Count),
            falseMerge, falseSplit, wrongRelationWithinCluster = wrongWithin,
            continuationPredictions = 0,
            perRelation,
            v7cArtifact = Relative(root, v7c),
            providerCalls = 0, modelCalls = 0,
        };
    }

    private static object BuildConfusion(IReadOnlyList<Row> rows)
    {
        var columns = Labels;
        var matrix = Labels.ToDictionary(g => g, g => columns.ToDictionary(p => p, p => rows.Count(x => x.GoldRelation == g && x.PredictedRelation == p), StringComparer.Ordinal), StringComparer.Ordinal);
        return new { schemaVersion = "a99-v7d-confusion-matrix-v1", rows = matrix };
    }

    private static object[] BuildPerDocument(IReadOnlyList<Row> rows) => rows.GroupBy(x => x.DocumentId, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g =>
    {
        var a = g.ToArray(); var correct = a.Count(x => x.GoldRelation == x.PredictedRelation); var nodeCorrect = a.Count(x => x.GoldRelation == "DISTINCT" ? !x.PredictedSameCluster : x.PredictedSameCluster);
        return new { documentId = g.Key, total = a.Length, correct, effectiveCorrectness = Divide(correct, a.Length), nodeConstraintCorrect = nodeCorrect, nodeConstraintAccuracy = Divide(nodeCorrect, a.Length), falseMerge = a.Count(x => x.GoldRelation == "DISTINCT" && x.PredictedSameCluster), falseSplit = a.Count(x => x.GoldRelation is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF" && !x.PredictedSameCluster), continuationPredictions = 0 };
    }).ToArray();

    private static object BuildPositiveEdgeUtility(string root, IReadOnlyList<Row> rows)
    {
        using var requests = Read(Path.Combine(root, "artifacts/identity-benchmark/v7/identity-verification/preflight-v1-independent-positive-edge/requests.json"));
        using var parsed1 = Read(Path.Combine(root, "artifacts/identity-benchmark/v7/identity-verification/execution-v1-independent-positive-edge/parsed/001.json"));
        using var parsed2 = Read(Path.Combine(root, "artifacts/identity-benchmark/v7/identity-verification/execution-v1-independent-positive-edge/parsed/002.json"));
        var parsed = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["DOC-0123"] = parsed1.RootElement, ["DOC-0133"] = parsed2.RootElement };
        var goldByPair = rows.ToDictionary(x => PairKey(x.DocumentId, x.Left, x.Right), x => x.GoldRelation, StringComparer.Ordinal);
        var counters = new Dictionary<string, int>(StringComparer.Ordinal) { ["GOLD_SAME"] = 0, ["GOLD_CONTINUATION"] = 0, ["GOLD_DISTINCT"] = 0, ["NOT_IN_128_GOLD"] = 0 };
        foreach (var record in requests.RootElement.GetProperty("requests").EnumerateArray())
        {
            var req = record.GetProperty("request"); var doc = req.GetProperty("documentId"); if (!parsed.TryGetValue(doc.GetString()!, out var response)) continue;
            foreach (var decision in response.GetProperty("response").GetProperty("decisions").EnumerateArray().Where(x => x.GetProperty("decision").GetString() == "ACCEPT" && x.GetProperty("relation").GetString() == "SAME_SEMANTIC_REPEAT"))
            {
                var proposal = req.GetProperty("proposedEdges").EnumerateArray().Single(x => x.GetProperty("proposalId").GetString() == decision.GetProperty("proposalId").GetString());
                var left = req.GetProperty("occurrences").EnumerateArray().Single(x => x.GetProperty("ref").GetString() == proposal.GetProperty("left").GetString()).GetProperty("sourceOccurrenceId").GetString()!;
                var right = req.GetProperty("occurrences").EnumerateArray().Single(x => x.GetProperty("ref").GetString() == proposal.GetProperty("right").GetString()).GetProperty("sourceOccurrenceId").GetString()!;
                var key = PairKey(doc.GetString()!, left, right); if (goldByPair.TryGetValue(key, out var label)) counters[label == "SAME_SEMANTIC_REPEAT" ? "GOLD_SAME" : label == "CONTINUATION_OF" ? "GOLD_CONTINUATION" : "GOLD_DISTINCT"]++; else counters["NOT_IN_128_GOLD"]++;
            }
        }
        return new { schemaVersion = "a99-v7d-positive-edge-utility-v1", acceptedSameEdges = 41, goldCoverage = counters.Values.Sum() - counters["NOT_IN_128_GOLD"], counts = counters, note = "Diagnostic after projection freeze; not used to construct clusters." };
    }

    private static string NormalizeGold(string relation) => relation == "DISTINCT_SEMANTIC_NODE" ? "DISTINCT" : relation;
    private static string PairKey(string doc, string left, string right) => doc + ":" + (string.CompareOrdinal(left, right) < 0 ? left + "|" + right : right + "|" + left);
    private static double Divide(double a, double b) => b == 0 ? 0 : a / b;
    private static void ValidateV7C(JsonElement manifest, JsonElement summary)
    {
        Require(manifest.GetProperty("status").GetString() == "V7C_PREDICTION_GRAPH_FROZEN_BEFORE_GOLD_OR_EVALUATION" && manifest.GetProperty("input").GetProperty("v7bRequestSetSha256").GetString() == V7BRequestSetSha256 && manifest.GetProperty("providerCalls").GetInt32() == 0 && manifest.GetProperty("goldReadCount").GetInt32() == 0, "V7D_V7C_MANIFEST_FIREWALL");
        Require(summary.GetProperty("status").GetString() == "V7C_PREDICTION_GRAPH_FROZEN_BEFORE_GOLD_OR_EVALUATION" && summary.GetProperty("acceptedSameEdges").GetInt32() == 41 && summary.GetProperty("acceptedEdgesRejectedByGlobalConsistency").GetInt32() == 0 && summary.GetProperty("keepSplitViolations").GetInt32() == 0 && summary.GetProperty("transitivityViolations").GetInt32() == 0, "V7D_V7C_SUMMARY_INVARIANTS");
    }

    private static string BuildReport(object summary) => "# A99 V7D — conservative clustering dev regression\n\n" + "The cluster-to-pair projection was frozen before opening Gold: same cluster means `SAME_SEMANTIC_REPEAT`; different clusters mean `DISTINCT_SEMANTIC_NODE`. No continuation is inferred from text, role, or raw V7A proposals, so continuation prediction count is zero.\n\n" + "This is a DOC-0252/DOC-0133/DOC-0123 development-exposed regression only; it makes no generalization claim.\n\n```json\n" + JsonSerializer.Serialize(summary, JsonOptions) + "\n```\n";

    private sealed record Assignment(string DocumentId, string Ref, string SourceOccurrenceId, string ClusterId);
    private sealed record Row(string ReviewId, string CandidateId, string DocumentId, string Left, string Right, string GoldRelation, string PredictedRelation, bool PredictedSameCluster);
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
