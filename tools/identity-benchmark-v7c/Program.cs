using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV7C;

internal static class Program
{
    private const string V7BPreflightRelative = "artifacts/identity-benchmark/v7/identity-verification/preflight-v1-independent-positive-edge";
    private const string V7BExecutionRelative = "artifacts/identity-benchmark/v7/identity-verification/execution-v1-independent-positive-edge";
    private const string OutputRelative = "artifacts/identity-benchmark/v7/conservative-clustering/preflight-v1-clique-equivalence";
    private const string V7BRequestSetSha256 = "5af2dd92a27ff109a4bf929685f336fe9496d6c12602adfd1d3c6fe62e70cf45";
    private const int ExpectedOccurrences = 226;
    private const int ExpectedAcceptedSame = 41;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V7C_STATUS=CONSERVATIVE_CLIQUE_CLUSTERING_FROZEN PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V7C_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var preflight = Full(root, V7BPreflightRelative);
        var execution = Full(root, V7BExecutionRelative);
        var output = Full(root, OutputRelative);
        Require(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(), "V7C_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");
        using var preflightDoc = Read(Path.Combine(preflight, "manifest.json"));
        using var v7bFreeze = Read(Path.Combine(execution, "prediction-freeze.json"));
        using var v7bSummary = Read(Path.Combine(execution, "prediction-summary.json"));
        ValidateV7B(preflightDoc.RootElement, v7bFreeze.RootElement, v7bSummary.RootElement);
        using var requestDoc = Read(Path.Combine(preflight, "requests.json"));
        var requests = requestDoc.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("request").GetProperty("documentId").GetString()!, x => x.GetProperty("request"), StringComparer.Ordinal);
        var statuses = LoadStatuses(execution);
        var positiveEdges = new List<PositiveEdge>();
        var invalidDocuments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            if (status.Status != "VALID") { invalidDocuments.Add(status.DocumentId); continue; }
            var request = requests[status.DocumentId];
            using var parsed = Read(Path.Combine(execution, "parsed", $"{status.Sequence:D3}.json"));
            var response = parsed.RootElement.GetProperty("response");
            foreach (var decision in response.GetProperty("decisions").EnumerateArray())
            {
                if (decision.GetProperty("decision").GetString() != "ACCEPT") continue;
                if (decision.GetProperty("relation").GetString() != "SAME_SEMANTIC_REPEAT") continue;
                var id = decision.GetProperty("proposalId").GetString()!;
                var proposal = request.GetProperty("proposedEdges").EnumerateArray().Single(x => x.GetProperty("proposalId").GetString() == id);
                positiveEdges.Add(new PositiveEdge(status.DocumentId, id, proposal.GetProperty("left").GetString()!, proposal.GetProperty("right").GetString()!, "ACCEPT_SAME"));
            }
        }
        Require(positiveEdges.Count == ExpectedAcceptedSame, "V7C_ACCEPTED_SAME_COUNT");
        var clusters = new List<ClusterRecord>();
        var matrix = new List<PairAuthority>();
        var componentDiagnostics = new List<ComponentDiagnostic>();
        foreach (var request in requests.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var documentId = request.Key;
            var occurrences = request.Value.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("ref").GetString()!).ToArray();
            var edgeSet = positiveEdges.Where(x => x.DocumentId == documentId).ToDictionary(x => PairKey(x.Left, x.Right), x => x, StringComparer.Ordinal);
            foreach (var left in occurrences)
            foreach (var right in occurrences.Where(x => string.CompareOrdinal(left, x) < 0))
            {
                var key = PairKey(left, right);
                matrix.Add(new PairAuthority(documentId, left, right, edgeSet.ContainsKey(key) ? "POSITIVE_CANDIDATE" : "MUST_KEEP_SPLIT", edgeSet.TryGetValue(key, out var edge) ? edge.ProposalId : null, invalidDocuments.Contains(documentId), invalidDocuments.Contains(documentId) ? "INVALID_VERIFIER_DOCUMENT" : edgeSet.ContainsKey(key) ? "V7B_ACCEPT_SAME" : "NO_CLAIM_OR_NON_ACCEPTED"));
            }
            var partitions = BuildPartition(occurrences, edgeSet.Keys.ToHashSet(StringComparer.Ordinal));
            var componentCount = CountComponents(occurrences, edgeSet.Keys.ToHashSet(StringComparer.Ordinal));
            componentDiagnostics.Add(new ComponentDiagnostic(documentId, occurrences.Length, edgeSet.Count, invalidDocuments.Contains(documentId), componentCount, partitions.Count, partitions.Count(x => x.Members.Count > 1), partitions.Count == 0 ? 0 : partitions.Max(x => x.Members.Count)));
            clusters.AddRange(partitions.Select((members, index) => new ClusterRecord(documentId, $"V7C:{documentId}:{Sha256Text(string.Join("|", members.Members))[..16]}", members.Members, members.Members.Count > 1, edgeSet.Keys.Count(key => members.Members.Contains(SplitKey(key).Left) && members.Members.Contains(SplitKey(key).Right)))));
        }
        var matrixViolations = matrix.Count(x => x.Authority == "MUST_KEEP_SPLIT" && SameCluster(clusters, x.DocumentId, x.Left, x.Right));
        var acceptedSatisfied = positiveEdges.Count(x => SameCluster(clusters, x.DocumentId, x.Left, x.Right));
        var acceptedRejected = positiveEdges.Count - acceptedSatisfied;
        Require(matrixViolations == 0, "V7C_KEEP_SPLIT_VIOLATION");
        Require(acceptedRejected == 0, "V7C_ACCEPTED_EDGE_LOST");
        var transitivityViolations = matrixViolations;
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v7c-conservative-clique-equivalence-preflight-v1",
            status = "V7C_PREDICTION_GRAPH_FROZEN_BEFORE_GOLD_OR_EVALUATION",
            input = new { v7bRequestSetSha256 = V7BRequestSetSha256, acceptedSameEdges = ExpectedAcceptedSame, acceptedContinuationEdgesUsed = 0, invalidVerifierDocuments = invalidDocuments.OrderBy(x => x, StringComparer.Ordinal).ToArray() },
            occurrenceCount = ExpectedOccurrences,
            providerCalls = 0,
            goldReadCount = 0,
            historicalPredictionReadCount = 0,
            evaluationReadCount = 0,
            connectedComponentsAuthority = false,
            clusteringAlgorithm = "EXACT_CLIQUE_PARTITION_PER_POSITIVE_COMPONENT_V1",
            defaultPolicy = "ABSENCE_OF_POSITIVE_AUTHORITY_MEANS_KEEP_SPLIT",
            generalizationClaim = false,
        });
        await WriteAsync(Path.Combine(output, "authority-matrix.json"), new
        {
            schemaVersion = "a99-v7c-pair-authority-matrix-v1",
            status = "FROZEN",
            noClaimAndNonAcceptedPairs = "MUST_KEEP_SPLIT",
            invalidVerifierDocumentPolicy = "ALL_PAIRS_MUST_KEEP_SPLIT",
            crossDocumentPolicy = "MUST_KEEP_SPLIT",
            pairs = matrix,
        });
        await WriteAsync(Path.Combine(output, "clusters.json"), new
        {
            schemaVersion = "a99-v7c-conservative-clusters-v1",
            status = "FROZEN_BEFORE_GOLD_OR_EVALUATION",
            clusters = clusters.OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.ClusterId, StringComparer.Ordinal).ToArray(),
        });
        await WriteAsync(Path.Combine(output, "summary.json"), new
        {
            schemaVersion = "a99-v7c-conservative-clustering-summary-v1",
            status = "V7C_PREDICTION_GRAPH_FROZEN_BEFORE_GOLD_OR_EVALUATION",
            acceptedSameEdges = positiveEdges.Count,
            acceptedContinuationEdgesUsed = 0,
            invalidVerifierDocuments = invalidDocuments.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            totalClusters = clusters.Count,
            multiMemberClusters = clusters.Count(x => x.MultiMember),
            singletonClusters = clusters.Count(x => !x.MultiMember),
            largestCluster = clusters.Count == 0 ? 0 : clusters.Max(x => x.Members.Count),
            acceptedEdgesSatisfied = acceptedSatisfied,
            acceptedEdgesRejectedByGlobalConsistency = acceptedRejected,
            keepSplitViolations = matrixViolations,
            transitivityViolations,
            providerCalls = 0,
            goldReadCount = 0,
            historicalPredictionReadCount = 0,
            evaluationReadCount = 0,
            connectedComponentsAuthority = false,
            componentDiagnostics,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# A99 V7C — conservative equivalence clustering preflight\n\nV7B accepted SAME edges: **{positiveEdges.Count}**. V7B invalid verifier documents: **{invalidDocuments.Count}**. Clustering uses complete-link positive authority only; every absent/non-accepted pair is `MUST_KEEP_SPLIT`. Connected components are diagnostic only.\n\nClusters: **{clusters.Count}**; multi-member clusters: **{clusters.Count(x => x.MultiMember)}**; singleton clusters: **{clusters.Count(x => !x.MultiMember)}**; largest cluster: **{clusters.Max(x => x.Members.Count)}**. Accepted edges satisfied: **{acceptedSatisfied}/{positiveEdges.Count}**. KEEP_SPLIT violations: **{matrixViolations}**. Provider calls: **0**. Gold reads: **0**.\n");
    }

    private static IReadOnlyList<ClusterMembers> BuildPartition(string[] vertices, HashSet<string> edges)
    {
        var components = ConnectedComponents(vertices, edges);
        var result = new List<ClusterMembers>();
        foreach (var component in components)
        {
            Require(component.Count <= 20, "V7C_COMPONENT_TOO_LARGE_FOR_EXACT_PARTITION");
            var ordered = component.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var best = SolvePartition(ordered, edges);
            result.AddRange(best);
        }
        return result;
    }

    private static IReadOnlyList<ClusterMembers> SolvePartition(string[] vertices, HashSet<string> edges)
    {
        var memo = new Dictionary<string, PartitionScore>(StringComparer.Ordinal);
        PartitionScore Solve(HashSet<string> remaining)
        {
            if (remaining.Count == 0) return new PartitionScore(0, 0, []);
            var key = string.Join("|", remaining.OrderBy(x => x, StringComparer.Ordinal));
            if (memo.TryGetValue(key, out var cached)) return cached;
            var pivot = remaining.OrderBy(x => x, StringComparer.Ordinal).First();
            var others = remaining.Where(x => x != pivot).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var candidates = new List<string[]> { new[] { pivot } };
            for (var mask = 1; mask < (1 << others.Length); mask++)
            {
                var clique = new List<string> { pivot };
                for (var i = 0; i < others.Length; i++) if ((mask & (1 << i)) != 0) clique.Add(others[i]);
                if (IsClique(clique, edges)) candidates.Add(clique.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            }
            PartitionScore? best = null;
            foreach (var clique in candidates.OrderByDescending(x => x.Length).ThenBy(x => string.Join("|", x), StringComparer.Ordinal))
            {
                var rest = new HashSet<string>(remaining, StringComparer.Ordinal);
                foreach (var member in clique) rest.Remove(member);
                var suffix = Solve(rest);
                var score = suffix with { EdgeCount = suffix.EdgeCount + clique.Length * (clique.Length - 1) / 2, ClusterCount = suffix.ClusterCount + 1, Clusters = [new ClusterMembers(clique), .. suffix.Clusters] };
                if (best is null || Better(score, best)) best = score;
            }
            memo[key] = best!;
            return best!;
        }
        return Solve(vertices.ToHashSet(StringComparer.Ordinal)).Clusters;
    }

    private static bool Better(PartitionScore candidate, PartitionScore current) => candidate.EdgeCount > current.EdgeCount || candidate.EdgeCount == current.EdgeCount && (candidate.ClusterCount < current.ClusterCount || candidate.ClusterCount == current.ClusterCount && string.CompareOrdinal(SerializeClusters(candidate.Clusters), SerializeClusters(current.Clusters)) < 0);
    private static string SerializeClusters(IReadOnlyList<ClusterMembers> clusters) => string.Join(";", clusters.Select(x => string.Join(",", x.Members.OrderBy(y => y, StringComparer.Ordinal))).OrderBy(x => x, StringComparer.Ordinal));
    private static bool IsClique(IReadOnlyList<string> members, HashSet<string> edges) { for (var i = 0; i < members.Count; i++) for (var j = i + 1; j < members.Count; j++) if (!edges.Contains(PairKey(members[i], members[j]))) return false; return true; }

    private static List<List<string>> ConnectedComponents(string[] vertices, HashSet<string> edges)
    {
        var result = new List<List<string>>(); var unseen = vertices.ToHashSet(StringComparer.Ordinal);
        while (unseen.Count > 0)
        {
            var root = unseen.OrderBy(x => x, StringComparer.Ordinal).First(); unseen.Remove(root); var queue = new Queue<string>([root]); var component = new List<string>();
            while (queue.Count > 0) { var current = queue.Dequeue(); component.Add(current); foreach (var next in vertices.Where(x => unseen.Contains(x) && edges.Contains(PairKey(current, x))).OrderBy(x => x, StringComparer.Ordinal).ToArray()) { unseen.Remove(next); queue.Enqueue(next); } }
            result.Add(component);
        }
        return result;
    }

    private static int CountComponents(string[] vertices, HashSet<string> edges) => ConnectedComponents(vertices, edges).Count;
    private static bool SameCluster(IReadOnlyList<ClusterRecord> clusters, string documentId, string left, string right) => clusters.Any(x => x.DocumentId == documentId && x.Members.Contains(left) && x.Members.Contains(right));
    private static string PairKey(string left, string right) => string.CompareOrdinal(left, right) < 0 ? left + "|" + right : right + "|" + left;
    private static (string Left, string Right) SplitKey(string key) { var parts = key.Split('|', 2); return (parts[0], parts[1]); }

    private static IReadOnlyList<AttemptStatus> LoadStatuses(string execution)
    {
        var path = Path.Combine(execution, "attempt-manifest.json"); using var manifest = Read(path); var m = manifest.RootElement;
        Require(m.GetProperty("actualAttemptCount").GetInt32() == 3 && m.GetProperty("actualProviderCalls").GetInt32() == 3 && m.GetProperty("retryCount").GetInt32() == 0 && m.GetProperty("goldReadCount").GetInt32() == 0 && m.GetProperty("historicalPredictionReadCount").GetInt32() == 0 && m.GetProperty("evaluationReadCount").GetInt32() == 0 && !m.GetProperty("autoCollapse").GetBoolean(), "V7C_V7B_FIREWALL");
        return m.GetProperty("attempts").EnumerateArray().Select(x => new AttemptStatus(x.GetProperty("sequence").GetInt32(), x.GetProperty("documentId").GetString()!, x.GetProperty("status").GetString()!)).OrderBy(x => x.Sequence).ToArray();
    }

    private static void ValidateV7B(JsonElement preflight, JsonElement freeze, JsonElement summary)
    {
        Require(preflight.GetProperty("status").GetString() == "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION" && preflight.GetProperty("requestSetSha256").GetString() == V7BRequestSetSha256 && preflight.GetProperty("goldReadCount").GetInt32() == 0 && preflight.GetProperty("providerCalls").GetInt32() == 0, "V7C_PREFLIGHT_FIREWALL");
        Require(freeze.GetProperty("status").GetString() == "V7B_VERIFIER_OUTPUTS_FROZEN_BEFORE_CLUSTERING_OR_EVALUATION" && freeze.GetProperty("requestSetSha256").GetString() == V7BRequestSetSha256 && freeze.GetProperty("providerCalls").GetInt32() == 3 && freeze.GetProperty("goldReadCount").GetInt32() == 0 && freeze.GetProperty("historicalPredictionReadCount").GetInt32() == 0 && freeze.GetProperty("evaluationReadCount").GetInt32() == 0 && !freeze.GetProperty("autoCollapse").GetBoolean(), "V7C_FREEZE_FIREWALL");
        Require(summary.GetProperty("valid").GetInt32() == 2 && summary.GetProperty("invalidValidation").GetInt32() == 1 && summary.GetProperty("providerErrors").GetInt32() == 0 && summary.GetProperty("acceptedSameSemanticRepeat").GetInt32() == ExpectedAcceptedSame && summary.GetProperty("acceptedContinuation").GetInt32() == 0, "V7C_SUMMARY_FIREWALL");
    }

    private sealed record PositiveEdge(string DocumentId, string ProposalId, string Left, string Right, string Authority);
    private sealed record AttemptStatus(int Sequence, string DocumentId, string Status);
    private sealed record PairAuthority(string DocumentId, string Left, string Right, string Authority, string? ProposalId, bool InvalidVerifierDocument, string Reason);
    private sealed record ClusterMembers(IReadOnlyList<string> Members);
    private sealed record ClusterRecord(string DocumentId, string ClusterId, IReadOnlyList<string> Members, bool MultiMember, int AcceptedEdgesSatisfied);
    private sealed record ComponentDiagnostic(string DocumentId, int Occurrences, int AcceptedSameEdges, bool InvalidVerifierDocument, int PositiveComponents, int TotalClusters, int MultiMemberClusters, int LargestCluster);
    private sealed record PartitionScore(int EdgeCount, int ClusterCount, IReadOnlyList<ClusterMembers> Clusters);

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
