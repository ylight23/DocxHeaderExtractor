namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// One graph node after the harness resolved its source parts to coordinates. <see cref="Identity"/>
/// is the ordered coordinate tuple of its parts - the same shape as
/// <see cref="SemanticSourcePartsBinding.Identity"/> - and is what makes two nodes the same physical
/// occurrence: never text, never a rendering, only coordinates the harness itself resolved.
/// </summary>
public sealed record BoundSemanticGraphNode(
    string NodeId,
    string Function,
    IReadOnlyList<BoundSourcePart> Parts)
{
    public string Identity => string.Join("|", Parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
}

/// <summary>One accepted relation, still addressed by this reply's own request-local node ids.</summary>
public sealed record BoundSemanticGraphRelation(string Type, string FromNodeId, string ToNodeId);

public enum SemanticGraphNodeRejectionReason
{
    /// <summary>The binder refused its source parts: unknown alias, text not in the atom, ambiguous, etc.</summary>
    UnboundSource,

    /// <summary>One of its source parts names an alias this request does not own - a halo occurrence
    /// read as context, never claimable as a node.</summary>
    OwnershipViolation,

    /// <summary>A later node in the same reply declared the same nodeId; the first is kept.</summary>
    DuplicateNodeId,
}

public sealed record RejectedSemanticGraphNode(string NodeId, SemanticGraphNodeRejectionReason Reason, string? Detail);

public enum SemanticGraphRelationRejectionReason
{
    /// <summary>An endpoint does not name any node the request declared.</summary>
    UnknownEndpoint,

    /// <summary>An endpoint names a node that itself was rejected, so it is not a bound graph node.</summary>
    EndpointNodeRejected,

    /// <summary>A node related to itself. Rejected for every relation type, not only PARENT_OF.</summary>
    SelfRelation,

    /// <summary>Accepting this PARENT_OF edge would close a cycle among edges already accepted in this reply.</summary>
    CycleWouldForm,
}

public sealed record RejectedSemanticGraphRelation(
    SemanticGraphRelationProposal Relation, SemanticGraphRelationRejectionReason Reason);

/// <summary>
/// One reply's fully validated graph: every node and relation the reply may claim, and every one it
/// may not, with why. Nothing here is silently dropped - a node or relation not in the accepted lists
/// is in exactly one rejection list, and every rejection carries a machine-readable reason.
/// </summary>
public sealed record BoundSemanticRelationGraph(
    IReadOnlyList<BoundSemanticGraphNode> Nodes,
    IReadOnlyList<BoundSemanticGraphRelation> Relations,
    IReadOnlyList<RejectedSemanticGraphNode> RejectedNodes,
    IReadOnlyList<RejectedSemanticGraphRelation> RejectedRelations,
    int DeduplicatedRelationCount);

/// <summary>
/// Binds a V5 reply's nodes to exact source coordinates and validates its relations, entirely on top
/// of the existing structured source-parts infrastructure.
/// <para>
/// Node binding is exactly the V2/V4 pipeline applied to each node's own parts:
/// <see cref="SemanticSourcePartCanonicalizer.Canonicalize"/> resolves the selection mode the model
/// never states, then <see cref="SemanticSourcePartBinder.Bind"/> resolves coordinates. This is
/// deliberately not a second binder: the same refusal codes (unknown alias, text not in atom,
/// ambiguous selection, out of source order, overlap) mean the same thing here as everywhere else
/// this vocabulary is used.
/// </para>
/// <para>
/// P05 ownership carries over unchanged: a node's claimed source parts must all resolve to aliases
/// this request owns. A part that resolves into the halo is not repaired or partially accepted - the
/// whole node is rejected, because a halo occurrence read as context must never become a request's
/// own claim.
/// </para>
/// </summary>
public static class SemanticRelationGraphBinder
{
    public static BoundSemanticRelationGraph Bind(
        IReadOnlyList<SemanticSourceAtom> atoms,
        IReadOnlySet<string> ownedAliases,
        SemanticRelationGraphProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        ArgumentNullException.ThrowIfNull(ownedAliases);
        ArgumentNullException.ThrowIfNull(proposal);

        var acceptedNodes = new List<BoundSemanticGraphNode>();
        var rejectedNodes = new List<RejectedSemanticGraphNode>();
        var acceptedNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in proposal.Nodes)
        {
            if (!seenNodeIds.Add(node.NodeId))
            {
                rejectedNodes.Add(new(node.NodeId, SemanticGraphNodeRejectionReason.DuplicateNodeId,
                    "a later node in this reply declared the same nodeId"));
                continue;
            }

            var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, node.SourceParts);
            if (!canonical.IsCanonical)
            {
                rejectedNodes.Add(new(node.NodeId, SemanticGraphNodeRejectionReason.UnboundSource, canonical.Reason));
                continue;
            }

            var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts));
            if (!bound.IsBound)
            {
                rejectedNodes.Add(new(node.NodeId, SemanticGraphNodeRejectionReason.UnboundSource, bound.Reason));
                continue;
            }

            if (bound.Parts.Any(part => !ownedAliases.Contains(part.Alias)))
            {
                rejectedNodes.Add(new(node.NodeId, SemanticGraphNodeRejectionReason.OwnershipViolation,
                    "one or more parts name an alias outside this request's owned segment"));
                continue;
            }

            acceptedNodes.Add(new BoundSemanticGraphNode(node.NodeId, node.Function, bound.Parts));
            acceptedNodeIds.Add(node.NodeId);
        }

        var acceptedRelations = new List<BoundSemanticGraphRelation>();
        var rejectedRelations = new List<RejectedSemanticGraphRelation>();
        var seenRelations = new HashSet<(string Type, string From, string To)>();
        var deduplicated = 0;
        var parentOfEdges = new List<(string From, string To)>();

        foreach (var relation in proposal.Relations)
        {
            if (string.Equals(relation.FromNodeId, relation.ToNodeId, StringComparison.Ordinal))
            {
                rejectedRelations.Add(new(relation, SemanticGraphRelationRejectionReason.SelfRelation));
                continue;
            }
            if (!seenNodeIds.Contains(relation.FromNodeId) || !seenNodeIds.Contains(relation.ToNodeId))
            {
                rejectedRelations.Add(new(relation, SemanticGraphRelationRejectionReason.UnknownEndpoint));
                continue;
            }
            if (!acceptedNodeIds.Contains(relation.FromNodeId) || !acceptedNodeIds.Contains(relation.ToNodeId))
            {
                rejectedRelations.Add(new(relation, SemanticGraphRelationRejectionReason.EndpointNodeRejected));
                continue;
            }

            var key = (relation.Type, relation.FromNodeId, relation.ToNodeId);
            if (!seenRelations.Add(key))
            {
                deduplicated++;
                continue;
            }

            if (string.Equals(relation.Type, SemanticRelationTypes.ParentOf, StringComparison.Ordinal))
            {
                if (SemanticRelationGraphCycles.WouldFormCycle(parentOfEdges, relation.FromNodeId, relation.ToNodeId))
                {
                    rejectedRelations.Add(new(relation, SemanticGraphRelationRejectionReason.CycleWouldForm));
                    continue;
                }
                parentOfEdges.Add((relation.FromNodeId, relation.ToNodeId));
            }

            acceptedRelations.Add(new BoundSemanticGraphRelation(relation.Type, relation.FromNodeId, relation.ToNodeId));
        }

        return new BoundSemanticRelationGraph(
            acceptedNodes, acceptedRelations, rejectedNodes, rejectedRelations, deduplicated);
    }
}

/// <summary>
/// Cycle detection shared by the per-reply binder and the document-level merger, so "does this edge
/// close a cycle" is answered by exactly one algorithm at every scope it is asked at.
/// </summary>
internal static class SemanticRelationGraphCycles
{
    /// <summary>
    /// True when adding <paramref name="from"/> -&gt; <paramref name="to"/> to <paramref name="edges"/>
    /// would create a directed cycle. Checked before the edge is accepted, so a cycle is refused as
    /// one relation - never accepted and then silently broken elsewhere.
    /// </summary>
    public static bool WouldFormCycle(IReadOnlyList<(string From, string To)> edges, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return true;

        // A cycle would close iff `from` is already reachable FROM `to` (adding to->...->from->to).
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (a, b) in edges)
        {
            if (!adjacency.TryGetValue(a, out var list))
                adjacency[a] = list = [];
            list.Add(b);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(to);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (string.Equals(current, from, StringComparison.Ordinal)) return true;
            if (!visited.Add(current)) continue;
            if (adjacency.TryGetValue(current, out var next))
                foreach (var successor in next) stack.Push(successor);
        }
        return false;
    }
}
