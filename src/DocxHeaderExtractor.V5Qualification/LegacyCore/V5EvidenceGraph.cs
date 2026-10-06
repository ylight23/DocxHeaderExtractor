using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

public enum EvidenceRelationKind
{
    SOURCE_ORDER,
    CONTAINS,
    SAME_PAGE,
    SPATIAL_ADJACENCY,
    HYPERLINK,
}

public sealed record EvidenceGeometry(
    [property: JsonPropertyName("page")] int? Page = null,
    [property: JsonPropertyName("left")] double? Left = null,
    [property: JsonPropertyName("top")] double? Top = null,
    [property: JsonPropertyName("width")] double? Width = null,
    [property: JsonPropertyName("height")] double? Height = null);

public sealed record EvidenceAnchor(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("span")] StructuralSpan Span,
    [property: JsonPropertyName("geometry")] EvidenceGeometry? Geometry = null);

/// <summary>One parser observation. Facts are descriptive; none are semantic labels or scores.</summary>
public sealed record SourceObservation(
    string EvidenceId,
    string SourceId,
    string SourceAlias,
    int SourceOrdinal,
    EvidenceModality Modality,
    string Text,
    StructuralSpan Span,
    EvidenceGeometry? Geometry = null,
    IReadOnlyDictionary<string, string?>? Facts = null);

public sealed record EvidenceNode(
    [property: JsonPropertyName("evidenceId")] string EvidenceId,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceAlias")] string SourceAlias,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("modality")] EvidenceModality Modality,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("anchor")] EvidenceAnchor Anchor,
    [property: JsonPropertyName("facts")] IReadOnlyDictionary<string, string?> Facts)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EvidenceId) || string.IsNullOrWhiteSpace(SourceId) || string.IsNullOrWhiteSpace(SourceAlias))
            throw new InvalidOperationException("evidence-identity-missing");
        if (!Anchor.Span.IsValidFor(Text)) throw new InvalidOperationException("evidence-span-invalid");
        foreach (var key in Facts.Keys)
            if (key.Contains("candidate", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("heading", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("score", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("salience", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("importance", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("semantic-fact-forbidden");
    }
}

public sealed record EvidenceRelation(
    [property: JsonPropertyName("kind")] EvidenceRelationKind Kind,
    [property: JsonPropertyName("fromEvidenceId")] string FromEvidenceId,
    [property: JsonPropertyName("toEvidenceId")] string ToEvidenceId);

public sealed class UniversalEvidenceGraph
{
    public UniversalEvidenceGraph(IEnumerable<EvidenceNode> nodes, IEnumerable<EvidenceRelation> relations)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(relations);
        var nodeList = nodes.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray();
        var ids = nodeList.Select(item => item.EvidenceId).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidOperationException("duplicate-evidence-id");
        foreach (var node in nodeList) node.Validate();
        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        var relationList = relations.ToArray();
        if (relationList.Any(item => !idSet.Contains(item.FromEvidenceId) || !idSet.Contains(item.ToEvidenceId)))
            throw new InvalidOperationException("dangling-evidence-relation");
        Nodes = new ReadOnlyCollection<EvidenceNode>(nodeList);
        Relations = new ReadOnlyCollection<EvidenceRelation>(relationList);
    }

    public IReadOnlyList<EvidenceNode> Nodes { get; }
    public IReadOnlyList<EvidenceRelation> Relations { get; }

    public string Hash()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Nodes, Relations }, CanonicalJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

public static class EvidenceGraphBuilder
{
    public static UniversalEvidenceGraph Build(IEnumerable<SourceObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var list = observations.ToArray();
        var nodes = list.Select(observation => new EvidenceNode(
            observation.EvidenceId,
            observation.SourceId,
            observation.SourceAlias,
            observation.SourceOrdinal,
            observation.Modality,
            observation.Text,
            new EvidenceAnchor(observation.SourceId, observation.SourceOrdinal, observation.Span, observation.Geometry),
            new ReadOnlyDictionary<string, string?>(observation.Facts is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : new Dictionary<string, string?>(observation.Facts, StringComparer.Ordinal)))).ToArray();
        var ordered = nodes.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray();
        var relations = new List<EvidenceRelation>();
        for (var index = 1; index < ordered.Length; index++)
        {
            relations.Add(new(EvidenceRelationKind.SOURCE_ORDER, ordered[index - 1].EvidenceId, ordered[index].EvidenceId));
            if (ordered[index - 1].Anchor.Geometry?.Page is { } pageA && ordered[index].Anchor.Geometry?.Page == pageA)
                relations.Add(new(EvidenceRelationKind.SAME_PAGE, ordered[index - 1].EvidenceId, ordered[index].EvidenceId));
        }
        return new UniversalEvidenceGraph(nodes, relations);
    }
}
