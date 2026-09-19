using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// One adjudication owner, and the behaviour it had to preserve to become one.
/// <para>
/// The production entry point used to carry its own copy of this algorithm. Moving it into
/// CanonicalSemanticClosedLoopControlPlane is only safe if nothing observable moved with it, so
/// these pin the parts that would otherwise drift quietly: request-id shape, ordering, call
/// counting, what is withheld, and how a misbehaving adjudicator is contained.
/// </para>
/// <para>
/// Ordering is deliberately append order, not source order. The control plane arrived with a sort;
/// taking it would have changed which proposal reaches the binder first, and that is a separate
/// question from who owns the algorithm.
/// </para>
/// </summary>
public sealed class AdjudicationOwnerEquivalenceTests
{
    private static DocumentSourceCatalog Catalog(params (string Id, string Text)[] units) =>
        new(units.Select((unit, index) => new DocumentSourceUnit(
            unit.Id, index, unit.Text,
            new SourceAnchor { SourceType = "docx", ParagraphId = unit.Id, ParagraphIndex = index },
            new StructuralSpan(0, unit.Text.Length))));

    private static CanonicalSemanticProposal Whole(string alias, string role) =>
        new(alias, true, null, SemanticRole: role,
            SelectionMode: CanonicalSemanticSelectionMode.WholeAlias);

    private static (SemanticConflictNormalizationResult Normalization, IReadOnlyList<SemanticSourceAlias> Aliases)
        Normalized(DocumentSourceCatalog catalog, params CanonicalSemanticProposal[] proposals)
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        return (SemanticConflictNormalizer.Normalize(proposals, aliases), aliases);
    }

    [Fact]
    public async Task No_conflict_spends_no_call_and_returns_the_normalized_set_unchanged()
    {
        var catalog = Catalog(("p1", "Heading"));
        var (normalization, aliases) = Normalized(catalog,
            new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION"));
        var model = new RecordingAdjudicator(SemanticAdjudicationDecision.Select);

        var result = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            normalization, aliases, model);

        Assert.Equal(0, result.ModelCalls);
        Assert.Empty(model.RequestIds);
        Assert.Equal(normalization.BindingReadyProposals, result.BindingReadyProposals);
    }

    [Fact]
    public async Task One_conflict_selects_the_frozen_alternative_under_the_production_request_id()
    {
        var catalog = Catalog(("p1", "CHAPTER III"));
        var (normalization, aliases) = Normalized(catalog, Whole("S0001", "ARTICLE"), Whole("S0001", "CHAPTER"));
        var model = new RecordingAdjudicator(SemanticAdjudicationDecision.Select, selectRole: "CHAPTER");

        var result = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            normalization, aliases, model, requestId: "docx:DOC-1");

        Assert.Equal(1, result.ModelCalls);
        // The shape production logs and traces have always used. Dropping the middle segment would
        // silently break every correlation against an older run.
        var caseId = Assert.Single(result.Cases).CaseId;
        Assert.Equal($"docx:DOC-1:adjudication:{caseId}", Assert.Single(model.RequestIds));
        Assert.Equal("CHAPTER", Assert.Single(result.BindingReadyProposals).SemanticRole);
    }

    [Fact]
    public async Task An_attribute_conflict_is_adjudicated_the_same_way()
    {
        var catalog = Catalog(("p1", "Heading"));
        var (normalization, aliases) = Normalized(catalog,
            new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION"),
            new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "ARTICLE"));
        Assert.NotEmpty(normalization.Conflicts.Concat<object>(normalization.AttributeConflicts));
        var model = new RecordingAdjudicator(SemanticAdjudicationDecision.Select, selectRole: "SECTION");

        var result = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            normalization, aliases, model);

        Assert.Equal(1, result.ModelCalls);
        Assert.Empty(result.InvalidCaseIds);
    }

    [Fact]
    public async Task An_unresolved_case_withholds_the_occurrence_and_reports_its_id()
    {
        var catalog = Catalog(("p1", "Heading"));
        var (normalization, aliases) = Normalized(catalog, Whole("S0001", "SECTION"), Whole("S0001", "BODY"));

        var result = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            normalization, aliases, new RecordingAdjudicator(SemanticAdjudicationDecision.Unresolved));

        Assert.Empty(result.BindingReadyProposals);
        Assert.Single(result.UnresolvedCaseIds);
        Assert.Empty(result.InvalidCaseIds);
        // An adjudicator that answered within the contract and could not decide is not a
        // malfunctioning one, and the two must not be counted together.
        Assert.False(result.HasInvalidResponses);
        Assert.True(result.HasUnresolvedCases);
    }

    [Fact]
    public async Task An_invalid_response_costs_its_own_case_and_the_next_case_still_runs()
    {
        // The containment test that matters. The control plane used to throw here, which would end
        // a document over one bad reply - the opposite of what the model-reply boundary holds.
        var catalog = Catalog(("p1", "CHAPTER III"), ("p2", "SECTION IV"));
        var (normalization, aliases) = Normalized(catalog,
            Whole("S0001", "ARTICLE"), Whole("S0001", "CHAPTER"),
            Whole("S0002", "ARTICLE"), Whole("S0002", "SECTION"));
        // Two disagreements, one per occurrence. Whether normalization files them as occurrence or
        // attribute conflicts is its business; what matters here is that two cases get opened.
        Assert.Equal(2, normalization.Conflicts.Count + normalization.AttributeConflicts.Count);
        var model = new FirstCaseInvalidAdjudicator(selectRole: "SECTION");

        var result = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            normalization, aliases, model);

        Assert.Equal(2, result.ModelCalls);
        Assert.Single(result.InvalidCaseIds);
        // The second case survived the first one failing, and its selection reached the binder.
        Assert.Equal("SECTION", Assert.Single(result.BindingReadyProposals).SemanticRole);
    }

    [Fact]
    public async Task Accepted_alternatives_keep_append_order_rather_than_source_order()
    {
        // Pinned because the control plane arrived with a sort by source ordinal. Adopting it would
        // change what reaches the binder first; that is a behaviour change with its own downstream
        // questions, not part of moving an algorithm to one owner.
        var catalog = Catalog(("p1", "CHAPTER III"), ("p2", "SECTION IV"));
        var (normalization, aliases) = Normalized(catalog,
            Whole("S0002", "ARTICLE"), Whole("S0002", "SECTION"),
            Whole("S0001", "ARTICLE"), Whole("S0001", "CHAPTER"));
        var model = new RecordingAdjudicator(SemanticAdjudicationDecision.Select, selectRole: null);

        var result = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            normalization, aliases, model);

        // Cases are opened in normalization order, and acceptances append in that same order.
        Assert.Equal(
            result.Cases.Select(item => item.CaseId),
            model.RequestIds.Select(id => id[(id.LastIndexOf(':') + 1)..]));
    }

    [Fact]
    public async Task Local_context_and_structural_evidence_reach_the_adjudicator_unchanged()
    {
        var catalog = Catalog(("p1", "CHAPTER III"));
        var (normalization, aliases) = Normalized(catalog, Whole("S0001", "ARTICLE"), Whole("S0001", "CHAPTER"));
        var model = new RecordingAdjudicator(SemanticAdjudicationDecision.Select, selectRole: "CHAPTER");

        await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            normalization, aliases, model,
            localContext: ["local-1"], structuralEvidence: ["structural-1"]);

        var opened = Assert.Single(model.Cases);
        Assert.Contains("local-1", opened.LocalContext);
        Assert.Contains("structural-1", opened.StructuralEvidence);
    }

    [Fact]
    public void The_control_plane_does_not_normalize_a_second_time()
    {
        // Structural, not behavioural. Two normalizations over different input sets is exactly how
        // two implementations look interchangeable while adjudicating different things. The method
        // takes a normalization result, so there is no second pass to drift.
        var parameters = typeof(CanonicalSemanticClosedLoopControlPlane)
            .GetMethod(nameof(CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync))!
            .GetParameters();

        Assert.Equal(typeof(SemanticConflictNormalizationResult), parameters[0].ParameterType);
        Assert.DoesNotContain(parameters, parameter =>
            parameter.ParameterType == typeof(IReadOnlyList<CanonicalSemanticProposal>));
    }

    private sealed class RecordingAdjudicator(string decision, string? selectRole = null)
        : ICanonicalSemanticAdjudicationModel
    {
        public List<string> RequestIds { get; } = [];
        public List<SemanticAdjudicationCase> Cases { get; } = [];

        public Task<SemanticAdjudicationResponse> AdjudicateAsync(
            SemanticAdjudicationCase adjudicationCase, string requestId, CancellationToken cancellationToken = default)
        {
            RequestIds.Add(requestId);
            Cases.Add(adjudicationCase);
            if (decision == SemanticAdjudicationDecision.Unresolved)
                return Task.FromResult(new SemanticAdjudicationResponse(
                    adjudicationCase.CaseId, SemanticAdjudicationDecision.Unresolved));
            var selected = selectRole is null
                ? adjudicationCase.Alternatives[0]
                : adjudicationCase.Alternatives.First(item => item.SemanticRole == selectRole);
            return Task.FromResult(new SemanticAdjudicationResponse(
                adjudicationCase.CaseId, SemanticAdjudicationDecision.Select, selected.AlternativeId));
        }
    }

    private sealed class FirstCaseInvalidAdjudicator(string selectRole) : ICanonicalSemanticAdjudicationModel
    {
        private int _calls;

        public Task<SemanticAdjudicationResponse> AdjudicateAsync(
            SemanticAdjudicationCase adjudicationCase, string requestId, CancellationToken cancellationToken = default)
        {
            if (_calls++ == 0)
                // An alternative id that was never offered: a response outside the contract.
                return Task.FromResult(new SemanticAdjudicationResponse(
                    adjudicationCase.CaseId, SemanticAdjudicationDecision.Select, "not-an-offered-alternative"));
            var selected = adjudicationCase.Alternatives.First(item => item.SemanticRole == selectRole);
            return Task.FromResult(new SemanticAdjudicationResponse(
                adjudicationCase.CaseId, SemanticAdjudicationDecision.Select, selected.AlternativeId));
        }
    }
}
