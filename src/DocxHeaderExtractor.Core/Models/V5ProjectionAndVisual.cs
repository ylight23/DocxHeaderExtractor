namespace DocxHeaderExtractor.Core.V5;

public sealed record ProjectionResult(
    string ProjectionName,
    object Payload,
    IReadOnlyList<string> ClaimIds,
    IReadOnlyList<string> EvidenceIds);

public interface IKnowledgeProjection
{
    string Name { get; }
    ProjectionResult Project(DocumentKnowledgeState state, DocumentTaskContract contract);
}

/// <summary>Task-specific output is produced here, after claims and relations are validated.</summary>
public sealed class ProjectionEngine
{
    private readonly IReadOnlyDictionary<string, IKnowledgeProjection> _projections;

    public ProjectionEngine(IEnumerable<IKnowledgeProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(projections);
        _projections = projections.ToDictionary(item => item.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<ProjectionResult> Project(
        DocumentKnowledgeState state,
        DocumentTaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        var results = new List<ProjectionResult>();
        foreach (var request in contract.Projections)
        {
            if (!_projections.TryGetValue(request.Name, out var projection))
            {
                if (request.Required) throw new InvalidOperationException($"projection-not-registered:{request.Name}");
                continue;
            }
            results.Add(projection.Project(state, contract));
        }
        return results;
    }
}

/// <summary>Creates a typed visual evidence request from an unresolved claim; it does not call a provider.</summary>
public static class TargetedVisualEscalation
{
    public static VisualEvidenceRequest CreateRequest(
        SemanticClaimProposal claim,
        UniversalEvidenceGraph graph,
        string question,
        int budget)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(graph);
        var aliases = claim.Subject.SourceParts.Select(part => part.SourceAlias).ToHashSet(StringComparer.Ordinal);
        var nodes = graph.Nodes.Where(node => aliases.Contains(node.SourceAlias)).ToArray();
        var page = nodes.Select(node => node.Anchor.Geometry?.Page).FirstOrDefault(value => value is not null);
        var geometry = nodes.Select(node => node.Anchor.Geometry).FirstOrDefault(value => value is not null);
        return new VisualEvidenceRequest(
            claim.ClaimId,
            question,
            nodes.Select(node => node.EvidenceId).ToArray(),
            page,
            geometry,
            Math.Max(0, budget));
    }
}
