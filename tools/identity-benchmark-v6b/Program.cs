using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV6B;

internal static class Program
{
    private const string RequestsRelative = "artifacts/identity-benchmark/v5/semantic-node-induction/preflight/requests.json";
    private const string ClustersRelative = "artifacts/identity-benchmark/v5/source-only-clusters/freeze/clusters.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v6/owner-evidence/source-only-freeze-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V6B_STATUS=SOURCE_ONLY_OWNER_EVIDENCE_FROZEN MODEL_CALLS=0 PROVIDER_CALLS=0 GOLD_READ_COUNT=0 SEMANTIC_OWNER_ASSIGNMENTS=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V6B_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        using var requests = Read(Full(root, RequestsRelative));
        using var clusters = Read(Full(root, ClustersRelative));
        ValidateInputs(requests.RootElement, clusters.RootElement);
        var clusterByOccurrence = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var evidenceByCluster = clusters.RootElement.GetProperty("clusters").EnumerateArray().ToDictionary(x => x.GetProperty("clusterId").GetString()!, StringComparer.Ordinal);
        var packets = new Dictionary<string, PacketBuilder>(StringComparer.Ordinal);
        foreach (var request in requests.RootElement.GetProperty("requests").EnumerateArray())
        {
            var clusterId = request.GetProperty("clusterId").GetString()!;
            var documentId = request.GetProperty("documentId").GetString()!;
            foreach (var occurrence in request.GetProperty("occurrences").EnumerateArray())
            {
                var id = occurrence.GetProperty("occurrenceId").GetString()!;
                if (!packets.TryGetValue(id, out var packet))
                {
                    var container = DeriveContainer(id);
                    packet = new PacketBuilder(id, documentId, occurrence.GetProperty("text").GetString()!, occurrence.GetProperty("documentOrder").GetInt32(), container, Kind(container));
                    packets[id] = packet;
                }
                packet.ClusterIds.Add(clusterId);
                packet.EvidenceReasons.UnionWith(evidenceByCluster[clusterId].GetProperty("evidenceReasons").EnumerateArray().Select(x => x.GetString()!));
                packet.PacketClasses.UnionWith(evidenceByCluster[clusterId].GetProperty("packetClasses").EnumerateArray().Select(x => x.GetString()!));
                packet.CandidateIds.UnionWith(evidenceByCluster[clusterId].GetProperty("candidateIds").EnumerateArray().Select(x => x.GetString()!));
                packet.Previous = ReadNeighbors(occurrence, "previousSourceOccurrences");
                packet.Next = ReadNeighbors(occurrence, "nextSourceOccurrences");
            }
        }

        var packetRows = packets.Values.OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.DocumentOrder).ThenBy(x => x.OccurrenceId, StringComparer.Ordinal).Select(x => x.ToRow()).ToArray();
        var groups = packetRows.GroupBy(x => x.SourceContainerIdentity, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => new { sourceContainerIdentity = g.Key, sourceUnitKind = g.First().SourceUnitKind, documentIds = g.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(), occurrenceCount = g.Count(), occurrenceIds = g.Select(x => x.OccurrenceId).ToArray() }).ToArray();
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "manifest.json"), new { schemaVersion = "a99-v6b-owner-evidence-manifest-v1", status = "SOURCE_ONLY_OWNER_EVIDENCE_FROZEN", inputRequests = Relative(root, Full(root, RequestsRelative)), inputClusters = Relative(root, Full(root, ClustersRelative)), packetCount = packetRows.Length, scopeGroupCount = groups.Length, modelCalls = 0, providerCalls = 0, goldReadCount = 0, semanticOwnerAssignments = 0, semanticNodeAssignments = 0, ownerDecisions = "NONE", note = "Parser-owned container evidence only. This artifact is an owner-induction input boundary, not semantic owner Gold." });
        await WriteAsync(Path.Combine(output, "packets.json"), packetRows);
        await WriteAsync(Path.Combine(output, "scope-groups.json"), groups);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(packetRows, groups), new UTF8Encoding(false));
    }

    private static void ValidateInputs(JsonElement requests, JsonElement clusters)
    {
        Require(requests.GetProperty("sourceOnly").GetBoolean(), "V6B_REQUESTS_NOT_SOURCE_ONLY");
        Require(requests.GetProperty("requests").GetArrayLength() == 102, "V6B_REQUEST_COUNT");
        Require(clusters.GetProperty("sourceOnly").GetBoolean(), "V6B_CLUSTERS_NOT_SOURCE_ONLY");
        Require(clusters.GetProperty("clusters").GetArrayLength() == 102, "V6B_CLUSTER_COUNT");
    }

    private static string DeriveContainer(string occurrenceId)
    {
        var path = occurrenceId.Split(':', 2).ElementAtOrDefault(1) ?? occurrenceId;
        var table = path.IndexOf("/tbl[", StringComparison.Ordinal);
        if (table >= 0)
        {
            var end = path.IndexOf(']', table);
            return path[..(end >= 0 ? end + 1 : path.Length)];
        }
        var paragraph = path.IndexOf("/p[", StringComparison.Ordinal);
        if (paragraph >= 0) return path[..paragraph];
        return "TEXT_STREAM";
    }

    private static string Kind(string container) => container == "TEXT_STREAM" ? "TEXT_STREAM" : container.Contains("/tbl[", StringComparison.Ordinal) ? "TABLE_CONTAINER" : "DOCUMENT_CONTAINER";
    private static object[] ReadNeighbors(JsonElement occurrence, string property) => occurrence.TryGetProperty(property, out var value) ? value.EnumerateArray().Select(x => (object)new { occurrenceId = x.GetProperty("occurrenceId").GetString(), text = x.GetProperty("text").GetString(), documentOrder = x.GetProperty("documentOrder").GetInt32() }).ToArray() : Array.Empty<object>();
    private static string BuildReport(IReadOnlyList<Packet> packets, IReadOnlyList<object> groups) => "# A99 V6B — source-only structural owner evidence freeze\n\n" + "This phase freezes parser-owned container and context evidence only. It does not assign semantic structural owners, does not read Gold, and does not call a model/provider. `sourceContainerIdentity` is an evidence key, not an autonomous-document-unit decision.\n\n" + JsonSerializer.Serialize(new { packetCount = packets.Count, scopeGroupCount = groups.Count, documentCounts = packets.GroupBy(x => x.DocumentId).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { documentId = x.Key, occurrences = x.Count() }).ToArray(), sourceUnitKinds = packets.GroupBy(x => x.SourceUnitKind).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { kind = x.Key, occurrences = x.Count() }).ToArray() }, JsonOptions) + "\n";
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed class PacketBuilder
    {
        public PacketBuilder(string occurrenceId, string documentId, string text, int documentOrder, string sourceContainerIdentity, string sourceUnitKind) { OccurrenceId = occurrenceId; DocumentId = documentId; Text = text; DocumentOrder = documentOrder; SourceContainerIdentity = sourceContainerIdentity; SourceUnitKind = sourceUnitKind; }
        public string OccurrenceId { get; }
        public string DocumentId { get; }
        public string Text { get; }
        public int DocumentOrder { get; }
        public string SourceContainerIdentity { get; }
        public string SourceUnitKind { get; }
        public object[] Previous { get; set; } = Array.Empty<object>();
        public object[] Next { get; set; } = Array.Empty<object>();
        public HashSet<string> ClusterIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CandidateIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EvidenceReasons { get; } = new(StringComparer.Ordinal);
        public HashSet<string> PacketClasses { get; } = new(StringComparer.Ordinal);
        public Packet ToRow() => new(OccurrenceId, DocumentId, Text, DocumentOrder, SourceContainerIdentity, SourceUnitKind, Previous, Next, ClusterIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(), CandidateIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(), EvidenceReasons.OrderBy(x => x, StringComparer.Ordinal).ToArray(), PacketClasses.OrderBy(x => x, StringComparer.Ordinal).ToArray(), false, null);
    }

    private sealed record Packet(string OccurrenceId, string DocumentId, string Text, int DocumentOrder, string SourceContainerIdentity, string SourceUnitKind, object[] PreviousSourceOccurrences, object[] NextSourceOccurrences, string[] ClusterIds, string[] CandidateIds, string[] EvidenceReasons, string[] PacketClasses, bool SemanticOwnerAssigned, string? SemanticOwnerId);
}
