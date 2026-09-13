using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticConflictAdjudicatorTests
{
    [Fact]
    public void Create_case_uses_physical_source_and_parser_owned_context()
    {
        var conflict = Conflict();
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(
            conflict,
            Aliases(),
            ["caller-local-context"],
            ["caller-structural-evidence"]);

        Assert.StartsWith("SAC-", adjudicationCase.CaseId, StringComparison.Ordinal);
        Assert.Equal(conflict.PhysicalSourceIdentity, adjudicationCase.PhysicalSourceIdentity);
        Assert.Equal(["S0002"], adjudicationCase.SourceEvidence.Select(item => item.SourceAlias));
        Assert.Equal("Chương III", adjudicationCase.SourceEvidence[0].Text);
        Assert.Contains("caller-local-context", adjudicationCase.LocalContext);
        Assert.Contains("[S0002] Chương III", adjudicationCase.LocalContext);
        Assert.Contains("S0002.paragraphId=body[1]/p[250]", adjudicationCase.StructuralEvidence);
        Assert.Equal("Resolve the semantic disagreement or return unresolved.", adjudicationCase.Task);
    }

    [Fact]
    public void Adjudication_case_contract_does_not_expose_text_or_coordinates_in_alternatives()
    {
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(Conflict(), Aliases());
        var json = JsonSerializer.Serialize(adjudicationCase);
        var schema = JsonSerializer.Serialize(SemanticAdjudicationContract.Schema());

        Assert.DoesNotContain("verbatimText", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verbatimText", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("end", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("level", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("level", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Case_and_alternative_ids_are_deterministic_regardless_of_input_order()
    {
        var conflict = Conflict();
        var reversed = conflict with { Alternatives = conflict.Alternatives.Reverse().ToArray() };

        var first = SemanticConflictAdjudicator.CreateCase(conflict, Aliases());
        var second = SemanticConflictAdjudicator.CreateCase(reversed, Aliases());

        Assert.Equal(first.CaseId, second.CaseId);
        Assert.Equal(JsonSerializer.Serialize(first.Alternatives),
            JsonSerializer.Serialize(second.Alternatives));
        Assert.Equal(JsonSerializer.Serialize(first.SourceEvidence),
            JsonSerializer.Serialize(second.SourceEvidence));
    }

    [Fact]
    public void Resolved_response_must_select_an_existing_frozen_alternative()
    {
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(Conflict(), Aliases());
        var selected = adjudicationCase.Alternatives.Single(item => item.SemanticRole == "CHAPTER");

        var result = SemanticConflictAdjudicator.ValidateResponse(
            adjudicationCase,
            new SemanticAdjudicationResponse(
                adjudicationCase.CaseId,
                SemanticAdjudicationDecision.Resolved,
                selected.AlternativeId));

        Assert.True(result.IsValid);
        Assert.Equal(SemanticAdjudicationDecision.Resolved, result.Status);
        Assert.Equal(selected.OriginalProposal, result.AcceptedProposal);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Unresolved_response_is_valid_without_selecting_a_winner()
    {
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(Conflict(), Aliases());

        var result = SemanticConflictAdjudicator.ValidateResponse(
            adjudicationCase,
            new SemanticAdjudicationResponse(
                adjudicationCase.CaseId,
                SemanticAdjudicationDecision.Unresolved));

        Assert.True(result.IsValid);
        Assert.Equal(SemanticAdjudicationDecision.Unresolved, result.Status);
        Assert.Null(result.AcceptedProposal);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Unknown_selection_and_case_mismatch_fail_closed()
    {
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(Conflict(), Aliases());

        var result = SemanticConflictAdjudicator.ValidateResponse(
            adjudicationCase,
            new SemanticAdjudicationResponse("wrong-case", SemanticAdjudicationDecision.Resolved, "A9999"));

        Assert.False(result.IsValid);
        Assert.Contains("CASE_ID_MISMATCH", result.Errors);
        Assert.Contains("UNKNOWN_SELECTED_ALTERNATIVE", result.Errors);
        Assert.Null(result.AcceptedProposal);
    }

    [Fact]
    public void Unresolved_response_with_a_selection_is_rejected()
    {
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(Conflict(), Aliases());
        var selected = adjudicationCase.Alternatives[0].AlternativeId;

        var result = SemanticConflictAdjudicator.ValidateResponse(
            adjudicationCase,
            new SemanticAdjudicationResponse(
                adjudicationCase.CaseId,
                SemanticAdjudicationDecision.Unresolved,
                selected));

        Assert.False(result.IsValid);
        Assert.Contains("UNRESOLVED_HAS_SELECTION", result.Errors);
        Assert.Null(result.AcceptedProposal);
    }

    [Fact]
    public void Adjudicator_does_not_invent_or_rewrite_a_proposal()
    {
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(Conflict(), Aliases());
        var selected = adjudicationCase.Alternatives[0];

        var result = SemanticConflictAdjudicator.ValidateResponse(
            adjudicationCase,
            new SemanticAdjudicationResponse(
                adjudicationCase.CaseId,
                SemanticAdjudicationDecision.Resolved,
                selected.AlternativeId));

        Assert.True(result.IsValid);
        Assert.Same(selected.OriginalProposal, result.AcceptedProposal);
        Assert.Equal("S0002", result.AcceptedProposal!.SourceAlias);
        Assert.Null(result.AcceptedProposal.VerbatimText);
    }

    [Fact]
    public void Selected_existing_alternative_is_the_only_proposal_given_to_exact_binder()
    {
        var aliases = Aliases();
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(Conflict(), aliases);
        var selected = adjudicationCase.Alternatives.Single(item => item.SemanticRole == "CHAPTER");
        var decision = SemanticConflictAdjudicator.ValidateResponse(
            adjudicationCase,
            new SemanticAdjudicationResponse(adjudicationCase.CaseId, SemanticAdjudicationDecision.Select, selected.AlternativeId));

        Assert.True(decision.IsValid);
        var bound = CanonicalSemanticExactBinder.Bind([decision.AcceptedProposal!], aliases, out var observations);

        Assert.Single(bound);
        Assert.Equal("CHAPTER", bound[0].SemanticRole);
        Assert.Equal("Chương III", bound[0].Text);
        Assert.Single(observations);
        Assert.Equal(CanonicalSemanticBindingStatus.Bound, observations[0].Status);
    }

    [Fact]
    public void Unresolved_decision_withholds_all_proposals_from_binder()
    {
        var aliases = Aliases();
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(Conflict(), aliases);
        var decision = SemanticConflictAdjudicator.ValidateResponse(
            adjudicationCase,
            new SemanticAdjudicationResponse(adjudicationCase.CaseId, SemanticAdjudicationDecision.Unresolved));

        Assert.True(decision.IsValid);
        var bound = CanonicalSemanticExactBinder.Bind(
            decision.AcceptedProposal is null ? [] : [decision.AcceptedProposal], aliases, out var observations);

        Assert.Empty(bound);
        Assert.Empty(observations);
    }

    [Fact]
    public void Invalid_adjudication_response_cannot_reach_binder()
    {
        var aliases = Aliases();
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(Conflict(), aliases);
        var decision = SemanticConflictAdjudicator.ValidateResponse(
            adjudicationCase,
            new SemanticAdjudicationResponse(adjudicationCase.CaseId, SemanticAdjudicationDecision.Select, "A9999"));

        Assert.False(decision.IsValid);
        var bound = CanonicalSemanticExactBinder.Bind(
            decision.AcceptedProposal is null ? [] : [decision.AcceptedProposal], aliases, out var observations);

        Assert.Empty(bound);
        Assert.Empty(observations);
    }

    private static SemanticProposalConflict Conflict() => new(
        "source:body[1]/p[250]:0:10",
        [
            new CanonicalSemanticProposal(
                "S0002", true, null,
                SemanticRole: "ARTICLE",
                SelectionMode: CanonicalSemanticSelectionMode.WholeAlias),
            new CanonicalSemanticProposal(
                "S0002", true, null,
                SemanticRole: "CHAPTER",
                SelectionMode: CanonicalSemanticSelectionMode.WholeAlias),
        ]);

    private static IReadOnlyList<SemanticSourceAlias> Aliases() =>
        SemanticSourceAliasCatalog.FromCatalog(new DocumentSourceCatalog([
            new DocumentSourceUnit(
                "body[1]/p[249]", 249, "Previous heading",
                new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = "body[1]/p[249]" },
                new StructuralSpan(0, "Previous heading".Length)),
            new DocumentSourceUnit(
                "body[1]/p[250]", 250, "Chương III",
                new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = "body[1]/p[250]" },
                new StructuralSpan(0, "Chương III".Length)),
            new DocumentSourceUnit(
                "body[1]/p[251]", 251, "Next heading",
                new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = "body[1]/p[251]" },
                new StructuralSpan(0, "Next heading".Length)),
        ]));
}
