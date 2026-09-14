using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace IdentityBenchmarkV5B;

internal static class Program
{
    private const string ClusterRelative = "artifacts/identity-benchmark/v5/source-only-clusters/freeze/clusters.json";
    private const string ClusterManifestRelative = "artifacts/identity-benchmark/v5/source-only-clusters/freeze/manifest.json";
    private const string SourceCatalogRelative = "artifacts/identity-benchmark/v2/source-catalog.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v5/semantic-node-induction/preflight";
    private const int ExpectedClusters = 102;
    private const string LocalContextRadius = "2_source_occurrences_each_side";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V5B_STATUS=SEMANTIC_NODE_INDUCTION_PREFLIGHT_COMPLETE REQUESTS_FROZEN=true MODEL_CALLS=0 PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V5B_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var clusterPath = Full(root, ClusterRelative);
        var clusterManifestPath = Full(root, ClusterManifestRelative);
        var sourceCatalogPath = Full(root, SourceCatalogRelative);
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);

        using var clusterDoc = JsonDocument.Parse(await File.ReadAllTextAsync(clusterPath));
        using var clusterManifest = JsonDocument.Parse(await File.ReadAllTextAsync(clusterManifestPath));
        using var sourceCatalogDoc = JsonDocument.Parse(await File.ReadAllTextAsync(sourceCatalogPath));
        ValidateClusterFreeze(clusterDoc.RootElement, clusterManifest.RootElement);
        var sourceOccurrences = ReadSourceOccurrences(sourceCatalogDoc.RootElement);
        var requests = BuildRequests(clusterDoc.RootElement, sourceOccurrences, Sha256File(sourceCatalogPath));
        if (requests.Count != ExpectedClusters) throw new InvalidDataException("V5B_CLUSTER_COUNT");

        var requestRecords = requests.Select(request => new
        {
            clusterId = request.ClusterId,
            documentId = request.DocumentId,
            requestHash = HdsaSemanticClusterInductionContract.RequestHash(request),
            occurrenceCount = request.Occurrences.Count,
            occurrenceIds = request.Occurrences.Select(x => x.OccurrenceId).ToArray(),
        }).ToArray();

        var manifest = new
        {
            schemaVersion = "a99-v5b-semantic-node-induction-preflight-v1",
            phase = "V5B_GLOBAL_CLUSTER_SEMANTIC_NODE_INDUCTION",
            status = "READY_FOR_PROVIDER_EXECUTION",
            clusterFreeze = Relative(root, clusterPath),
            clusterFreezeSha256 = Sha256File(clusterPath),
            clusterManifestSha256 = Sha256File(clusterManifestPath),
            sourceCatalog = Relative(root, sourceCatalogPath),
            sourceCatalogSha256 = Sha256File(sourceCatalogPath),
            requestSchemaVersion = HdsaSemanticClusterInductionContract.Version,
            clusterCount = requests.Count,
            occurrenceCount = requests.Sum(x => x.Occurrences.Count),
            requestCount = requests.Count,
            localContext = LocalContextRadius,
            modelCalls = 0,
            providerCalls = 0,
            goldReadCount = 0,
            goldDerivedInput = false,
            knownPairLabelsRead = false,
            knownPairLabelsIncluded = false,
            hierarchyRequested = false,
            rawResponsesPersisted = false,
            parsedResponsesPersisted = false,
            predictionsFrozen = false,
            note = "Offline V5B request preflight only. Requests contain source-owned cluster evidence and context; they contain no pair labels, Gold, hierarchy, or promotion decisions.",
        };

        await WriteAsync(Path.Combine(output, "manifest.json"), manifest);
        await WriteAsync(Path.Combine(output, "requests.json"), new
        {
            schemaVersion = "a99-v5b-semantic-node-induction-requests-v1",
            sourceOnly = true,
            requests,
        });
        await WriteAsync(Path.Combine(output, "request-index.json"), new
        {
            schemaVersion = "a99-v5b-request-index-v1",
            sourceOnly = true,
            requests = requestRecords,
        });
        await WriteAsync(Path.Combine(output, "firewall.json"), new
        {
            schemaVersion = "a99-v5b-firewall-v1",
            modelCalls = 0,
            providerCalls = 0,
            goldReadCount = 0,
            goldDerivedInput = false,
            knownPairLabelsRead = false,
            knownPairLabelsIncluded = false,
            hierarchyRequested = false,
            promotionDecision = false,
            pairLabel = false,
            semanticNodeAssignment = false,
            predictionFreeze = false,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(manifest, requestRecords), new UTF8Encoding(false));
    }

    private static IReadOnlyList<HdsaSemanticClusterInductionRequest> BuildRequests(JsonElement root, IReadOnlyList<SourceOccurrence> sourceOccurrences, string sourceCatalogSha256)
    {
        var byId = sourceOccurrences.ToDictionary(x => x.OccurrenceId, StringComparer.Ordinal);
        var byDocument = sourceOccurrences.GroupBy(x => x.OccurrenceId.Split(':', 2)[0], StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.DocumentOrder).ToArray(), StringComparer.Ordinal);
        var requests = new List<HdsaSemanticClusterInductionRequest>();
        foreach (var cluster in root.GetProperty("clusters").EnumerateArray())
        {
            var clusterId = Required(cluster, "clusterId");
            var documentId = Required(cluster, "documentId");
            var occurrenceIds = cluster.GetProperty("occurrenceIds").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (occurrenceIds.Length == 0) throw new InvalidDataException("V5B_EMPTY_CLUSTER:" + clusterId);
            if (!byDocument.TryGetValue(documentId, out var ordered)) throw new InvalidDataException("V5B_UNKNOWN_DOCUMENT:" + documentId);
            var occurrences = occurrenceIds.Select(id =>
            {
                if (!byId.TryGetValue(id, out var source)) throw new InvalidDataException("V5B_UNKNOWN_OCCURRENCE:" + id);
                if (!id.StartsWith(documentId + ":", StringComparison.Ordinal)) throw new InvalidDataException("V5B_CROSS_DOCUMENT_OCCURRENCE:" + id);
                var index = Array.FindIndex(ordered, x => string.Equals(x.OccurrenceId, id, StringComparison.Ordinal));
                return new HdsaSemanticClusterOccurrence(
                    source.OccurrenceId,
                    source.Text,
                    source.DocumentOrder,
                    ordered.Skip(Math.Max(0, index - 2)).Take(Math.Min(2, index)).Where(x => x.DocumentOrder < source.DocumentOrder).Select(ToContext).ToArray(),
                    ordered.Skip(index + 1).Take(2).Select(ToContext).ToArray());
            }).OrderBy(x => x.DocumentOrder).ThenBy(x => x.OccurrenceId, StringComparer.Ordinal).ToArray();

            var evidence = new HdsaSemanticClusterEvidence(
                Strings(cluster, "evidenceReasons"),
                Strings(cluster, "evidenceConfigurations"),
                Strings(cluster, "sourceOrderDistanceBuckets"),
                Strings(cluster, "packetClasses"),
                Strings(cluster, "candidateIds"));
            requests.Add(new HdsaSemanticClusterInductionRequest(
                HdsaSemanticClusterInductionContract.Version,
                clusterId,
                documentId,
                sourceCatalogSha256,
                $"V5:{documentId}:{clusterId}",
                occurrences,
                evidence));
        }
        return requests.OrderBy(x => x.ClusterId, StringComparer.Ordinal).ToArray();
    }

    private static HdsaSemanticClusterContextOccurrence ToContext(SourceOccurrence source) => new(source.OccurrenceId, source.Text, source.DocumentOrder);

    private static IReadOnlyList<SourceOccurrence> ReadSourceOccurrences(JsonElement root) => root.GetProperty("sourceOccurrences").EnumerateArray()
        .Select(x => new SourceOccurrence(Required(x, "nodeId"), x.GetProperty("text").GetString() ?? string.Empty, x.GetProperty("documentOrder").GetInt32()))
        .ToArray();

    private static void ValidateClusterFreeze(JsonElement clusters, JsonElement manifest)
    {
        if (clusters.GetProperty("schemaVersion").GetString() != "a99-v5a-source-only-clusters-v1") throw new InvalidDataException("V5B_CLUSTER_SCHEMA_DRIFT");
        if (manifest.GetProperty("status").GetString() != "FROZEN_SOURCE_ONLY_AMBIGUITY_CLUSTERS") throw new InvalidDataException("V5B_CLUSTER_NOT_FROZEN");
        if (manifest.GetProperty("goldReadCount").GetInt32() != 0 || manifest.GetProperty("providerCalls").GetInt32() != 0) throw new InvalidDataException("V5B_CLUSTER_FIREWALL");
        if (manifest.GetProperty("semanticMergePerformed").GetBoolean() || manifest.GetProperty("semanticNodeIdsAssigned").GetBoolean()) throw new InvalidDataException("V5B_CLUSTER_SEMANTIC_CONTAMINATION");
        if (clusters.GetProperty("clusters").GetArrayLength() != ExpectedClusters) throw new InvalidDataException("V5B_CLUSTER_COUNT");
    }

    private static string[] Strings(JsonElement element, string name) => element.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    private static string Required(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new InvalidDataException("V5B_EMPTY_" + name.ToUpperInvariant());
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string BuildReport(object manifest, IReadOnlyList<object> requests) => "# A99 V5B — semantic node induction preflight\n\n" +
        "Status: **READY_FOR_PROVIDER_EXECUTION**. This checkpoint prepares requests only; it does not execute a model/provider or open Gold.\n\n" +
        "## Contract boundary\n\n" +
        "The model is asked to jointly assign occurrences to cluster-local semantic nodes and occurrence roles. It is not asked for pair labels, hierarchy, levels, promotion decisions, or global node IDs.\n\n" +
        "## Firewall\n\n" +
        "- Model/provider calls: **0**.\n- Gold reads: **0**.\n- Known pair labels included: **false**.\n- Hierarchy requested: **false**.\n- Prediction freeze: **false**.\n\n" +
        "## Manifest\n\n```json\n" + JsonSerializer.Serialize(manifest, JsonOptions) + "\n```\n\n" +
        $"Prepared requests: **{requests.Count}**. Each request hash is indexed in `request-index.json`; raw and parsed response artifacts are intentionally absent until an authorized provider campaign.\n";

    private sealed record SourceOccurrence(string OccurrenceId, string Text, int DocumentOrder);
}
