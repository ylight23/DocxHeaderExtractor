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
