namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Provider boundary for semantic reasoning #2. The adjudicator may only select one frozen
/// alternative or remain unresolved; it cannot create source text, coordinates, or a new proposal.
/// </summary>
public interface ICanonicalSemanticAdjudicationModel
{
    Task<SemanticAdjudicationResponse> AdjudicateAsync(
        SemanticAdjudicationCase adjudicationCase,
        string requestId,
        CancellationToken cancellationToken = default);
}

public sealed record CanonicalSemanticAdjudicationResult(
    IReadOnlyList<CanonicalSemanticProposal> BindingReadyProposals,
    IReadOnlyList<SemanticAdjudicationCase> Cases,
    IReadOnlyList<string> UnresolvedCaseIds,
    int ModelCalls)
{
    public bool HasUnresolvedCases => UnresolvedCaseIds.Count > 0;
}

/// <summary>
/// Harness-owned semantic adjudication loop. It detects disagreements deterministically, asks a
/// semantic reasoner only for genuine conflicts, validates the response, and returns only frozen
/// source-backed alternatives to the binder. No Gold, style rule, or numeric coordinate can enter
/// this boundary.
/// </summary>
public static class CanonicalSemanticClosedLoopControlPlane
{
    public static async Task<CanonicalSemanticAdjudicationResult> AdjudicateAsync(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyList<SemanticSourceAlias> aliases,
        ICanonicalSemanticAdjudicationModel adjudicationModel,
        IReadOnlyList<string>? localContext = null,
        IReadOnlyList<string>? structuralEvidence = null,
        string requestId = "canonical-semantic-adjudication",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(adjudicationModel);

        var normalization = SemanticConflictNormalizer.Normalize(proposals, aliases);
        var accepted = normalization.NormalizedProposals.ToList();
        var cases = new List<SemanticAdjudicationCase>();
        var unresolved = new List<string>();
        var calls = 0;

        foreach (var conflict in normalization.Conflicts)
        {
            var adjudicationCase = SemanticConflictAdjudicator.CreateCase(
                conflict, aliases, localContext, structuralEvidence);
            cases.Add(adjudicationCase);
            var response = await adjudicationModel.AdjudicateAsync(
                adjudicationCase, $"{requestId}:{adjudicationCase.CaseId}", cancellationToken);
            calls++;
            AcceptOrWithhold(adjudicationCase, response, accepted, unresolved);
        }

        foreach (var conflict in normalization.AttributeConflicts)
        {
            var adjudicationCase = SemanticConflictAdjudicator.CreateCase(
                conflict, aliases, localContext, structuralEvidence);
            cases.Add(adjudicationCase);
            var response = await adjudicationModel.AdjudicateAsync(
                adjudicationCase, $"{requestId}:{adjudicationCase.CaseId}", cancellationToken);
            calls++;
            AcceptOrWithhold(adjudicationCase, response, accepted, unresolved);
        }

        var byAlias = aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        var ordered = accepted
            .OrderBy(item => SourceOrder(item, byAlias))
            .ThenBy(item => item.SourceAlias, StringComparer.Ordinal)
            .ToArray();

        return new(ordered, cases, unresolved, calls);
    }

    private static void AcceptOrWithhold(
        SemanticAdjudicationCase adjudicationCase,
        SemanticAdjudicationResponse response,
        ICollection<CanonicalSemanticProposal> accepted,
        ICollection<string> unresolved)
    {
        var decision = SemanticConflictAdjudicator.ValidateResponse(adjudicationCase, response);
        if (!decision.IsValid)
            throw new InvalidOperationException(
                $"INVALID_SEMANTIC_ADJUDICATION:{adjudicationCase.CaseId}:{string.Join(',', decision.Errors)}");

        if (decision.AcceptedProposal is not null)
            accepted.Add(decision.AcceptedProposal);
        else
            unresolved.Add(adjudicationCase.CaseId);
    }

    private static int SourceOrder(
        CanonicalSemanticProposal proposal,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases)
    {
        var names = proposal.SourceAliases is { Count: > 0 }
            ? proposal.SourceAliases
            : [proposal.SourceAlias];
        return names
            .Select(name => aliases.TryGetValue(name, out var alias) ? alias.SourceOrdinal : int.MaxValue)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
    }
}
