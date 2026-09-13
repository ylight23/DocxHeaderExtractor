using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>One immutable semantic component in a source-backed catalog.</summary>
public sealed record HdsaSemanticCatalogComponent(
    [property: JsonPropertyName("semanticNodeId")] string SemanticNodeId,
    [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("sourceOrder")] int SourceOrder);

/// <summary>
/// A proposed component collapse. The operation carries only the affected component IDs;
/// unrelated catalog entries are not rebuilt or reidentified.
/// </summary>
public sealed record HdsaSemanticMergeOperation(
    [property: JsonPropertyName("mergedSemanticNodeId")] string MergedSemanticNodeId,
    [property: JsonPropertyName("componentSemanticNodeIds")] IReadOnlyList<string> ComponentSemanticNodeIds,
    [property: JsonPropertyName("inferenceSource")] string InferenceSource = "EXPERIMENTAL");

/// <summary>Parent request projection used to prove candidate stability under a merge.</summary>
/// <remarks>
/// A catalog fingerprint is deliberately not part of this semantic request payload. It belongs
/// in the run manifest; embedding a global fingerprint in every node request would make every
/// unrelated request drift when one component changes.
/// </remarks>
public sealed record HdsaMergeIsolationParentRequest(
    [property: JsonPropertyName("childSemanticNodeId")] string ChildSemanticNodeId,
    [property: JsonPropertyName("candidateParentSemanticNodeIds")] IReadOnlyList<string> CandidateParentSemanticNodeIds,
    [property: JsonPropertyName("rootAllowed")] bool RootAllowed,
    [property: JsonPropertyName("localContext")] string? LocalContext,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

public sealed record HdsaSemanticMergeProjectionResult(
    IReadOnlyList<HdsaSemanticCatalogComponent> Components,
    IReadOnlyDictionary<string, string> SemanticNodeProjection,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Merge-isolation contract. It performs a local membership-preserving projection rather than
/// rebuilding every candidate request from a changed catalog cardinality/order.
/// </summary>
public static class HdsaSemanticMergeIsolation
{
    public const string Version = "semantic-merge-isolation-v1";

    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string StableMergedNodeId(IEnumerable<string> memberOccurrenceIds)
    {
        ArgumentNullException.ThrowIfNull(memberOccurrenceIds);
        var members = memberOccurrenceIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (members.Length == 0) throw new ArgumentException("At least one member occurrence is required.", nameof(memberOccurrenceIds));
        return "SN-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', members))))[..16].ToLowerInvariant();
    }

    public static HdsaSemanticMergeProjectionResult Apply(
        IEnumerable<HdsaSemanticCatalogComponent> baseline,
        HdsaSemanticMergeOperation operation)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(operation);

        var entries = baseline.ToArray();
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var byId = entries.ToDictionary(item => item.SemanticNodeId, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(operation.MergedSemanticNodeId)) errors.Add("MERGED_NODE_ID_EMPTY");
        if (byId.ContainsKey(operation.MergedSemanticNodeId)) errors.Add("MERGED_NODE_ID_ALREADY_EXISTS");

        var affectedIds = operation.ComponentSemanticNodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        if (affectedIds.Length < 2) errors.Add("MERGE_REQUIRES_AT_LEAST_TWO_COMPONENTS");
        if (affectedIds.Distinct(StringComparer.Ordinal).Count() != affectedIds.Length) errors.Add("DUPLICATE_MERGE_COMPONENT");
        foreach (var id in affectedIds)
            if (!byId.ContainsKey(id)) errors.Add("UNKNOWN_MERGE_COMPONENT:" + id);

        var affected = affectedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        var occurrenceOwners = affected.SelectMany(entry => entry.MemberOccurrenceIds.Select(occurrence => (occurrence, entry.SemanticNodeId)))
            .GroupBy(item => item.occurrence, StringComparer.Ordinal);
        if (occurrenceOwners.Any(group => group.Select(item => item.SemanticNodeId).Distinct(StringComparer.Ordinal).Count() > 1))
            errors.Add("OVERLAPPING_COMPONENT_MEMBERSHIP");
        if (errors.Count > 0) return new([], new Dictionary<string, string>(StringComparer.Ordinal), errors.Order(StringComparer.Ordinal).ToArray());

        var mergedMembers = affected.OrderBy(item => item.SourceOrder)
            .ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .SelectMany(item => item.MemberOccurrenceIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var merged = new HdsaSemanticCatalogComponent(
            operation.MergedSemanticNodeId,
            mergedMembers,
            string.Join(" ", affected.OrderBy(item => item.SourceOrder).Select(item => item.CanonicalText)),
            affected.Min(item => item.SourceOrder));

        var projected = entries.Where(item => !affectedIds.Contains(item.SemanticNodeId, StringComparer.Ordinal))
            .Append(merged)
            .OrderBy(item => item.SourceOrder)
            .ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .ToArray();
        var mapping = entries.ToDictionary(item => item.SemanticNodeId, item =>
            affectedIds.Contains(item.SemanticNodeId, StringComparer.Ordinal) ? operation.MergedSemanticNodeId : item.SemanticNodeId,
            StringComparer.Ordinal);
        return new(projected, mapping, []);
    }

    public static IReadOnlyList<string> ProjectCandidateReferences(
        IEnumerable<string> candidateSemanticNodeIds,
        IReadOnlyDictionary<string, string> semanticNodeProjection)
    {
        ArgumentNullException.ThrowIfNull(candidateSemanticNodeIds);
        ArgumentNullException.ThrowIfNull(semanticNodeProjection);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidateSemanticNodeIds)
        {
            if (!semanticNodeProjection.TryGetValue(candidate, out var projected))
                throw new InvalidDataException("UNKNOWN_CANDIDATE_REFERENCE:" + candidate);
            if (seen.Add(projected)) result.Add(projected);
        }
        return result;
    }

    public static HdsaMergeIsolationParentRequest ProjectRequest(
        HdsaMergeIsolationParentRequest request,
        IReadOnlyDictionary<string, string> semanticNodeProjection)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(semanticNodeProjection);
        if (!semanticNodeProjection.TryGetValue(request.ChildSemanticNodeId, out var child))
            throw new InvalidDataException("UNKNOWN_CHILD_REFERENCE:" + request.ChildSemanticNodeId);
        return request with
        {
            ChildSemanticNodeId = child,
            CandidateParentSemanticNodeIds = ProjectCandidateReferences(request.CandidateParentSemanticNodeIds, semanticNodeProjection),
        };
    }

    public static string SerializeRequest(HdsaMergeIsolationParentRequest request) =>
        JsonSerializer.Serialize(request, HashOptions);

    public static string RequestHash(HdsaMergeIsolationParentRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SerializeRequest(request)))).ToLowerInvariant();
}
