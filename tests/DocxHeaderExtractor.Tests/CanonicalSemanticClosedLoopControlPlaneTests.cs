using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class CanonicalSemanticClosedLoopControlPlaneTests
{
    [Fact]
    public void Pipeline_withholds_source_invalid_semantic_output_before_binding()
    {
        var catalog = Catalog(("p1", "Actual heading"));

        var result = CanonicalSemanticPipeline.Run(
            catalog,
            [new CanonicalSemanticProposal("S0001", true, "Invented heading")],
            "hash");

        Assert.Empty(result.BoundHeadings);
        Assert.Contains(result.ContractIssues, issue => issue.Code == "NON_VERBATIM_TEXT");
        Assert.Empty(result.BindingObservations);
    }

    [Fact]
    public void Pipeline_enforces_segment_ownership_before_binding()
    {
        var catalog = Catalog(("p1", "Owned"), ("p2", "Visible overlap"));

        var result = CanonicalSemanticPipeline.Run(
            catalog,
            [new CanonicalSemanticProposal("S0002", true, "Visible overlap")],
            "hash",
            null,
            new HashSet<string>(["S0001"], StringComparer.Ordinal));

        Assert.Empty(result.BoundHeadings);
        Assert.Contains(result.ContractIssues, issue => issue.Code == "OUT_OF_OWNED_SEGMENT");
    }

    [Fact]
    public async Task Conflict_is_reopened_and_only_selected_frozen_alternative_reaches_binder()
    {
        var catalog = Catalog(("p1", "CHAPTER III"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var proposals = new[]
        {
            new CanonicalSemanticProposal(
                "S0001", true, null, SemanticRole: "ARTICLE",
                SelectionMode: CanonicalSemanticSelectionMode.WholeAlias),
            new CanonicalSemanticProposal(
                "S0001", true, null, SemanticRole: "CHAPTER",
                SelectionMode: CanonicalSemanticSelectionMode.WholeAlias),
        };
        var model = new SelectRoleAdjudicator("CHAPTER");

        var adjudicated = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            proposals, aliases, model);
        var pipeline = CanonicalSemanticPipeline.Run(
            catalog, adjudicated.BindingReadyProposals, "hash");

        Assert.Equal(1, adjudicated.ModelCalls);
        Assert.Single(adjudicated.Cases);
        Assert.Empty(adjudicated.UnresolvedCaseIds);
        var heading = Assert.Single(pipeline.BoundHeadings);
        Assert.Equal("CHAPTER", heading.SemanticRole);
        Assert.Equal("CHAPTER III", heading.Text);
    }

    [Fact]
    public async Task No_conflict_does_not_spend_an_adjudication_call()
    {
        var catalog = Catalog(("p1", "Heading"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var proposal = new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION");
        var model = new SelectRoleAdjudicator("SECTION");

        var result = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            [proposal], aliases, model);

        Assert.Equal(0, result.ModelCalls);
        Assert.Empty(result.Cases);
        Assert.Equal(proposal, Assert.Single(result.BindingReadyProposals));
    }

    [Fact]
    public async Task Unresolved_conflict_is_withheld_instead_of_silently_collapsed()
    {
        var catalog = Catalog(("p1", "Heading"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var proposals = new[]
        {
            new CanonicalSemanticProposal(
                "S0001", true, null, SemanticRole: "SECTION",
                SelectionMode: CanonicalSemanticSelectionMode.WholeAlias),
            new CanonicalSemanticProposal(
                "S0001", false, null, SemanticRole: "BODY",
                SelectionMode: CanonicalSemanticSelectionMode.WholeAlias),
        };

        var result = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            proposals, aliases, new UnresolvedAdjudicator());

        Assert.Equal(1, result.ModelCalls);
        Assert.Empty(result.BindingReadyProposals);
        Assert.Single(result.UnresolvedCaseIds);
    }

    private sealed class SelectRoleAdjudicator(string semanticRole) : ICanonicalSemanticAdjudicationModel
    {
        public Task<SemanticAdjudicationResponse> AdjudicateAsync(
            SemanticAdjudicationCase adjudicationCase,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            var selected = adjudicationCase.Alternatives.Single(item => item.SemanticRole == semanticRole);
            return Task.FromResult(new SemanticAdjudicationResponse(
                adjudicationCase.CaseId,
                SemanticAdjudicationDecision.Select,
                selected.AlternativeId));
        }
    }

    private sealed class UnresolvedAdjudicator : ICanonicalSemanticAdjudicationModel
    {
        public Task<SemanticAdjudicationResponse> AdjudicateAsync(
            SemanticAdjudicationCase adjudicationCase,
            string requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SemanticAdjudicationResponse(
                adjudicationCase.CaseId,
                SemanticAdjudicationDecision.Unresolved));
    }

    private static DocumentSourceCatalog Catalog(params (string Id, string Text)[] units) =>
        new(units.Select((unit, index) => new DocumentSourceUnit(
            unit.Id,
            index + 1,
            unit.Text,
            new SourceAnchor { SourceType = "test", ParagraphId = unit.Id },
            new StructuralSpan(0, unit.Text.Length))));
}
