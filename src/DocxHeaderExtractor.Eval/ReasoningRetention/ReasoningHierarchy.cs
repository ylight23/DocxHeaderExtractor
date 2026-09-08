using System.Text.Json.Serialization;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public sealed record ReasoningProposalInventoryItem(
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End,
    [property: JsonPropertyName("exactText")] string ExactText,
    [property: JsonPropertyName("semanticRole")] string SemanticRole);

public sealed record ReasoningHierarchyEdge(
    [property: JsonPropertyName("childProposalId")] string ChildProposalId,
    [property: JsonPropertyName("parentProposalId")] string? ParentProposalId);

public sealed record ReasoningHierarchyValidation(
    [property: JsonPropertyName("acceptedEdges")] IReadOnlyList<ReasoningHierarchyEdge> AcceptedEdges,
    [property: JsonPropertyName("rejectedEdges")] IReadOnlyList<ReasoningHierarchyEdge> RejectedEdges,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors);

public sealed record ReasoningHierarchyModelRequest(
    string DocumentId,
    string Route,
    string RequestId,
    IReadOnlyList<ReasoningProposalInventoryItem> Inventory,
    string SystemPrompt,
    string UserPrompt,
    string ConfigurationSignature);

public sealed record ReasoningHierarchyModelResponse(
    IReadOnlyList<ReasoningHierarchyEdge> Edges,
    string? RawResponseHash = null);

public interface IReasoningHierarchyModel
{
    Task<ReasoningHierarchyModelResponse> CompleteHierarchyAsync(ReasoningHierarchyModelRequest request, CancellationToken ct = default);
}

public static class ReasoningHierarchyPrompt
{
    public const string System = """
You are a document hierarchy resolver. Use only the supplied proposal inventory. Return JSON with
edges containing childProposalId and parentProposalId (or null). Use proposal IDs exactly as
provided. Do not invent source IDs, source occurrences, levels, or text. Invalid edges are
discarded by the harness.
""";

    public static string BuildUser(IReadOnlyList<ReasoningProposalInventoryItem> inventory) =>
        "PROPOSAL_INVENTORY\n" + JsonSerializer.Serialize(inventory) +
        "\nReturn {\"edges\":[{\"childProposalId\":\"...\",\"parentProposalId\":null}]}";
}

public static class ReasoningHierarchyResponseParser
{
    public static ReasoningHierarchyModelResponse Parse(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("reasoning-hierarchy-response-json-incomplete");
        using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
        if (!doc.RootElement.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
            throw new FormatException("reasoning-hierarchy-response-edges-missing");
        var result = new List<ReasoningHierarchyEdge>();
        foreach (var edge in edges.EnumerateArray())
        {
            if (!edge.TryGetProperty("childProposalId", out var child) || child.ValueKind != JsonValueKind.String ||
                !edge.TryGetProperty("parentProposalId", out var parent) ||
                (parent.ValueKind != JsonValueKind.String && parent.ValueKind != JsonValueKind.Null))
                throw new FormatException("reasoning-hierarchy-response-edge-invalid");
            result.Add(new ReasoningHierarchyEdge(child.GetString()!, parent.ValueKind == JsonValueKind.Null ? null : parent.GetString()));
        }
        return new ReasoningHierarchyModelResponse(result);
    }
}

/// <summary>Global, proposal-ID-only hierarchy authority for the reasoning path.</summary>
public static class ReasoningGlobalHierarchyPass
{
    public static string ProposalId(string documentId, string sourceId, StructuralSpan span) =>
        $"proposal:{documentId}:{sourceId}:{span.Start}:{span.End}";

    public static IReadOnlyList<ReasoningProposalInventoryItem> BuildInventory(
        string documentId,
        SourceDocument source,
        IEnumerable<ReasoningHeadingProposal> proposals)
    {
        var ordinals = source.Paragraphs.ToDictionary(item => item.SourceId, item => item.SourceOrdinal, StringComparer.Ordinal);
        return proposals
            .Select(proposal => new ReasoningProposalInventoryItem(
                ProposalId(documentId, proposal.SourceId, proposal.HeadingSpan),
                ordinals.GetValueOrDefault(proposal.SourceId, int.MaxValue),
                proposal.SourceId,
                proposal.HeadingSpan.Start,
                proposal.HeadingSpan.End,
                proposal.Text,
                proposal.SemanticRole))
            .OrderBy(item => item.SourceOrdinal)
            .ThenBy(item => item.Start)
            .ThenBy(item => item.End)
            .ThenBy(item => item.ProposalId, StringComparer.Ordinal)
            .ToArray();
    }

    public static ReasoningHierarchyValidation Validate(
        IReadOnlyList<ReasoningProposalInventoryItem> inventory,
        IEnumerable<ReasoningHierarchyEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(edges);
        var known = inventory.Select(item => item.ProposalId).ToHashSet(StringComparer.Ordinal);
        var rejected = new List<ReasoningHierarchyEdge>();
        var errors = new List<string>();
        var candidates = new List<ReasoningHierarchyEdge>();
        foreach (var edge in edges)
        {
            if (!known.Contains(edge.ChildProposalId) || (edge.ParentProposalId is not null && !known.Contains(edge.ParentProposalId)))
            {
                rejected.Add(edge); errors.Add("parent-missing"); continue;
            }
            if (edge.ParentProposalId == edge.ChildProposalId)
            {
                rejected.Add(edge); errors.Add("parent-self-reference"); continue;
            }
            candidates.Add(edge);
        }

        var byChild = candidates.GroupBy(edge => edge.ChildProposalId, StringComparer.Ordinal);
        var accepted = new Dictionary<string, ReasoningHierarchyEdge>(StringComparer.Ordinal);
        foreach (var group in byChild)
        {
            var nonNull = group.Where(edge => edge.ParentProposalId is not null)
                .OrderBy(edge => edge.ParentProposalId, StringComparer.Ordinal).ToArray();
            if (nonNull.Length == 0) continue;
            accepted[group.Key] = nonNull[0];
            foreach (var duplicate in nonNull.Skip(1))
            {
                rejected.Add(duplicate); errors.Add("multiple-parent-claims");
            }
        }

        foreach (var edge in accepted.Values.OrderBy(edge => edge.ChildProposalId, StringComparer.Ordinal).ToArray())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = edge.ChildProposalId;
            while (accepted.TryGetValue(current, out var parent) && parent.ParentProposalId is not null)
            {
                if (!seen.Add(current))
                {
                    accepted.Remove(edge.ChildProposalId);
                    rejected.Add(edge);
                    errors.Add("parent-cycle");
                    break;
                }
                current = parent.ParentProposalId;
            }
        }

        return new ReasoningHierarchyValidation(
            accepted.Values.OrderBy(edge => edge.ChildProposalId, StringComparer.Ordinal).ToArray(),
            rejected.OrderBy(edge => edge.ChildProposalId, StringComparer.Ordinal).ThenBy(edge => edge.ParentProposalId, StringComparer.Ordinal).ToArray(),
            errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyDictionary<string, int> DeriveLevels(
        IReadOnlyList<ReasoningProposalInventoryItem> inventory,
        IEnumerable<ReasoningHierarchyEdge> acceptedEdges)
    {
        var parent = acceptedEdges.Where(edge => edge.ParentProposalId is not null)
            .ToDictionary(edge => edge.ChildProposalId, edge => edge.ParentProposalId!, StringComparer.Ordinal);
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        int Depth(string id, HashSet<string> path)
        {
            if (levels.TryGetValue(id, out var level)) return level;
            if (!path.Add(id) || !parent.TryGetValue(id, out var parentId)) return levels[id] = 1;
            return levels[id] = Depth(parentId, path) + 1;
        }
        foreach (var item in inventory) _ = Depth(item.ProposalId, new HashSet<string>(StringComparer.Ordinal));
        return levels;
    }
}
