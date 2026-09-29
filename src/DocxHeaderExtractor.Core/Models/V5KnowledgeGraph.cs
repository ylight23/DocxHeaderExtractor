using System.Collections.ObjectModel;

namespace DocxHeaderExtractor.Core.V5;

public sealed record KnowledgeValidationIssue(string Code, string? ClaimId, string Message);

public sealed class DocumentKnowledgeState
{
    public DocumentKnowledgeState(
        UniversalEvidenceGraph evidenceGraph,
        IEnumerable<BoundSemanticClaim> claims,
        IEnumerable<KnowledgeValidationIssue>? conflicts = null,
        IEnumerable<ClaimProvenance>? evidenceProvenance = null,
        IReadOnlyDictionary<string, ProjectionResult>? projectionState = null)
    {
        EvidenceGraph = evidenceGraph ?? throw new ArgumentNullException(nameof(evidenceGraph));
        Claims = new ReadOnlyCollection<BoundSemanticClaim>((claims ?? throw new ArgumentNullException(nameof(claims))).ToArray());
        Conflicts = new ReadOnlyCollection<KnowledgeValidationIssue>((conflicts ?? []).ToArray());
        EvidenceProvenance = new ReadOnlyCollection<ClaimProvenance>((evidenceProvenance ?? []).ToArray());
        ProjectionState = projectionState ?? new Dictionary<string, ProjectionResult>(StringComparer.Ordinal);
    }

    public UniversalEvidenceGraph EvidenceGraph { get; }
    public IReadOnlyList<BoundSemanticClaim> Claims { get; }
    public IReadOnlyList<KnowledgeValidationIssue> Conflicts { get; }
    public IReadOnlyList<ClaimProvenance> EvidenceProvenance { get; }
    public IReadOnlyDictionary<string, ProjectionResult> ProjectionState { get; }
    public IReadOnlyList<BoundSemanticClaim> OpenClaims => Claims.Where(item => item.State == ClaimResolutionState.OPEN).ToArray();
    public IReadOnlyList<BoundSemanticClaim> ResolvedClaims => Claims.Where(item => item.State == ClaimResolutionState.RESOLVED).ToArray();
}

public static class KnowledgeGraphValidator
{
    public static IReadOnlyList<KnowledgeValidationIssue> Validate(
        DocumentKnowledgeState state,
        DocumentTaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        var predicates = contract.Predicates.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var relations = contract.Relations.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var issues = new List<KnowledgeValidationIssue>();
        var claimIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in state.Claims)
        {
            if (!claimIds.Add(claim.ClaimId)) issues.Add(new("DUPLICATE_CLAIM", claim.ClaimId, "claim id is repeated"));
            if (!predicates.Contains(claim.Predicate) && !relations.ContainsKey(claim.Predicate))
                issues.Add(new("ILLEGAL_PREDICATE", claim.ClaimId, "predicate is not allowed by the task contract"));
            if (relations.TryGetValue(claim.Predicate, out var relation))
            {
                if (claim.Object is null) issues.Add(new("DANGLING_ENDPOINT", claim.ClaimId, "relation has no grounded object"));
                if (relation.StructuralParent && claim.Object is not null && claim.Subject.Identity == claim.Object.Identity)
                    issues.Add(new("SELF_STRUCTURAL_PARENT", claim.ClaimId, "a structural parent cannot point to itself"));
            }
        }
        foreach (var relation in contract.Relations.Where(item => item.StructuralParent))
        {
            var edges = state.Claims.Where(item => item.Predicate == relation.Name && item.Object is not null)
                .Select(item => (From: item.Subject.Identity, To: item.Object!.Identity)).ToArray();
            if (HasCycle(edges)) issues.Add(new("STRUCTURAL_CYCLE", null, $"relation '{relation.Name}' contains a cycle"));
        }
        return issues;
    }

    private static bool HasCycle(IReadOnlyList<(string From, string To)> edges)
    {
        var outgoing = edges.GroupBy(edge => edge.From, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.To).ToArray(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string node)
        {
            if (!visiting.Add(node)) return true;
            if (visited.Contains(node)) { visiting.Remove(node); return false; }
            if (outgoing.TryGetValue(node, out var next) && next.Any(Visit)) return true;
            visiting.Remove(node);
            visited.Add(node);
            return false;
        }
        return outgoing.Keys.Any(Visit);
    }
}
