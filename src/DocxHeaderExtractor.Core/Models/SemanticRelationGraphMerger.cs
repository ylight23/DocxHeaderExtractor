namespace DocxHeaderExtractor.Core.Models;

/// <summary>Which request and which of that request's own node ids a physical occurrence came from.</summary>
public sealed record SemanticGraphProvenance(string PackId, string RequestNodeId);

/// <summary>
/// One physical occurrence in the document-level graph. <see cref="CanonicalNodeId"/> is its bound
/// source-coordinate identity - stable, harness-derived, never a rendering of its text. Every
/// physical occurrence a request bound stays its own node here: merging by identical wording is
/// never done. The only way two occurrences become the same entity is
/// <see cref="EntityGroupId"/>, and that is set only from an explicit SAME_ENTITY/CONTINUES edge.
/// </summary>
public sealed record DocumentSemanticGraphNode(
    string CanonicalNodeId,
    string Function,
    IReadOnlyList<BoundSourcePart> Parts,
    IReadOnlyList<SemanticGraphProvenance> Provenance)
{
    /// <summary>
    /// The entity this occurrence belongs to. Equal to <see cref="CanonicalNodeId"/> until a
    /// SAME_ENTITY or CONTINUES edge joins it to another occurrence; equal group ids across two
    /// nodes is the only fact that says they are the same entity.
    /// </summary>
    public string EntityGroupId { get; init; } = CanonicalNodeId;
}

/// <summary>One relation resolved to document-level (canonical) node identities.</summary>
public sealed record DocumentSemanticGraphRelation(
    string Type, string FromCanonicalNodeId, string ToCanonicalNodeId, string PackId);

public enum DocumentRelationRejectionReason
{
    /// <summary>Accepting this PARENT_OF edge would close a cycle among edges already merged from other packs.</summary>
    CrossPackCycle,

    /// <summary>Two different request-local endpoints resolved to the same canonical node, making this a self-relation.</summary>
    SelfRelationAfterMerge,
}

public sealed record RejectedDocumentSemanticGraphRelation(DocumentSemanticGraphRelation Relation, DocumentRelationRejectionReason Reason);

/// <summary>
/// The merged, document-level graph. Nothing here infers a relation the packs did not assert: a
/// relation a pack could not express (its endpoints spanned two packs) is never approximated - V5-A's
/// contract keeps every relation request-local on purpose, so there is nothing cross-pack to resolve
/// here beyond what pack-local relations already resolve to once every node has a canonical identity.
/// </summary>
public sealed record DocumentSemanticGraph(
    IReadOnlyList<DocumentSemanticGraphNode> Nodes,
    IReadOnlyList<DocumentSemanticGraphRelation> Relations,
    IReadOnlyList<RejectedDocumentSemanticGraphRelation> RejectedRelations);

/// <summary>
/// Merges every pack's already-bound graph (<see cref="BoundSemanticRelationGraph"/>, from
/// <see cref="SemanticRelationGraphBinder"/>) into one document-level graph.
/// <para>
/// Two nodes from different packs collapse into one <see cref="DocumentSemanticGraphNode"/> only
/// when their bound coordinates are byte-identical - the same atom, the same start, the same end.
/// That is not a text match: it is the harness observing that two packs both bound the same physical
/// occurrence, which happens at pack-boundary overlap and must not produce two document nodes for
/// one piece of source.
/// </para>
/// <para>
/// SAME_ENTITY and CONTINUES do something different and are never treated as coordinate identity:
/// they group otherwise-distinct occurrences into one <see cref="DocumentSemanticGraphNode.EntityGroupId"/>
/// while every occurrence stays independently visible. Source order is read directly off each node's
/// own parts; it is never itself a semantic relation.
/// </para>
/// </summary>
public static class SemanticRelationGraphMerger
{
    public static DocumentSemanticGraph Merge(IReadOnlyList<(string PackId, BoundSemanticRelationGraph Graph)> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);

        var byIdentity = new Dictionary<string, DocumentSemanticGraphNode>(StringComparer.Ordinal);
        // (packId, request-local nodeId) -> canonical identity, so pack-local relations resolve.
        var canonicalIdOf = new Dictionary<(string PackId, string NodeId), string>();

        foreach (var (packId, graph) in packs)
        {
            foreach (var node in graph.Nodes)
            {
                var identity = node.Identity;
                canonicalIdOf[(packId, node.NodeId)] = identity;
                if (byIdentity.TryGetValue(identity, out var existing))
                {
                    byIdentity[identity] = existing with
                    {
                        Provenance = [.. existing.Provenance, new SemanticGraphProvenance(packId, node.NodeId)],
                    };
                }
                else
                {
                    byIdentity[identity] = new DocumentSemanticGraphNode(
                        identity, node.Function, node.Parts, [new SemanticGraphProvenance(packId, node.NodeId)]);
                }
            }
        }

        var relations = new List<DocumentSemanticGraphRelation>();
        var rejected = new List<RejectedDocumentSemanticGraphRelation>();
        var parentOfEdges = new List<(string From, string To)>();
        var groupOf = new Dictionary<string, string>(StringComparer.Ordinal);
        string GroupRoot(string id)
        {
            var root = id;
            while (groupOf.TryGetValue(root, out var next) && !string.Equals(next, root, StringComparison.Ordinal))
                root = next;
            return root;
        }
        void Union(string a, string b)
        {
            var rootA = GroupRoot(a);
            var rootB = GroupRoot(b);
            if (string.Equals(rootA, rootB, StringComparison.Ordinal)) return;
            // Deterministic root: the identity that sorts first, so the group id does not depend on
            // pack processing order.
            var (keep, drop) = string.CompareOrdinal(rootA, rootB) <= 0 ? (rootA, rootB) : (rootB, rootA);
            groupOf[drop] = keep;
        }

        foreach (var (packId, graph) in packs)
        {
            foreach (var relation in graph.Relations)
            {
                var from = canonicalIdOf[(packId, relation.FromNodeId)];
                var to = canonicalIdOf[(packId, relation.ToNodeId)];
                var resolved = new DocumentSemanticGraphRelation(relation.Type, from, to, packId);

                if (string.Equals(from, to, StringComparison.Ordinal))
                {
                    // Two different pack-local ids can resolve to the same physical occurrence at a
                    // pack-boundary overlap; the relation the pack drew between them is then a
                    // self-relation once merged, and is rejected the same as any other self-relation.
                    rejected.Add(new(resolved, DocumentRelationRejectionReason.SelfRelationAfterMerge));
                    continue;
                }

                if (string.Equals(relation.Type, SemanticRelationTypes.ParentOf, StringComparison.Ordinal))
                {
                    if (SemanticRelationGraphCycles.WouldFormCycle(parentOfEdges, from, to))
                    {
                        rejected.Add(new(resolved, DocumentRelationRejectionReason.CrossPackCycle));
                        continue;
                    }
                    parentOfEdges.Add((from, to));
                }
                else if (relation.Type is SemanticRelationTypes.SameEntity or SemanticRelationTypes.Continues)
                {
                    Union(from, to);
                }

                relations.Add(resolved);
            }
        }

        var nodes = byIdentity.Values
            .Select(node => node with { EntityGroupId = GroupRoot(node.CanonicalNodeId) })
            .OrderBy(node => node.Parts[0].Ordinal).ThenBy(node => node.Parts[0].Start)
            .ToArray();

        return new DocumentSemanticGraph(nodes, relations, rejected);
    }
}
