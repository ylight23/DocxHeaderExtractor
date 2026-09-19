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
    IReadOnlyList<string> InvalidCaseIds,
    int ModelCalls)
{
    public bool HasUnresolvedCases => UnresolvedCaseIds.Count > 0;

    /// <summary>
    /// Two different failures, deliberately not merged. UNRESOLVED is an adjudicator that answered
    /// within the contract and could not decide; INVALID is one that broke the contract. Both
    /// withhold the occurrence, but a census that cannot tell them apart cannot say whether the
    /// reasoner is uncertain or malfunctioning.
    /// </summary>
    public bool HasInvalidResponses => InvalidCaseIds.Count > 0;
}

/// <summary>
/// Harness-owned semantic adjudication loop. It detects disagreements deterministically, asks a
/// semantic reasoner only for genuine conflicts, validates the response, and returns only frozen
/// source-backed alternatives to the binder. No Gold, style rule, or numeric coordinate can enter
/// this boundary.
/// </summary>
public static class CanonicalSemanticClosedLoopControlPlane
{
    /// <summary>
    /// Adjudicates conflicts that normalization already found.
    /// <para>
    /// It takes the normalization result rather than raw proposals on purpose: the production path
    /// normalizes once, after contract validation has filtered, and a second normalization here
    /// would run over a different input set. Two implementations that normalize different sets are
    /// not interchangeable however similar their code looks.
    /// </para>
    /// <para>
    /// An adjudicator that breaks its response contract costs its own case and nothing else. It
    /// does not throw: one malformed reply must not end a document, which is the same containment
    /// the model reply boundary holds.
    /// </para>
    /// </summary>
    public static async Task<CanonicalSemanticAdjudicationResult> AdjudicateAsync(
        SemanticConflictNormalizationResult normalization,
        IReadOnlyList<SemanticSourceAlias> aliases,
        ICanonicalSemanticAdjudicationModel adjudicationModel,
        IReadOnlyList<string>? localContext = null,
        IReadOnlyList<string>? structuralEvidence = null,
        string requestId = "canonical-semantic-adjudication",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalization);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(adjudicationModel);

        // Append order, not source order. Sorting here would change which proposal reaches the
        // binder first, and that is a behaviour change with its own downstream questions, not part
        // of moving an algorithm to one owner.
        var accepted = normalization.BindingReadyProposals.ToList();
        var cases = new List<SemanticAdjudicationCase>();
        var unresolved = new List<string>();
        var invalid = new List<string>();
        var calls = 0;

        // Occurrence conflicts first, then attribute conflicts, in that order: it is the order the
        // production path used and it decides the order proposals reach the binder.
        var adjudicationCases = normalization.Conflicts
            .Select(conflict => SemanticConflictAdjudicator.CreateCase(
                conflict, aliases, localContext, structuralEvidence))
            .Concat(normalization.AttributeConflicts
                .Select(conflict => SemanticConflictAdjudicator.CreateCase(
                    conflict, aliases, localContext, structuralEvidence)));

        foreach (var adjudicationCase in adjudicationCases)
        {
            cases.Add(adjudicationCase);
            var response = await adjudicationModel.AdjudicateAsync(
                adjudicationCase, $"{requestId}:adjudication:{adjudicationCase.CaseId}", cancellationToken);
            calls++;

            var decision = SemanticConflictAdjudicator.ValidateResponse(adjudicationCase, response);
            if (!decision.IsValid)
                invalid.Add(adjudicationCase.CaseId);
            else if (decision.AcceptedProposal is not null)
                accepted.Add(decision.AcceptedProposal);
            else
                unresolved.Add(adjudicationCase.CaseId);
        }

        return new(accepted, cases, unresolved, invalid, calls);
    }
}
