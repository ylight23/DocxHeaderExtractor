namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Explicit semantic disagreement discovered by a global resolver. The resolver supplies only
/// already-frozen alternatives; it never asks a reopener to invent a new proposal.
/// </summary>
public sealed record CanonicalSemanticGlobalConflict(
    string ConflictId,
    IReadOnlyList<string> OccurrenceIds,
    IReadOnlyList<CanonicalSemanticProposal> Alternatives,
    IReadOnlyList<string> StructuralEvidence,
    IReadOnlyList<string> LocalContext)
{
    public string ConflictKind { get; init; } = "GLOBAL_SEMANTIC_CONFLICT";
    public IReadOnlyList<string> RelationEvidence { get; init; } = [];
}

public sealed record CanonicalSemanticGlobalReopenResult(
    IReadOnlyList<CanonicalSemanticProposal> AcceptedAlternatives,
    IReadOnlyList<string> UnresolvedConflictIds,
    int ReopenCalls,
    int InvalidResponses);

/// <summary>Provider-independent bounded semantic reopen contract.</summary>
public static class CanonicalSemanticGlobalReopenCoordinator
{
    public const int DefaultMaxRounds = 2;

    public static async Task<CanonicalSemanticGlobalReopenResult> ResolveAsync(
        IReadOnlyList<CanonicalSemanticGlobalConflict> conflicts,
        IReadOnlyList<SemanticSourceAlias> aliases,
        ICanonicalSemanticAdjudicationModel? model,
        string requestId,
        int maxRounds = DefaultMaxRounds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(aliases);
        if (maxRounds < 1) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        if (conflicts.Count == 0 || model is null)
            return new([], conflicts.Select(item => item.ConflictId).ToArray(), 0, 0);

        var accepted = new List<CanonicalSemanticProposal>();
        var unresolved = conflicts.ToDictionary(item => item.ConflictId, StringComparer.Ordinal);
        var unresolvedIds = new HashSet<string>(StringComparer.Ordinal);
        var calls = 0;
        var invalid = 0;
        for (var round = 1; round <= maxRounds && unresolved.Count > 0; round++)
        {
            foreach (var conflict in unresolved.Values.ToArray())
            {
                var semanticConflict = new SemanticProposalConflict(
                    conflict.ConflictId, conflict.Alternatives, "GLOBAL_SEMANTIC_REOPEN");
                var adjudicationCase = SemanticConflictAdjudicator.CreateCase(
                    semanticConflict, aliases, conflict.LocalContext, conflict.StructuralEvidence);
                var response = await model.AdjudicateAsync(
                    adjudicationCase, $"{requestId}:global-reopen:{round}:{conflict.ConflictId}", cancellationToken);
                calls++;
                var validation = SemanticConflictAdjudicator.ValidateResponse(adjudicationCase, response);
                if (validation.IsValid && validation.AcceptedProposal is not null)
                {
                    accepted.Add(validation.AcceptedProposal);
                    unresolved.Remove(conflict.ConflictId);
                }
                else if (!validation.IsValid)
                {
                    invalid++;
                    unresolvedIds.Add(conflict.ConflictId);
                    unresolved.Remove(conflict.ConflictId);
                }
                else
                {
                    // UNRESOLVED is terminal for this bounded reopen. It is never silently
                    // converted into a deterministic semantic choice.
                    unresolvedIds.Add(conflict.ConflictId);
                    unresolved.Remove(conflict.ConflictId);
                }
            }
        }
        unresolvedIds.UnionWith(unresolved.Keys);
        return new(accepted, unresolvedIds.OrderBy(item => item, StringComparer.Ordinal).ToArray(), calls, invalid);
    }
}
