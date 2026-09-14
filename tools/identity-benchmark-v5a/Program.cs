using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV5A;

internal static class Program
{
    private const int ExpectedSampleCount = 128;
    private const string SampleRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment/sample.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v5/source-only-clusters/freeze";
    private const string ClusterSeed = "a99-v5a-source-only-ambiguity-components-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V5A_STATUS=SOURCE_ONLY_CLUSTER_FREEZE_COMPLETE CLUSTERS_FROZEN=true MODEL_CALLS=0 PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V5A_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var samplePath = Full(root, SampleRelative);
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);
        using var sample = JsonDocument.Parse(await File.ReadAllTextAsync(samplePath));
        var candidates = ReadCandidates(sample.RootElement);
        ValidateInput(sample.RootElement, candidates);
        var graph = BuildGraph(candidates);
        var clusters = graph.BuildClusters();
        var sampleSha = Sha256File(samplePath);

        var manifest = new
        {
            schemaVersion = "a99-v5a-source-only-cluster-freeze-v1",
            phase = "V5A_SOURCE_ONLY_SEMANTIC_CLUSTER_FREEZE",
            status = "FROZEN_SOURCE_ONLY_AMBIGUITY_CLUSTERS",
            input = Relative(root, samplePath),
            inputSha256 = sampleSha,
            inputArtifactKind = "a99_identity_benchmark_v4f_d_sample",
            selectionSeed = sample.RootElement.GetProperty("selectionSeed").GetString(),
            clusterSeed = ClusterSeed,
            candidateCount = candidates.Count,
            occurrenceCount = graph.Nodes.Count,
            clusterCount = clusters.Count,
            nonSingletonClusterCount = clusters.Count(x => x.OccurrenceIds.Count > 1),
            edgeCount = graph.Edges.Count,
            highRecallDiscovery = true,
            modelCalls = 0,
            providerCalls = 0,
            goldReadCount = 0,
            knownPairLabelsRead = false,
            knownPairLabelsUsed = false,
            semanticMergePerformed = false,
            semanticNodeIdsAssigned = false,
            pairLabelsDerived = false,
            note = "Clusters are source-only ambiguity components. They authorize joint inspection only; they are not semantic identity groups or merge decisions.",
        };

        await WriteAsync(Path.Combine(output, "manifest.json"), manifest);
        await WriteAsync(Path.Combine(output, "clusters.json"), new
        {
            schemaVersion = "a99-v5a-source-only-clusters-v1",
            sourceOnly = true,
            candidateCount = candidates.Count,
            clusters,
        });
        await WriteAsync(Path.Combine(output, "edges.json"), new
        {
            schemaVersion = "a99-v5a-source-only-ambiguity-edges-v1",
            sourceOnly = true,
            edges = graph.Edges,
        });
        await WriteAsync(Path.Combine(output, "firewall.json"), new
        {
            schemaVersion = "a99-v5a-firewall-v1",
            modelCalls = 0,
            providerCalls = 0,
            goldReadCount = 0,
            knownPairLabelsRead = false,
            v4hGoldRead = false,
            predictionRead = false,
            mergeDecision = false,
            semanticNodeAssignment = false,
            derivedPairLabel = false,
            sourceOnlyEvidence = true,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(manifest, clusters, graph.Edges), new UTF8Encoding(false));
    }

    private static IReadOnlyList<Candidate> ReadCandidates(JsonElement root) => root.GetProperty("candidates").EnumerateArray().Select(x => new Candidate(
        x.GetProperty("pairId").GetString()!,
        x.GetProperty("documentId").GetString()!,
        x.GetProperty("left").GetString()!,
        x.GetProperty("right").GetString()!,
        x.GetProperty("packetClass").GetString()!,
        x.GetProperty("evidenceConfiguration").GetString()!,
        x.GetProperty("sourceOrderDistanceBucket").GetString()!,
        x.GetProperty("endpointDegreeBucket").GetString()!,
        x.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).OrderBy(r => r, StringComparer.Ordinal).ToArray())).ToArray();

    private static void ValidateInput(JsonElement root, IReadOnlyList<Candidate> candidates)
    {
        Require(root.GetProperty("artifactKind").GetString() == "a99_identity_benchmark_v4f_d_sample", "V5A_INPUT_ARTIFACT_DRIFT");
        Require(root.GetProperty("sampleCount").GetInt32() == ExpectedSampleCount && candidates.Count == ExpectedSampleCount, "V5A_SAMPLE_COUNT");
        Require(candidates.Select(x => x.PairId).Distinct(StringComparer.Ordinal).Count() == ExpectedSampleCount, "V5A_DUPLICATE_PAIR");
        Require(candidates.All(x => x.Reasons.Length > 0), "V5A_MISSING_SOURCE_REASON");
        Require(candidates.All(x => x.Left != x.Right), "V5A_SELF_EDGE");
        Require(candidates.All(x => x.Left.StartsWith(x.DocumentId + ":", StringComparison.Ordinal) && x.Right.StartsWith(x.DocumentId + ":", StringComparison.Ordinal)), "V5A_CROSS_DOCUMENT_EDGE");
    }

    private static SourceGraph BuildGraph(IReadOnlyList<Candidate> candidates)
    {
        var nodes = candidates.SelectMany(x => new[] { x.Left, x.Right }).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var edges = candidates.Select(x => new SourceEdge(x.PairId, x.DocumentId, x.Left, x.Right, x.Reasons, x.EvidenceConfiguration, x.SourceOrderDistanceBucket, x.PacketClass, x.EndpointDegreeBucket)).ToArray();
        var parent = nodes.ToDictionary(x => x, x => x, StringComparer.Ordinal);
        string Find(string value)
        {
            var current = value;
            while (!StringComparer.Ordinal.Equals(parent[current], current))
            {
                parent[current] = parent[parent[current]];
                current = parent[current];
            }
            return current;
        }
        foreach (var edge in edges)
        {
            var left = Find(edge.Left);
            var right = Find(edge.Right);
            if (!StringComparer.Ordinal.Equals(left, right)) parent[right] = left;
        }
        return new SourceGraph(nodes, edges, parent, Find);
    }

    private static string BuildReport(object manifest, IReadOnlyList<Cluster> clusters, IReadOnlyList<SourceEdge> edges) =>
        "# A99 V5A — source-only semantic ambiguity cluster freeze\n\n" +
        "Status: **FROZEN_SOURCE_ONLY_AMBIGUITY_CLUSTERS**\n\n" +
        "V5A constructs high-recall ambiguity components from the frozen V4F-D source-only candidate sample. It does not assign semantic nodes, classify relations, merge occurrences, or read V4H Gold.\n\n" +
        "## Firewall\n\n" +
        "- Model calls: **0**.\n- Provider calls: **0**.\n- Gold reads: **0**.\n- Known pair labels read/used: **false/false**.\n- Semantic merge: **false**.\n- Pair-label derivation: **false**.\n\n" +
        "## Freeze manifest\n\n```json\n" + JsonSerializer.Serialize(manifest, JsonOptions) + "\n```\n\n" +
        "## Cluster policy\n\n" +
        "Each connected component is an inspection cluster only. Edge reasons explain why source-owned evidence caused two occurrences to be co-inspected; they are not merge confidence and are not semantic labels. V5B may jointly reason over a cluster and document context, but must assign occurrence roles and semanticNodeId independently.\n\n" +
        "## Determinism\n\n" +
        $"Clusters: **{clusters.Count}**; source-only edges: **{edges.Count}**. Cluster IDs are SHA-256-derived from the sorted occurrence IDs and the fixed V5A seed.\n";

    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, string PacketClass, string EvidenceConfiguration, string SourceOrderDistanceBucket, string EndpointDegreeBucket, string[] Reasons);
    private sealed record SourceEdge(string CandidateId, string DocumentId, string Left, string Right, string[] Reasons, string EvidenceConfiguration, string SourceOrderDistanceBucket, string PacketClass, string EndpointDegreeBucket);
    private sealed record Cluster(string ClusterId, string DocumentId, IReadOnlyList<string> OccurrenceIds, int EdgeCount, IReadOnlyList<string> EvidenceReasons, IReadOnlyList<string> EvidenceConfigurations, IReadOnlyList<string> SourceOrderDistanceBuckets, IReadOnlyList<string> PacketClasses, IReadOnlyList<string> CandidateIds);

    private sealed class SourceGraph
    {
        public SourceGraph(IReadOnlySet<string> nodes, IReadOnlyList<SourceEdge> edges, IReadOnlyDictionary<string, string> parent, Func<string, string> find)
        {
            Nodes = nodes;
            Edges = edges;
            _parent = parent;
            _find = find;
        }

        public IReadOnlySet<string> Nodes { get; }
        public IReadOnlyList<SourceEdge> Edges { get; }
        private readonly IReadOnlyDictionary<string, string> _parent;
        private readonly Func<string, string> _find;

        public IReadOnlyList<Cluster> BuildClusters()
        {
            var grouped = Nodes.GroupBy(_find, StringComparer.Ordinal).OrderBy(g => g.Min(StringComparer.Ordinal), StringComparer.Ordinal).ToArray();
            return grouped.Select(group =>
            {
                var occurrences = group.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var edges = Edges.Where(x => occurrences.Contains(x.Left, StringComparer.Ordinal) && occurrences.Contains(x.Right, StringComparer.Ordinal)).ToArray();
                var documentId = occurrences[0].Split(':', 2)[0];
                var id = "SC-" + Sha256Text(ClusterSeed + "|" + string.Join("|", occurrences))[..16];
                return new Cluster(id, documentId, occurrences, edges.Length,
                    edges.SelectMany(x => x.Reasons).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    edges.Select(x => x.EvidenceConfiguration).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    edges.Select(x => x.SourceOrderDistanceBucket).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    edges.Select(x => x.PacketClass).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    edges.Select(x => x.CandidateId).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            }).ToArray();
        }
    }
}
