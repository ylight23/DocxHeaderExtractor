using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

// Synthetic fixtures only: no real PDF annotation or accuracy claims.
public sealed class P7PilotScorerTests
{
    private static readonly ReviewOccurrence[] Atoms = [new("A", 1, 10), new("B", 1, 5), new("C", 1, 7), new("D", 2, 8)];
    private static readonly ReviewedPart[] Gold = [new("A", 0, 10), new("B", 0, 5)];
    private static EvaluationScope Scope => new(P7EvaluationUniverse.Version, "s", "u", "snapshot", [1], P7EvaluationUniverse.CrossingPolicy);
    private static EvaluationAnnotation[] Labels => [new("A", "ADJUDICATED", "ESTABLISHES_STRUCTURE", true, Gold),
        new("B", "ADJUDICATED", "OTHER", true, Gold), new("C", "ADJUDICATED", "OTHER", false, []),
        new("D", "OUT_OF_EVALUATION_SCOPE", null, null, null)];
    private static PilotGoldUnit Unit => new("A", Gold, "C", false);
    private static PilotPrediction P(params ReviewedPart[] parts) => new("prediction", parts[0].Alias, parts);
    private static PilotScore Score(PilotPrediction[] predictions, PilotGoldUnit? unit = null, EvaluationAnnotation[]? labels = null) =>
        P7PilotScorer.Score(Scope, Atoms, labels ?? Labels, [unit ?? Unit], predictions);

    [Fact] public void Multipart_exact_scores_membership_and_boundary_separately()
    {
        var s = Score([P(Gold)]); Assert.True(s.EvaluableMembership);
        Assert.Equal(2, s.MembershipTP); Assert.Equal(0, s.MembershipFP); Assert.Equal(0, s.MembershipFN); Assert.Equal(1, s.Exact);
    }
    [Fact] public void Missing_predictions_count_false_negatives() => Assert.Equal(2, Score([]).MembershipFN);
    [Fact] public void Reviewed_nonmember_counts_false_positive_not_true_anchor_boundary()
    {
        var s = Score([P(new ReviewedPart("C", 0, 7))]); Assert.Equal(1, s.MembershipFP); Assert.Equal(2, s.MembershipFN);
        Assert.Equal("NON_HEADING_ANCHOR_MEMBERSHIP_DIAGNOSTIC", s.Boundaries.Single().Reason); Assert.Equal(0, s.Wrong);
    }
    [Fact] public void Internal_fn_and_underextent_remain_visible()
    {
        var s = Score([P(new ReviewedPart("A", 0, 10))]); Assert.Equal(1, s.MembershipFN); Assert.Equal("UNDEREXTENT", s.Boundaries.Single().Outcome);
    }
    [Fact] public void Known_reviewed_exit_is_overextent_error()
    {
        var s = Score([P(new("A", 0, 10), new("B", 0, 5), new("C", 0, 7))]);
        Assert.Equal(1, s.MembershipFP); Assert.Equal("OVEREXTENT", s.Boundaries.Single().Outcome);
    }
    [Fact] public void Same_endpoint_with_wrong_span_is_not_exact() =>
        Assert.Equal("WRONG_PARTS", Score([P(new("A", 1, 9), new("B", 0, 5))]).Boundaries.Single().Outcome);
    [Fact] public void Crossing_unknown_truth_is_not_a_wrong_boundary_and_is_not_clipped()
    {
        var p = P(new("A", 0, 10), new("B", 0, 5), new("D", 0, 8)); var s = Score([p]);
        Assert.Equal(1, s.NotEvaluable); Assert.Equal(0, s.Wrong); Assert.Equal(0, s.Exact);
        Assert.Equal(p.Parts, s.Boundaries.Single().FullPrediction); Assert.Equal(2, s.MembershipTP);
    }
    [Fact] public void Crossing_not_evaluable_extent_does_not_exempt_known_membership_false_positive()
    {
        var p = P(new("A", 0, 10), new("B", 0, 5), new("C", 0, 7), new("D", 0, 8));
        var s = Score([p]);
        Assert.Equal(1, s.NotEvaluable); Assert.Equal(0, s.Wrong); Assert.Equal(1, s.MembershipFP);
        Assert.Equal(2, s.MembershipTP); Assert.Equal(0, s.MembershipFN);
        Assert.Equal(p.Parts, s.Boundaries.Single().FullPrediction);
    }
    [Fact] public void Open_gold_boundary_does_not_exempt_reviewed_membership_false_positive()
    {
        var s = Score([P(new("A", 0, 10), new("B", 0, 5), new("C", 0, 7))], Unit with { ReviewedFirstOutside = null });
        Assert.Equal("GOLD_UNIT_BOUNDARY_OPEN", s.Boundaries.Single().Reason);
        Assert.Equal(1, s.MembershipFP); Assert.Equal(2, s.MembershipTP); Assert.Equal(0, s.Exact);
    }
    [Fact] public void All_outside_prediction_is_reported_not_negative()
    {
        var s = Score([P(new ReviewedPart("D", 0, 8))]); Assert.Equal(1, s.NotEvaluable); Assert.Equal(0, s.MembershipFP);
        Assert.Equal("OUT_OF_EVALUATION_SCOPE", s.Boundaries.Single().Reason);
    }
    [Fact] public void Entirely_inside_prediction_still_requires_closed_unit()
    {
        var s = Score([P(Gold)], Unit with { ReviewedFirstOutside = null });
        Assert.Equal("GOLD_UNIT_BOUNDARY_OPEN", s.Boundaries.Single().Reason); Assert.Equal(0, s.Exact);
    }
    [Fact] public void Unreviewed_exit_cannot_be_used_as_closure_witness()
    {
        var s = Score([P(Gold)], Unit with { ReviewedFirstOutside = "D" });
        Assert.Contains("GOLD_EXIT_WITNESS_INVALID", s.ReadinessGaps); Assert.False(s.EvaluableMembership); Assert.Equal(0, s.Exact);
    }
    [Fact] public void Forged_document_end_cannot_close_partial_gold() =>
        Assert.Contains("DOCUMENT_END_ATTESTATION_INVALID", Score([P(Gold)], Unit with { ReviewedFirstOutside = null, DocumentEndAttested = true }).ReadinessGaps);
    [Fact] public void Pending_unpredicted_atom_blocks_complete_membership_metric()
    {
        var labels = Labels; labels[2] = new("C", "PENDING", null, null, null); var s = Score([P(Gold)], labels: labels);
        Assert.False(s.EvaluableMembership); Assert.Null(s.MembershipTP); Assert.Null(s.MembershipFN); Assert.Equal(1, s.NotEvaluable);
    }
    [Fact] public void Invalid_source_is_contract_failure_not_favorable_exclusion()
    {
        var s = Score([P(new ReviewedPart("FAKE", 0, 4))]); Assert.Equal(1, s.ContractInvalid); Assert.Null(s.MembershipTP); Assert.Equal(0, s.NotEvaluable);
    }
    [Fact] public void Invalid_span_is_contract_failure() => Assert.Equal(1, Score([P(new ReviewedPart("A", 0, 11))]).ContractInvalid);
    [Fact] public void Reordered_source_is_contract_failure() => Assert.Equal(1, Score([P(new("B", 0, 5), new("A", 0, 10))]).ContractInvalid);
    [Fact] public void Overlap_and_duplicate_anchors_observed_without_pruning()
    {
        var s = Score([P(Gold), new("second", "A", Gold)]); Assert.Equal(2, s.Exact); Assert.Equal(1, s.DuplicateAnchors);
        Assert.Equal(1, s.OverlappingPairs); Assert.Equal(2, s.Boundaries.Count); Assert.Equal(2, s.MembershipTP);
    }
    [Fact] public void Duplicate_prediction_ids_block_scores()
    {
        var s = Score([P(Gold), P(Gold)]); Assert.Contains("DUPLICATE_PREDICTION_ID", s.ReadinessGaps); Assert.Equal(0, s.Exact);
    }
    [Fact] public void Other_function_does_not_force_continuation_member_negative()
    {
        var s = Score([P(Gold)]); Assert.Equal("OTHER", Labels[1].SemanticFunction); Assert.Equal(1, s.Exact);
    }
    [Fact] public void Closed_terminal_gold_unit_can_score_exact()
    {
        var atoms = Atoms.Take(2).ToArray(); var labels = Labels.Take(2).ToArray();
        var s = P7PilotScorer.Score(Scope, atoms, labels, [Unit with { ReviewedFirstOutside = null, DocumentEndAttested = true }], [P(Gold)]);
        Assert.Equal(1, s.Exact); Assert.Empty(s.ReadinessGaps);
    }
    [Fact] public void Gold_parts_inconsistent_with_labels_fail_closed() =>
        Assert.Contains("GOLD_UNIT_ANNOTATIONS_INCONSISTENT", Score([P(Gold)], Unit with { Parts = [new("A", 0, 10)] }).ReadinessGaps);

    [Fact] public void Incomplete_gold_unit_coverage_cannot_hide_unpredicted_units()
    {
        var s = P7PilotScorer.Score(Scope, Atoms, Labels, [], [P(Gold)]);
        Assert.Contains("GOLD_UNIT_COVERAGE_INCOMPLETE", s.ReadinessGaps); Assert.Null(s.MembershipFN); Assert.Equal(0, s.Exact);
    }

    [Fact] public void Unknown_outside_anchor_entering_scope_keeps_prediction_and_known_membership()
    {
        var scope = Scope with { Pages = [2] }; ReviewedPart[] d = [new("D", 0, 8)];
        EvaluationAnnotation[] labels = [new("A", "OUT_OF_EVALUATION_SCOPE", null, null, null),
            new("B", "OUT_OF_EVALUATION_SCOPE", null, null, null), new("C", "OUT_OF_EVALUATION_SCOPE", null, null, null),
            new("D", "ADJUDICATED", "ESTABLISHES_STRUCTURE", true, d)];
        var p = P(new("C", 0, 7), new("D", 0, 8));
        var s = P7PilotScorer.Score(scope, Atoms, labels, [new("D", d, null, true)], [p]);
        Assert.Equal(1, s.MembershipTP); Assert.Equal(1, s.NotEvaluable); Assert.Equal(p.Parts, s.Boundaries.Single().FullPrediction);
    }
}
