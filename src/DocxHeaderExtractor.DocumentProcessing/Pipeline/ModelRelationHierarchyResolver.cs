using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Turns the immediate-parent relations a model returned into harness-owned levels.
/// <para>
/// The split of authority is the point: the model decides which heading is whose parent and which
/// occurrences are one section; this resolver only validates those claims and counts depth. It
/// never reads numbering, style, indentation or adjacency, so a document's numbering shape can
/// never re-enter as a level authority.
/// </para>
/// </summary>
internal static class ModelRelationHierarchyResolver
{
    private const string ParentHintPrefix = "parent-node:";
    private const string RootParent = "ROOT";
    private const string NoHierarchy = "NONE";

    /// <summary>A heading the model placed under an earlier heading.</summary>
    internal const string ResolvedParent = "model-parent-relation";

    /// <summary>A heading the model placed at the top of the section tree.</summary>
    internal const string ResolvedRoot = "model-root";

    /// <summary>
    /// A heading the model positively determined to sit OUTSIDE the section tree: a document
    /// title, a running header, a table or figure label, a form label. It is a real heading and is
    /// reported as one, but it has no level and may not be anyone's parent — letting one act as a
    /// parent pushes every real section down a level and shifts the whole document.
    /// </summary>
    internal const string OutOfHierarchy = "model-out-of-hierarchy";

    /// <summary>
    /// The model did not decide. Distinct from <see cref="OutOfHierarchy"/>: this one is an
    /// absence of judgement and belongs in a review queue, that one is a judgement.
    /// </summary>
    internal const string Unresolved = "unresolved";

    /// <summary>Harness-owned hierarchy for one heading, derived from model parent relations.</summary>
    /// <param name="SemanticNodeKey">
    /// Which semantic section this occurrence belongs to. Shared only when the model said so with
    /// a same-node hint; otherwise every physical occurrence is its own node, because identical
    /// wording alone is not identity.
    /// </param>
    /// <param name="IsPrimaryOccurrence">
    /// False for a repeat of a node already seen earlier in document order. Repeats stay real
    /// occurrences; only the outline projection collapses them.
    /// </param>
    internal readonly record struct DerivedHeadingHierarchy(
        string SourceId,
        int Level,
        string? ParentSourceId,
        string Resolution,
        string SemanticNodeKey,
        bool IsPrimaryOccurrence);

    /// <summary>
    /// Level is derived from the immediate-parent relations the model returned, never from
    /// numbering shape. The harness owns validation and the arithmetic: a parent must be a bound
    /// heading that precedes its child in document order, self-parenting is rejected, and a child
    /// whose parent cannot be resolved stays unresolved instead of inheriting a guessed depth.
    /// </summary>
    internal static IReadOnlyList<DerivedHeadingHierarchy> DeriveHierarchyFromModelRelations(
        IReadOnlyList<CanonicalSemanticBoundHeading> bound)
    {
        var ordered = bound
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.Start).First())
            .OrderBy(item => item.SourceOrdinal)
            .ThenBy(item => item.Start)
            .ToArray();
        var sourceIdByAlias = ordered.ToDictionary(item => item.Alias, item => item.SourceId, StringComparer.Ordinal);
        var ordinalBySourceId = ordered
            .Select((item, index) => (item.SourceId, index))
            .ToDictionary(item => item.SourceId, item => item.index, StringComparer.Ordinal);

        // Pass one reads the model's own words: which headings sit outside the tree entirely.
        // It must finish before any parent is accepted, because a heading outside the tree cannot
        // be a parent and the claim can arrive after the child that names it.
        var outsideHierarchy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in ordered)
        {
            var hint = heading.RelationHints.FirstOrDefault(item =>
                item.StartsWith(ParentHintPrefix, StringComparison.Ordinal));
            if (hint is not null &&
                string.Equals(hint[ParentHintPrefix.Length..], NoHierarchy, StringComparison.OrdinalIgnoreCase))
                outsideHierarchy.Add(heading.SourceId);
        }

        var parentBySourceId = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var heading in ordered)
        {
            if (outsideHierarchy.Contains(heading.SourceId)) continue;
            var hint = heading.RelationHints.FirstOrDefault(item =>
                item.StartsWith(ParentHintPrefix, StringComparison.Ordinal));
            if (hint is null) continue;
            var target = hint[ParentHintPrefix.Length..];
            if (string.Equals(target, RootParent, StringComparison.OrdinalIgnoreCase))
            {
                parentBySourceId[heading.SourceId] = null;
                continue;
            }
            if (!sourceIdByAlias.TryGetValue(target, out var parentSourceId)) continue;
            if (outsideHierarchy.Contains(parentSourceId)) continue;
            if (string.Equals(parentSourceId, heading.SourceId, StringComparison.Ordinal)) continue;
            if (ordinalBySourceId[parentSourceId] >= ordinalBySourceId[heading.SourceId]) continue;
            parentBySourceId[heading.SourceId] = parentSourceId;
        }

        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DerivedHeadingHierarchy>(ordered.Length);
        foreach (var heading in ordered)
        {
            var resolved = parentBySourceId.TryGetValue(heading.SourceId, out var parentSourceId);
            var level = 1;
            if (resolved && parentSourceId is not null)
                level = levels.TryGetValue(parentSourceId, out var parentLevel) ? parentLevel + 1 : 1;
            levels[heading.SourceId] = Math.Clamp(level, 1, 9);
            // Identity ownership lives in the semantic stage. Hierarchy only consumes its stable
            // key to mark the primary occurrence without re-defining same-node semantics.
            var nodeKey = CanonicalSemanticIdentityResolver.CreateNodeKey(heading);
            result.Add(new DerivedHeadingHierarchy(
                heading.SourceId,
                levels[heading.SourceId],
                resolved ? parentSourceId : null,
                outsideHierarchy.Contains(heading.SourceId) ? OutOfHierarchy
                    : !resolved ? Unresolved
                    : parentSourceId is null ? ResolvedRoot : ResolvedParent,
                nodeKey,
                seenNodes.Add(nodeKey)));
        }
        return result;
    }
}
