using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7EvaluationUniverseTests
{
    private static readonly ReviewOccurrence[] Atoms = [new("A", 1, 10), new("B", 1, 5), new("C", 2, 7)];
    private static EvaluationScope Scope() => new(P7EvaluationUniverse.Version, "source", "universe", "snapshot", [1], P7EvaluationUniverse.CrossingPolicy);
    private static EvaluationAnnotation[] Ready() => [new("A", "ADJUDICATED", "ESTABLISHES_STRUCTURE", true, [new("A", 0, 10)]),
        new("B", "ADJUDICATED", "OTHER", false, []), new("C", "OUT_OF_EVALUATION_SCOPE", null, null, null)];
    private static IReadOnlyList<string> Gaps(EvaluationAnnotation[] a, EvaluationScope? s = null) =>
        P7EvaluationUniverse.ReadinessGaps(s ?? Scope(), "source", "universe", "snapshot", [1, 2], Atoms, a);

    [Fact] public void Fully_reviewed_page_subset_does_not_require_whole_corpus_gold() => Assert.Empty(Gaps(Ready()));
    [Fact] public void Draft_is_pending_inside_scope_unlabelled_outside()
    {
        var draft = P7EvaluationUniverse.Draft(Scope(), Atoms);
        Assert.Equal(new[] { "PENDING", "PENDING", "OUT_OF_EVALUATION_SCOPE" }, draft.Select(a => a.Status));
        Assert.All(draft, a => { Assert.Null(a.SemanticFunction); Assert.Null(a.HeadingMembership); Assert.Null(a.ExtentParts); });
        Assert.Contains("EVALUATION_ADJUDICATION_PENDING", Gaps(draft.ToArray()));
    }
    [Fact] public void Missing_unreviewed_occurrence_does_not_become_other() =>
        Assert.Contains("EXPLICIT_FULL_SOURCE_PARTITION_REQUIRED", Gaps(Ready().Take(2).ToArray()));
    [Fact] public void Pending_review_cannot_supply_negative_label()
    {
        var rows = Ready(); rows[1] = new("B", "PENDING", "OTHER", false, []);
        Assert.Contains("PENDING_NOT_NEGATIVE_OR_PARTIAL_GOLD", Gaps(rows));
    }
    [Fact] public void Out_of_scope_is_not_a_semantic_negative()
    {
        var rows = Ready(); rows[2] = new("C", "OUT_OF_EVALUATION_SCOPE", "OTHER", false, []);
        Assert.Contains("OUTSIDE_SCOPE_MUST_REMAIN_UNSCORED", Gaps(rows));
    }
    [Fact] public void Membership_function_and_extent_all_required()
    {
        foreach (var row in new[] { Ready()[0] with { SemanticFunction = null }, Ready()[0] with { HeadingMembership = null }, Ready()[0] with { ExtentParts = null } })
        {
            var rows = Ready(); rows[0] = row; Assert.Contains("ADJUDICATION_INCOMPLETE", Gaps(rows));
        }
    }
    [Fact] public void Invalid_or_partial_gold_cannot_close_page_scope()
    {
        var rows = Ready(); rows[0] = rows[0] with { ExtentParts = [new("C", 0, 7)] };
        Assert.Contains("GOLD_EXTENT_CROSSES_SCOPE_REQUIRES_SCOPE_EXPANSION", Gaps(rows));
        rows[0] = rows[0] with { ExtentParts = [new("A", 0, 11)] };
        Assert.Contains("EVALUATION_EXTENT_REFERENCE_INVALID", Gaps(rows));
    }
    [Fact] public void Prediction_starting_outside_but_entering_scope_is_kept_whole()
    {
        var o = P7EvaluationUniverse.ObservePrediction(Scope(), Atoms, ["C", "A"], Ready());
        Assert.Equal("CROSS_SCOPE_NOT_EVALUABLE_UNKNOWN_TRUTH", o.Classification); Assert.True(o.RetainDiagnostic); Assert.False(o.MayScoreExactBoundary);
        Assert.Equal(new[] { "A" }, o.TouchingScope); Assert.Equal(new[] { "C" }, o.OutsideScope);
    }
    [Fact] public void Prediction_starting_inside_but_leaving_scope_is_not_trimmed()
    {
        var o = P7EvaluationUniverse.ObservePrediction(Scope(), Atoms, ["A", "C"], Ready());
        Assert.Equal("CROSS_SCOPE_NOT_EVALUABLE_UNKNOWN_TRUTH", o.Classification); Assert.False(o.MayScoreExactBoundary);
    }
    [Fact] public void Pending_boundary_is_not_evaluable_but_diagnostic_is_retained()
    {
        var o = P7EvaluationUniverse.ObservePrediction(Scope(), Atoms, ["A", "C"], P7EvaluationUniverse.Draft(Scope(), Atoms));
        Assert.Equal("NOT_EVALUABLE_UNIVERSE_NOT_READY", o.Classification); Assert.False(o.MayScoreExactBoundary); Assert.True(o.RetainDiagnostic);
    }
    [Fact] public void Unknown_source_is_invalid_not_a_favorable_exclusion() =>
        Assert.Equal("INVALID_SOURCE_REFERENCE", P7EvaluationUniverse.ObservePrediction(Scope(), Atoms, ["FAKE"], Ready()).Classification);
    [Fact] public void Drop_crossing_policy_is_rejected() =>
        Assert.Contains("CROSS_SCOPE_POLICY_UNSAFE", Gaps(Ready(), Scope() with { BoundaryPolicy = "DROP_CROSSING" }));
    [Fact] public void Source_snapshot_drift_invalidates_review_scope() =>
        Assert.Contains("EVALUATION_SNAPSHOT_MISMATCH", Gaps(Ready(), Scope() with { SnapshotSha256 = "different" }));
    [Fact] public void Pending_unpredicted_source_blocks_universe_even_when_prediction_itself_reviewed()
    {
        var rows = Ready(); rows[1] = new("B", "PENDING", null, null, null);
        var observed = P7EvaluationUniverse.ObservePrediction(Scope(), Atoms, ["A"], rows);
        Assert.Equal("NOT_EVALUABLE_UNIVERSE_NOT_READY", observed.Classification); Assert.False(observed.MayScoreExactBoundary);
    }
    [Fact] public void Positive_membership_must_reference_own_source_in_extent()
    {
        var rows = Ready(); rows[0] = rows[0] with { ExtentParts = [new("B", 0, 5)] };
        Assert.Contains("MEMBERSHIP_NOT_IN_OWN_UNIT_EXTENT", Gaps(rows));
    }
}
