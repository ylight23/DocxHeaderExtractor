using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV6A;

internal static class Program
{
    private const string V5A = "artifacts/identity-benchmark/v5/source-only-clusters/freeze/clusters.json";
    private const string V5BRequests = "artifacts/identity-benchmark/v5/semantic-node-induction/preflight/requests.json";
    private const string V5BAttempts = "artifacts/identity-benchmark/v5/semantic-node-induction/execution/attempt-manifest.json";
    private const string V5CItems = "artifacts/identity-benchmark/v5/semantic-node-induction/evaluation-v2/per-item.json";
    private const string V5CSummary = "artifacts/identity-benchmark/v5/semantic-node-induction/evaluation-v2/summary.json";
    private const string Output = "artifacts/identity-benchmark/v6/diagnosis/v5-failure-diagnosis-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V6A_STATUS=OFFLINE_V5_FAILURE_DIAGNOSIS_COMPLETE MODEL_CALLS=0 PROVIDER_CALLS=0 PREDICTIONS_MUTATED=false");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V6A_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var output = Full(root, Output);
        Directory.CreateDirectory(output);
        using var clusters = Read(Full(root, V5A));
        using var requests = Read(Full(root, V5BRequests));
        using var attempts = Read(Full(root, V5BAttempts));
        using var items = Read(Full(root, V5CItems));
        using var summary = Read(Full(root, V5CSummary));
        Require(summary.RootElement.GetProperty("phase").GetString() == "V5C_GRAPH_TO_PAIR_EVALUATION_V2", "V6A_V5C_SOURCE_DRIFT");

        var clusterByCandidate = clusters.RootElement.GetProperty("clusters").EnumerateArray()
            .SelectMany(c => c.GetProperty("candidateIds").EnumerateArray().Select(id => new { Id = id.GetString()!, Cluster = c }))
            .ToDictionary(x => x.Id, x => x.Cluster, StringComparer.Ordinal);
        var requestByCluster = requests.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("clusterId").GetString()!, StringComparer.Ordinal);
        var attemptByCluster = attempts.RootElement.GetProperty("attempts").EnumerateArray().ToDictionary(x => x.GetProperty("clusterId").GetString()!, StringComparer.Ordinal);
        var falseMerges = items.RootElement.EnumerateArray().Where(x => x.GetProperty("failureOwnership").GetString() == "WRONG_NODE_MERGE").ToArray();
        Require(falseMerges.Length == 55, "V6A_FALSE_MERGE_COUNT_DRIFT");

        var cases = falseMerges.Select(item => Diagnose(item, clusterByCandidate[item.GetProperty("candidateId").GetString()!], requestByCluster, attemptByCluster)).ToArray();
        var report = new
        {
            schemaVersion = "a99-v6a-v5-failure-diagnosis-v1",
            status = "V6A_COMPLETE",
            source = "FROZEN_V5C_V2_FALSE_MERGES",
            modelCalls = 0,
            providerCalls = 0,
            predictionsMutated = false,
            noProductionRuleInferred = true,
            totalFalseMerges = cases.Length,
            byMergeMode = cases.GroupBy(x => x.MergeMode).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { mergeMode = x.Key, count = x.Count() }).ToArray(),
            byDocument = cases.GroupBy(x => x.DocumentId).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { documentId = x.Key, count = x.Count() }).ToArray(),
            byEvidence = cases.SelectMany(x => x.EvidenceReasons).GroupBy(x => x, StringComparer.Ordinal).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => new { reason = x.Key, count = x.Count() }).ToArray(),
            interpretation = "All cases are observed model grouping overreach against frozen V4H relation authority. This artifact does not infer structural owners or install a veto rule; owner induction remains a separate V6 experiment.",
        };
        await WriteAsync(Path.Combine(output, "manifest.json"), new { schemaVersion = "a99-v6a-manifest-v1", status = "V6A_COMPLETE", cases = cases.Length, modelCalls = 0, providerCalls = 0, predictionsMutated = false, sourceV5CSummary = Relative(root, Full(root, V5CSummary)) });
        await WriteAsync(Path.Combine(output, "summary.json"), report);
        await WriteAsync(Path.Combine(output, "cases.json"), cases);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(report, cases), new UTF8Encoding(false));
    }

    private static DiagnosticCase Diagnose(JsonElement item, JsonElement cluster, IReadOnlyDictionary<string, JsonElement> requests, IReadOnlyDictionary<string, JsonElement> attempts)
    {
        var candidateId = item.GetProperty("candidateId").GetString()!;
        var clusterId = cluster.GetProperty("clusterId").GetString()!;
        var request = requests[clusterId];
        var leftId = item.GetProperty("leftOccurrenceId").GetString()!;
        var rightId = item.GetProperty("rightOccurrenceId").GetString()!;
        var left = request.GetProperty("occurrences").EnumerateArray().Single(x => x.GetProperty("occurrenceId").GetString() == leftId);
        var right = request.GetProperty("occurrences").EnumerateArray().Single(x => x.GetProperty("occurrenceId").GetString() == rightId);
        var attempt = attempts[clusterId];
        var response = attempt.GetProperty("response").GetProperty("response");
        var predicted = item.GetProperty("predictedRelation").GetString()!;
        var assignments = response.GetProperty("occurrenceAssignments").EnumerateArray().Where(x => x.GetProperty("occurrenceId").GetString() == leftId || x.GetProperty("occurrenceId").GetString() == rightId).Select(x => new { occurrenceId = x.GetProperty("occurrenceId").GetString(), semanticNodeLocalId = x.GetProperty("semanticNodeLocalId").GetString(), occurrenceRole = x.GetProperty("occurrenceRole").GetString(), confidence = x.GetProperty("confidence").GetString() }).ToArray();
        var nodeIds = assignments.Select(x => x.semanticNodeLocalId!).Distinct(StringComparer.Ordinal).ToArray();
        return new DiagnosticCase(
            item.GetProperty("reviewId").GetString()!, candidateId, clusterId, candidateId.Split(':', 2)[0],
            item.GetProperty("goldRelation").GetString()!, predicted, "WRONG_NODE_MERGE",
            predicted == "CONTINUATION_OF" ? "MERGED_VIA_EXPLICIT_CONTINUATION_EDGE" : "MERGED_WITHOUT_EXPLICIT_CONTINUATION_EDGE",
            new[] { Occurrence(left), Occurrence(right) }, assignments, nodeIds,
            cluster.GetProperty("evidenceReasons").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            cluster.GetProperty("packetClasses").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            cluster.GetProperty("sourceOrderDistanceBuckets").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            false, "MODEL_SEMANTIC_GROUPING_OVERREACH",
            "The frozen artifacts prove a wrong node merge, but do not authorize inferring structural ownership from this case. V6 owner induction must acquire/induce owner evidence independently.");
    }

    private static object Occurrence(JsonElement x) => new { occurrenceId = x.GetProperty("occurrenceId").GetString(), text = x.GetProperty("text").GetString(), documentOrder = x.GetProperty("documentOrder").GetInt32() };
    private static string BuildReport(object summary, IReadOnlyList<DiagnosticCase> cases) => "# A99 V6A — V5 failure diagnosis\n\n" + "This is an offline forensic artifact over the frozen V5C v2 false-merge set. It does not mutate V5B/V5C predictions, infer structural-owner Gold, or install a production rule.\n\n```json\n" + JsonSerializer.Serialize(summary, JsonOptions) + "\n```\n\nThe cases retain cluster evidence, source occurrence text/order, and the model grouping mode. `sourceOwnerEvidenceAvailable=false` is intentional: V5B did not freeze an owner annotation, so V6A does not retrofit one from Gold.\n";
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private sealed record DiagnosticCase(
        string ReviewId, string CandidateId, string ClusterId, string DocumentId, string GoldRelation, string PredictedRelation,
        string FailureOwnership, string MergeMode, object[] OccurrencePair, object[] PredictedAssignments, string[] PredictedNodeIds,
        string[] EvidenceReasons, string[] PacketClasses, string[] SourceOrderDistanceBuckets,
        bool SourceOwnerEvidenceAvailable, string DiagnosticCategory, string Note);
}
