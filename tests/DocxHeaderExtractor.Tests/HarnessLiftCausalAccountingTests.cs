using DocxHeaderExtractor.Eval.HarnessLift;

namespace DocxHeaderExtractor.Tests;

public sealed class HarnessLiftCausalAccountingTests
{
    [Fact]
    public void Deterministic_bypass_is_not_candidate_loss()
    {
        var result = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            deterministicRouteProven: true,
            deterministicCorrect: true,
            candidateRequired: false,
            candidateSelectedProven: false));

        Assert.Equal(HarnessHl3CandidateLossDisposition.DeterministicBypassCorrect, result);
    }

    [Fact]
    public void Deterministic_bypass_wrong_is_attributed_to_deterministic_stage()
    {
        var result = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            deterministicRouteProven: true,
            deterministicWrong: true,
            candidateRequired: false));

        Assert.Equal(HarnessHl3CandidateLossDisposition.DeterministicBypassWrong, result);
    }

    [Fact]
    public void Source_and_candidate_namespaces_are_not_assumed_equivalent()
    {
        var result = HarnessLiftCausalAccounting.Compare(
            "SOURCE_SOURCE_IDS", ["paragraph-17"],
            "CANDIDATE_BLOCK_IDS", ["block-b17"]);

        Assert.Equal(0, result.ExactIntersection);
        Assert.Equal(1, result.AOnly);
        Assert.Equal(1, result.BOnly);
    }

    [Fact]
    public void Explicit_source_candidate_intersection_is_the_only_proven_bridge()
    {
        var result = HarnessLiftCausalAccounting.Compare(
            "SOURCE_SOURCE_IDS", ["paragraph-17", "paragraph-18"],
            "CANDIDATE_BLOCK_IDS", ["paragraph-17"]);

        Assert.Equal(1, result.ExactIntersection);
        Assert.Equal(0.5, result.IntersectionRate);
    }

    [Fact]
    public void Unproven_representation_is_not_a_candidate_loss()
    {
        var result = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            representationProven: false,
            candidateRequired: true,
            candidateConstructedProven: false));

        Assert.Equal(HarnessHl3CandidateLossDisposition.RepresentationMismatch, result);
    }

    [Fact]
    public void First_loss_reports_unbridged_representation_before_candidate_stages()
    {
        var result = HarnessLiftCausalAccounting.ChooseFirstLoss(FirstLoss(
            representationProven: false,
            candidateRequired: true,
            candidateConstructedProven: false));

        Assert.Equal(HarnessHl3FirstLossStage.RepresentationNotBridged, result);
    }

    [Fact]
    public void Candidate_not_constructed_is_distinct_from_not_selected()
    {
        var notConstructed = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            candidateRequired: true,
            candidateConstructedProven: false,
            candidateSelectedProven: false));
        var notSelected = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            candidateRequired: true,
            candidateConstructedProven: true,
            candidateSelectedProven: false));

        Assert.Equal(HarnessHl3CandidateLossDisposition.TrueCandidateNotConstructed, notConstructed);
        Assert.Equal(HarnessHl3CandidateLossDisposition.TrueCandidateNotSelected, notSelected);
    }

    [Fact]
    public void Ranking_budget_loss_is_distinct_from_not_selected()
    {
        var result = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            candidateRequired: true,
            candidateConstructedProven: true,
            candidateSelectedProven: false,
            rankingBudgetProven: true));

        Assert.Equal(HarnessHl3CandidateLossDisposition.TrueRankingBudgetLoss, result);
    }

    [Fact]
    public void Document_model_calls_do_not_prove_occurrence_exposure()
    {
        var result = HarnessLiftCausalAccounting.ModelExposure(
            modelRunOccurredInDocument: true,
            retainedPerOccurrenceDecision: false);

        Assert.Equal(HarnessHl3ModelExposureStatus.ModelOccurrenceExposureUnknown, result);
    }

    [Fact]
    public void Retained_per_occurrence_decision_proves_exposure_separately()
    {
        var result = HarnessLiftCausalAccounting.ModelExposure(
            modelRunOccurredInDocument: true,
            retainedPerOccurrenceDecision: true);

        Assert.Equal(HarnessHl3ModelExposureStatus.ModelOccurrenceExposedProven, result);
    }

    [Fact]
    public void Missing_request_membership_remains_unobservable()
    {
        var result = HarnessLiftCausalAccounting.ModelExposure(
            modelRunOccurredInDocument: true,
            retainedPerOccurrenceDecision: false,
            modelNotApplicable: false);

        Assert.Equal(HarnessHl3ModelExposureStatus.ModelOccurrenceExposureUnknown, result);
    }

    [Fact]
    public void Model_proposal_wrong_is_not_invented_when_exposure_is_unknown()
    {
        var result = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            candidateRequired: true,
            candidateConstructedProven: true,
            candidateSelectedProven: true,
            modelExposureProven: false,
            modelProposalWrong: true));

        Assert.Equal(HarnessHl3CandidateLossDisposition.TraceNotObservable, result);
    }

    [Fact]
    public void Validation_rejection_is_only_attributed_after_wrong_proposal_is_proven()
    {
        var result = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            candidateRequired: true,
            candidateConstructedProven: true,
            candidateSelectedProven: true,
            modelExposureProven: true,
            modelProposalWrong: true,
            proposalValidationRejected: true));

        Assert.Equal(HarnessHl3CandidateLossDisposition.OtherProvenStage, result);
    }

    [Fact]
    public void Final_lineage_mismatch_is_distinct_from_unobservable_trace()
    {
        var result = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            candidateRequired: true,
            candidateConstructedProven: true,
            candidateSelectedProven: true,
            modelExposureProven: true,
            finalSourceLineageMismatch: true));

        Assert.Equal(HarnessHl3CandidateLossDisposition.FinalSourceLineageMismatch, result);
    }

    [Fact]
    public void Positive_only_population_cannot_claim_precision_or_f1()
    {
        var metrics = HarnessLiftAccounting.BinaryMetrics(0, 0, 0);

        Assert.Null(metrics.Precision);
        Assert.Null(metrics.F1);
        Assert.Null(HarnessLiftAccounting.Lift(8, 0, 0));
    }

    [Fact]
    public void Deterministic_automation_is_not_post_model_lift()
    {
        var deterministic = HarnessLiftCausalAccounting.ClassifyCandidateLoss(Input(
            deterministicRouteProven: true,
            deterministicCorrect: true));

        Assert.Equal(HarnessHl3CandidateLossDisposition.DeterministicBypassCorrect, deterministic);
        Assert.NotEqual(HarnessHl3CandidateLossDisposition.OtherProvenStage, deterministic);
    }

    [Fact]
    public void First_loss_requires_model_route_before_candidate_loss()
    {
        var result = HarnessLiftCausalAccounting.ChooseFirstLoss(FirstLoss(
            deterministicRouteProven: false,
            candidateRequired: false,
            candidateConstructedProven: false,
            candidateSelectedProven: false,
            modelExposureProven: false));

        Assert.Equal(HarnessHl3FirstLossStage.TraceNotObservable, result);
    }

    [Fact]
    public void Exact_namespace_comparison_has_no_fuzzy_join()
    {
        var result = HarnessLiftCausalAccounting.Compare(
            "SOURCE_SOURCE_IDS", ["body/p/17"],
            "FINAL_SOURCE_IDS", ["body/p/017"]);

        Assert.Equal(0, result.ExactIntersection);
    }

    [Fact]
    public void Unknown_final_lineage_is_not_counted_as_wrong()
    {
        Assert.False(HarnessLiftCausalAccounting.IsFinalLineageWrong("UNKNOWN"));
        Assert.False(HarnessLiftCausalAccounting.IsFinalLineageWrong("FINAL_ABSENT"));
        Assert.True(HarnessLiftCausalAccounting.IsFinalLineageWrong("FINAL_PRESENT_WRONG_FIELD"));
    }

    [Fact]
    public void No_model_run_is_not_applicable_rather_than_false_exposure()
    {
        var result = HarnessLiftCausalAccounting.ModelExposure(
            modelRunOccurredInDocument: false,
            retainedPerOccurrenceDecision: false);

        Assert.Equal(HarnessHl3ModelExposureStatus.ModelNotApplicable, result);
    }

    private static HarnessHl3LossClassificationInput Input(
        bool sourceProven = true,
        bool representationProven = true,
        bool deterministicRouteProven = false,
        bool deterministicCorrect = false,
        bool deterministicWrong = false,
        bool candidateRequired = false,
        bool candidateConstructedProven = false,
        bool candidateSelectedProven = false,
        bool rankingBudgetProven = false,
        bool modelExposureProven = false,
        bool modelProposalWrong = false,
        bool proposalValidationRejected = false,
        bool markerResolutionError = false,
        bool structuralResolutionError = false,
        bool finalSourceLineageMismatch = false,
        bool finalProjectionError = false) => new(
        sourceProven,
        representationProven,
        deterministicRouteProven,
        deterministicCorrect,
        deterministicWrong,
        candidateRequired,
        candidateConstructedProven,
        candidateSelectedProven,
        rankingBudgetProven,
        modelExposureProven,
        modelProposalWrong,
        proposalValidationRejected,
        markerResolutionError,
        structuralResolutionError,
        finalSourceLineageMismatch,
        finalProjectionError);

    private static HarnessHl3FirstLossInput FirstLoss(
        bool sourceProven = true,
        bool representationProven = true,
        bool deterministicRouteProven = false,
        bool deterministicCorrect = false,
        bool deterministicWrong = false,
        bool candidateRequired = false,
        bool candidateConstructedProven = false,
        bool candidateSelectedProven = false,
        bool rankingBudgetProven = false,
        bool modelExposureProven = false,
        bool modelProposalWrong = false,
        bool proposalValidationRejected = false,
        bool markerResolutionError = false,
        bool structuralResolutionError = false,
        bool finalProjectionError = false) => new(
        sourceProven,
        representationProven,
        deterministicRouteProven,
        deterministicCorrect,
        deterministicWrong,
        candidateRequired,
        candidateConstructedProven,
        candidateSelectedProven,
        rankingBudgetProven,
        modelExposureProven,
        modelProposalWrong,
        proposalValidationRejected,
        markerResolutionError,
        structuralResolutionError,
        finalProjectionError);
}
