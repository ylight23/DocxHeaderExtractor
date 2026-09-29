namespace DocxHeaderExtractor.Core.Models;

/// <summary>One DOCUMENT_IDENTITY occurrence, rendered from its bound parts.</summary>
public sealed record DocumentIdentityOccurrence(string CanonicalNodeId, string Text, IReadOnlyList<BoundSourcePart> Parts);

/// <summary>
/// One REGION_LABEL occurrence projected as a structural heading. <see cref="ParentCanonicalNodeId"/>
/// is set only from a validated PARENT_OF edge whose parent is itself a REGION_LABEL occurrence - a
/// PARENT_OF edge onto a non-heading occurrence is not materialized as a heading parent.
/// </summary>
public sealed record CanonicalHeadingOccurrence(
    string CanonicalNodeId, string Text, IReadOnlyList<BoundSourcePart> Parts, string? ParentCanonicalNodeId);

/// <summary>
/// One heading's place in the outline. <see cref="Depth"/> is counted by the harness by walking
/// validated PARENT_OF edges; the model never states a level.
/// </summary>
public sealed record CanonicalOutlineEntry(
    string CanonicalNodeId, string Text, int Depth, string? ParentCanonicalNodeId);

/// <summary>
/// One NAVIGATION_REFERENCE occurrence. <see cref="ReferencedCanonicalNodeIds"/> lists what it points
/// at via REFERENCES edges; those targets are never themselves promoted to headings by this
/// projection, whatever their own function is.
/// </summary>
public sealed record CanonicalNavigationOccurrence(
    string CanonicalNodeId, string Text, IReadOnlyList<BoundSourcePart> Parts,
    IReadOnlyList<string> ReferencedCanonicalNodeIds);

/// <summary>
/// Deterministic, pure projections of a validated <see cref="DocumentSemanticGraph"/>. Every
/// projection reads only <see cref="DocumentSemanticGraphNode.Function"/> and validated relations of
/// the matching type - never <c>CanonicalSemanticProposal.IsHeading</c>, never
/// <c>CanonicalSemanticBoundHeading</c>, never a <c>relationHints</c> string, never
/// <c>PdfSemanticRole</c>/<c>PdfBlockRole</c>. A function this project does not yet define a
/// projection for (PAGE_FURNITURE, OBJECT_LABEL, TABLE_STRUCTURE, FOOTNOTE_OR_SOURCE,
/// BODY_INFORMATION, METADATA) never becomes a structural heading through any of these.
/// </summary>
public static class SemanticRelationGraphProjection
{
    public static IReadOnlyList<DocumentIdentityOccurrence> DocumentIdentity(DocumentSemanticGraph graph) =>
        ByFunction(graph, SemanticOccurrenceFunctions.DocumentIdentity)
            .Select(node => new DocumentIdentityOccurrence(node.CanonicalNodeId, Render(node), node.Parts))
            .ToArray();

    public static IReadOnlyList<CanonicalHeadingOccurrence> CanonicalHeadings(DocumentSemanticGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var headingIds = ByFunction(graph, SemanticOccurrenceFunctions.RegionLabel)
            .Select(node => node.CanonicalNodeId)
            .ToHashSet(StringComparer.Ordinal);

        // Only the parent from THIS document's own accepted edges, and only when that parent is
        // itself a heading occurrence - a PARENT_OF edge onto a PAGE_FURNITURE or METADATA node
        // names a relation, not a heading ancestor, so it is not materialized here.
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relation in graph.Relations)
            if (string.Equals(relation.Type, SemanticRelationTypes.ParentOf, StringComparison.Ordinal) &&
                headingIds.Contains(relation.ToCanonicalNodeId) && headingIds.Contains(relation.FromCanonicalNodeId))
                parentOf[relation.ToCanonicalNodeId] = relation.FromCanonicalNodeId;

        return ByFunction(graph, SemanticOccurrenceFunctions.RegionLabel)
            .Select(node => new CanonicalHeadingOccurrence(
                node.CanonicalNodeId, Render(node), node.Parts,
                parentOf.GetValueOrDefault(node.CanonicalNodeId)))
            .ToArray();
    }

    public static IReadOnlyList<CanonicalOutlineEntry> CanonicalOutline(DocumentSemanticGraph graph)
    {
        var headings = CanonicalHeadings(graph);
        var byId = headings.ToDictionary(heading => heading.CanonicalNodeId, StringComparer.Ordinal);

        int DepthOf(string nodeId)
        {
            var depth = 0;
            var current = nodeId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            // PARENT_OF cycles are already rejected before a graph reaches this projection; the
            // visited guard is a defensive bound, not the mechanism that keeps this deterministic.
            while (byId.TryGetValue(current, out var node) && node.ParentCanonicalNodeId is { } parent && visited.Add(current))
            {
                depth++;
                current = parent;
            }
            return depth;
        }

        return headings
            .Select(heading => new CanonicalOutlineEntry(
                heading.CanonicalNodeId, heading.Text, DepthOf(heading.CanonicalNodeId), heading.ParentCanonicalNodeId))
            .ToArray();
    }

    public static IReadOnlyList<CanonicalNavigationOccurrence> CanonicalNavigation(DocumentSemanticGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var navigationIds = ByFunction(graph, SemanticOccurrenceFunctions.NavigationReference)
            .Select(node => node.CanonicalNodeId)
            .ToHashSet(StringComparer.Ordinal);

        var references = graph.Relations
            .Where(relation => string.Equals(relation.Type, SemanticRelationTypes.References, StringComparison.Ordinal) &&
                navigationIds.Contains(relation.FromCanonicalNodeId))
            .GroupBy(relation => relation.FromCanonicalNodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(relation => relation.ToCanonicalNodeId).ToArray(),
                StringComparer.Ordinal);

        return ByFunction(graph, SemanticOccurrenceFunctions.NavigationReference)
            .Select(node => new CanonicalNavigationOccurrence(
                node.CanonicalNodeId, Render(node), node.Parts,
                references.GetValueOrDefault(node.CanonicalNodeId, [])))
            .ToArray();
    }

    private static IEnumerable<DocumentSemanticGraphNode> ByFunction(DocumentSemanticGraph graph, string function)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Nodes
            .Where(node => string.Equals(node.Function, function, StringComparison.Ordinal))
            .OrderBy(node => node.Parts[0].Ordinal)
            .ThenBy(node => node.Parts[0].Start);
    }

    private static string Render(DocumentSemanticGraphNode node) => SemanticSourceProjection.Render(node.Parts);
}
