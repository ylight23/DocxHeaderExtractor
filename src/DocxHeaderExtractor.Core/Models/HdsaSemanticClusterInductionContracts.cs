using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public enum HdsaSemanticClusterOccurrenceRole
{
    [JsonStringEnumMemberName("PRIMARY")] Primary,
    [JsonStringEnumMemberName("REPEAT")] Repeat,
    [JsonStringEnumMemberName("CONTINUATION")] Continuation,
    [JsonStringEnumMemberName("ALIAS_REPRESENTATION")] AliasRepresentation,
}

public enum HdsaSemanticClusterConfidence
{
    [JsonStringEnumMemberName("HIGH")] High,
    [JsonStringEnumMemberName("MEDIUM")] Medium,
    [JsonStringEnumMemberName("LOW")] Low,
}

public sealed record HdsaSemanticClusterContextOccurrence(
    [property: JsonPropertyName("occurrenceId")] string OccurrenceId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("documentOrder")] int DocumentOrder);

public sealed record HdsaSemanticClusterOccurrence(
    [property: JsonPropertyName("occurrenceId")] string OccurrenceId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("documentOrder")] int DocumentOrder,
    [property: JsonPropertyName("previousSourceOccurrences")] IReadOnlyList<HdsaSemanticClusterContextOccurrence> PreviousSourceOccurrences,
    [property: JsonPropertyName("nextSourceOccurrences")] IReadOnlyList<HdsaSemanticClusterContextOccurrence> NextSourceOccurrences);

public sealed record HdsaSemanticClusterEvidence(
    [property: JsonPropertyName("evidenceReasons")] IReadOnlyList<string> EvidenceReasons,
    [property: JsonPropertyName("evidenceConfigurations")] IReadOnlyList<string> EvidenceConfigurations,
    [property: JsonPropertyName("sourceOrderDistanceBuckets")] IReadOnlyList<string> SourceOrderDistanceBuckets,
    [property: JsonPropertyName("packetClasses")] IReadOnlyList<string> PacketClasses,
    [property: JsonPropertyName("candidateIds")] IReadOnlyList<string> CandidateIds);

public sealed record HdsaSemanticClusterInductionRequest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("clusterId")] string ClusterId,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("sourceCatalogSha256")] string SourceCatalogSha256,
    [property: JsonPropertyName("clusterNamespace")] string ClusterNamespace,
    [property: JsonPropertyName("occurrences")] IReadOnlyList<HdsaSemanticClusterOccurrence> Occurrences,
    [property: JsonPropertyName("sourceEvidence")] HdsaSemanticClusterEvidence SourceEvidence,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false,
    [property: JsonPropertyName("knownPairLabelsIncluded")] bool KnownPairLabelsIncluded = false,
    [property: JsonPropertyName("hierarchyRequested")] bool HierarchyRequested = false);

public sealed record HdsaSemanticClusterOccurrenceAssignment(
    [property: JsonPropertyName("occurrenceId")] string OccurrenceId,
    [property: JsonPropertyName("semanticNodeLocalId")] string SemanticNodeLocalId,
    [property: JsonPropertyName("occurrenceRole")] HdsaSemanticClusterOccurrenceRole OccurrenceRole,
    [property: JsonPropertyName("confidence")] HdsaSemanticClusterConfidence Confidence);

public sealed record HdsaSemanticClusterNode(
    [property: JsonPropertyName("semanticNodeLocalId")] string SemanticNodeLocalId,
    [property: JsonPropertyName("canonicalMeaning")] string CanonicalMeaning,
    [property: JsonPropertyName("structuralScopeDescription")] string StructuralScopeDescription,
    [property: JsonPropertyName("organizationalFunction")] string OrganizationalFunction);

public sealed record HdsaSemanticClusterContinuationEdge(
    [property: JsonPropertyName("fromOccurrenceId")] string FromOccurrenceId,
    [property: JsonPropertyName("toOccurrenceId")] string ToOccurrenceId);

public sealed record HdsaSemanticClusterInductionResponse(
    [property: JsonPropertyName("clusterId")] string ClusterId,
    [property: JsonPropertyName("occurrenceAssignments")] IReadOnlyList<HdsaSemanticClusterOccurrenceAssignment> OccurrenceAssignments,
    [property: JsonPropertyName("semanticNodes")] IReadOnlyList<HdsaSemanticClusterNode> SemanticNodes,
    [property: JsonPropertyName("continuationEdges")] IReadOnlyList<HdsaSemanticClusterContinuationEdge> ContinuationEdges,
    [property: JsonPropertyName("unresolvedOccurrenceIds")] IReadOnlyList<string> UnresolvedOccurrenceIds);

public sealed record HdsaSemanticClusterInductionValidation(
    bool Accepted,
    bool Complete,
    IReadOnlyList<string> Errors)
{
    public static HdsaSemanticClusterInductionValidation Accept() => new(true, true, Array.Empty<string>());
}

public static class HdsaSemanticClusterInductionContract
{
    public const string Version = "a99-v5b-global-cluster-semantic-node-induction-v1";

    public static string Namespace(string documentId, string clusterId, string localNodeId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(clusterId) || string.IsNullOrWhiteSpace(localNodeId))
            throw new ArgumentException("Document, cluster, and local node IDs are required.");
        return $"V5:{documentId}:{clusterId}:{localNodeId}";
    }

    public static HdsaSemanticClusterInductionValidation Validate(
        HdsaSemanticClusterInductionRequest request,
        HdsaSemanticClusterInductionResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        var errors = new List<string>();
        var occurrenceIds = request.Occurrences.Select(x => x.OccurrenceId).ToHashSet(StringComparer.Ordinal);
        var assignments = response.OccurrenceAssignments ?? Array.Empty<HdsaSemanticClusterOccurrenceAssignment>();
        var unresolved = response.UnresolvedOccurrenceIds ?? Array.Empty<string>();
        var nodes = response.SemanticNodes ?? Array.Empty<HdsaSemanticClusterNode>();
        var edges = response.ContinuationEdges ?? Array.Empty<HdsaSemanticClusterContinuationEdge>();

        if (!string.Equals(response.ClusterId, request.ClusterId, StringComparison.Ordinal)) errors.Add("CLUSTER_ID_MISMATCH");
        if (occurrenceIds.Count != request.Occurrences.Count) errors.Add("REQUEST_DUPLICATE_OCCURRENCE_ID");
        if (assignments.Select(x => x.OccurrenceId).Distinct(StringComparer.Ordinal).Count() != assignments.Count) errors.Add("DUPLICATE_ASSIGNMENT");
        if (unresolved.Distinct(StringComparer.Ordinal).Count() != unresolved.Count) errors.Add("DUPLICATE_UNRESOLVED_ID");
        if (assignments.Any(x => !occurrenceIds.Contains(x.OccurrenceId))) errors.Add("UNKNOWN_ASSIGNMENT_OCCURRENCE");
        if (unresolved.Any(x => !occurrenceIds.Contains(x))) errors.Add("UNKNOWN_UNRESOLVED_OCCURRENCE");
        if (assignments.Any(x => unresolved.Contains(x.OccurrenceId, StringComparer.Ordinal))) errors.Add("ASSIGNED_AND_UNRESOLVED");
        if (assignments.Count + unresolved.Count != occurrenceIds.Count) errors.Add("INCOMPLETE_OCCURRENCE_COVERAGE");
        if (nodes.Any(x => string.IsNullOrWhiteSpace(x.SemanticNodeLocalId))) errors.Add("EMPTY_LOCAL_NODE_ID");
        if (nodes.Select(x => x.SemanticNodeLocalId).Distinct(StringComparer.Ordinal).Count() != nodes.Count) errors.Add("DUPLICATE_LOCAL_NODE_ID");
        var nodeIds = nodes.Select(x => x.SemanticNodeLocalId).ToHashSet(StringComparer.Ordinal);
        if (assignments.Any(x => !nodeIds.Contains(x.SemanticNodeLocalId))) errors.Add("UNKNOWN_LOCAL_NODE_ID");
        if (nodes.Any(x => string.IsNullOrWhiteSpace(x.CanonicalMeaning) || string.IsNullOrWhiteSpace(x.OrganizationalFunction))) errors.Add("EMPTY_NODE_DEFINITION");
        if (edges.Any(x => string.Equals(x.FromOccurrenceId, x.ToOccurrenceId, StringComparison.Ordinal))) errors.Add("SELF_CONTINUATION");
        if (edges.Select(x => $"{x.FromOccurrenceId}\u001f{x.ToOccurrenceId}").Distinct(StringComparer.Ordinal).Count() != edges.Count) errors.Add("DUPLICATE_CONTINUATION_EDGE");
        if (edges.Any(x => !occurrenceIds.Contains(x.FromOccurrenceId) || !occurrenceIds.Contains(x.ToOccurrenceId))) errors.Add("UNKNOWN_CONTINUATION_OCCURRENCE");

        var assignmentByOccurrence = assignments.ToDictionary(x => x.OccurrenceId, StringComparer.Ordinal);
        if (edges.Any(x => !assignmentByOccurrence.ContainsKey(x.FromOccurrenceId) || !assignmentByOccurrence.ContainsKey(x.ToOccurrenceId))) errors.Add("CONTINUATION_TO_UNRESOLVED");
        if (edges.Any(x => assignmentByOccurrence.TryGetValue(x.FromOccurrenceId, out var from) && assignmentByOccurrence.TryGetValue(x.ToOccurrenceId, out var to) && !string.Equals(from.SemanticNodeLocalId, to.SemanticNodeLocalId, StringComparison.Ordinal))) errors.Add("CONTINUATION_CROSS_NODE");
        if (HasCycle(edges)) errors.Add("CONTINUATION_CYCLE");

        var complete = assignments.Count + unresolved.Count == occurrenceIds.Count && errors.Count == 0;
        return new HdsaSemanticClusterInductionValidation(errors.Count == 0 && complete, complete, new ReadOnlyCollection<string>(errors));
    }

    public static string RequestHash(HdsaSemanticClusterInductionRequest request)
    {
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static bool HasCycle(IReadOnlyList<HdsaSemanticClusterContinuationEdge> edges)
    {
        var graph = edges.GroupBy(x => x.FromOccurrenceId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(e => e.ToOccurrenceId).ToArray(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string node)
        {
            if (!visiting.Add(node)) return true;
            if (visited.Contains(node)) { visiting.Remove(node); return false; }
            if (graph.TryGetValue(node, out var next) && next.Any(Visit)) return true;
            visiting.Remove(node);
            visited.Add(node);
            return false;
        }
        return graph.Keys.Any(Visit);
    }
}
