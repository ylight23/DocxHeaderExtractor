using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Identity;

/// <summary>
/// Global semantic resolution after binding. Repeated display headings remain occurrences even
/// when they share one semantic node; the outline projection is the only collapsing step.
/// </summary>
/// <summary>
/// Resolves semantic identity after exact source binding. Explicit same-node and continuation
/// hints are semantic identity decisions; physical occurrences remain distinct without them.
/// Hierarchy consumes the resulting identity key but does not redefine this rule.
/// </summary>
public static class CanonicalSemanticIdentityResolver
{
    /// <summary>Stable identity key shared by text graph resolution and hierarchy diagnostics.</summary>
    public static string CreateNodeKey(CanonicalSemanticBoundHeading item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var sameNodeHint = item.RelationHints.FirstOrDefault(hint =>
            hint.StartsWith("same-node:", StringComparison.Ordinal) ||
            hint.StartsWith("continuation-node:", StringComparison.Ordinal));
        return sameNodeHint is not null
            ? $"explicit:{sameNodeHint}"
            : $"physical:{item.SourceId}:{item.Start}:{item.End}:{item.Alias}";
    }

    public static CanonicalSemanticGraph Resolve(IReadOnlyList<CanonicalSemanticBoundHeading> bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        var ordered = bound.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.Start).ThenBy(item => item.Alias, StringComparer.Ordinal).ToArray();
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var occurrences = new List<CanonicalSemanticGraphOccurrence>(ordered.Length);
        foreach (var (item, index) in ordered.Select((item, index) => (item, index)))
        {
            // Text/role alone is never a semantic identity. A node is shared only when the
            // semantic model supplied an explicit same-node/continuation relation; otherwise
            // each exact physical occurrence remains its own semantic node.
            var parentHint = item.RelationHints.FirstOrDefault(hint =>
                hint.StartsWith("parent-node:", StringComparison.Ordinal));
            var nodeKey = CreateNodeKey(item);
            if (!nodes.TryGetValue(nodeKey, out var nodeId))
            {
                nodeId = $"semantic-node:{nodes.Count + 1:0000}";
                nodes[nodeKey] = nodeId;
            }
            var occurrenceId = $"semantic-occurrence:{index + 1:0000}";
            var kind = occurrences.Any(existing => existing.SemanticNodeId == nodeId)
                ? (item.Scope.Contains("continuation", StringComparison.OrdinalIgnoreCase) ? "CONTINUATION" : "REPEAT")
                : "PRIMARY";
            var parentNodeHint = item.RelationHints.FirstOrDefault(hint => hint.StartsWith("parent-node:", StringComparison.Ordinal));
            var parentNodeId = parentNodeHint is null ? null : parentNodeHint["parent-node:".Length..];
            var parentOccurrence = parentNodeId is null
                ? null
                : occurrences.LastOrDefault(existing => existing.SemanticNodeId == parentNodeId)?.OccurrenceId;
            var levelHint = item.RelationHints.FirstOrDefault(hint => hint.StartsWith("level:", StringComparison.Ordinal));
            var level = levelHint is not null && int.TryParse(levelHint["level:".Length..], out var parsedLevel)
                ? parsedLevel : (int?)null;
            occurrences.Add(new(occurrenceId, nodeId, item.Alias, item.SourceId, item.SourceOrdinal,
                item.Text, item.SemanticRole, item.StructuralType, item.Scope, item.Start, item.End, kind, parentOccurrence)
            {
                Level = level,
                DocumentOrder = new CanonicalSemanticDocumentOrder(
                    item.SourceOrdinal, 0, 0, 0, item.Start)
            });
        }
        var projection = occurrences
            .GroupBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.DocumentOrder?.Page ?? item.SourceOrdinal)
            .ThenBy(item => item.DocumentOrder?.Top ?? 0)
            .ThenBy(item => item.DocumentOrder?.Left ?? 0)
            .ThenBy(item => item.DocumentOrder?.Layer ?? 0)
            .ThenBy(item => item.DocumentOrder?.LocalOrdinal ?? item.Start)
            .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal)
            .ToArray();
        return new CanonicalSemanticGraph(occurrences, projection);
    }
}
