using System.Text.Json;
using DocxHeaderExtractor.Eval.HarnessLift;

namespace DocxHeaderExtractor.Tests;

public sealed class HarnessLiftAccountingTests
{
    [Fact]
    public void Silver_is_never_official_or_supported()
    {
        var reference = Reference(HarnessReferenceAuthority.ModelAssistedSilver, HarnessCoverage.Partial, "headingExistence");

        Assert.False(HarnessLiftAccounting.IsOfficial(reference.Authority));
        Assert.False(HarnessLiftAccounting.Supports(reference, HarnessMetric.HeadingExistence));
    }

    [Fact]
    public void Aggregate_only_evidence_never_creates_occurrence_identity()
    {
        var evidence = new HarnessHistoricalEvidence
        {
            EvidenceId = "aggregate",
            SourceArtifact = "historical.json",
            EvidenceGranularity = HarnessEvidenceGranularity.AggregateOnly,
            EvidenceStrength = HarnessEvidenceStrength.Partial,
            Reason = "aggregate only",
        };

        Assert.Null(evidence.OccurrenceIdentity);
    }

    [Fact]
    public void Missing_reference_identity_remains_unknown()
    {
        Assert.Equal(HarnessKnowledge.Unknown, HarnessLiftAccounting.Knowledge(null, HarnessMetric.Level));
    }

    [Fact]
    public void Reference_provenance_is_retained()
    {
        var reference = Reference(HarnessReferenceAuthority.HumanGold, HarnessCoverage.Full, "headingExistence") with
        {
            SourcePath = "keys/001.key",
            Provenance = "reviewer-1; adjudication-v1",
            Notes = "exhaustive occurrence labels",
        };

        Assert.Equal("keys/001.key", reference.SourcePath);
        Assert.Contains("reviewer-1", reference.Provenance);
        Assert.Contains("exhaustive", reference.Notes);
    }

    [Fact]
    public void A_group_cannot_cross_evaluation_splits()
    {
        Assert.False(HarnessLiftAccounting.GroupsDoNotCrossSplits([
            ("g1", "DEV"),
            ("g1", "BLIND_HOLDOUT"),
        ]));
        Assert.True(HarnessLiftAccounting.GroupsDoNotCrossSplits([("g1", "DEV"), ("g2", "BLIND_HOLDOUT")]));
    }

    [Fact]
    public void Eligibility_is_field_specific()
    {
        var reference = Reference(HarnessReferenceAuthority.HumanKey, HarnessCoverage.Full, "level");

        Assert.Equal(HarnessKnowledge.Proven, HarnessLiftAccounting.Knowledge(reference, HarnessMetric.Level));
        Assert.Equal(HarnessKnowledge.NotApplicable, HarnessLiftAccounting.Knowledge(reference, HarnessMetric.Span));
    }

    [Fact]
    public void Conditional_denominator_excludes_non_exposed_observations()
    {
        var result = HarnessLiftAccounting.ConditionalAccuracy([
            (true, true),
            (true, false),
            (false, false),
        ]);

        Assert.Equal(2, result.Evaluated);
        Assert.Equal(1, result.Correct);
        Assert.Equal(0.5, result.Accuracy);
    }

    [Fact]
    public void Marker_recovery_is_attributed_after_model_error()
    {
        var stage = HarnessLiftAccounting.ChooseFirstLoss(
            sourceSeen: true, candidateSelected: true, modelCalled: true,
            modelCorrect: false, finalCorrect: true, validatorRejected: false,
            markerChanged: true, structuralChanged: false);

        Assert.Equal(HarnessLossStage.MarkerResolution, stage);
    }

    [Fact]
    public void Structural_recovery_is_attributed_after_model_error()
    {
        var stage = HarnessLiftAccounting.ChooseFirstLoss(
            sourceSeen: true, candidateSelected: true, modelCalled: true,
            modelCorrect: false, finalCorrect: true, validatorRejected: false,
            markerChanged: false, structuralChanged: true);

        Assert.Equal(HarnessLossStage.StructuralResolution, stage);
    }

    [Fact]
    public void Deterministic_stage_can_be_attributed_as_introducing_error()
    {
        Assert.Equal("INTRODUCED_BY_DETERMINISTIC_STAGE",
            HarnessLiftAccounting.ClassifyDeterministicStageEffect(true, false, true));
    }

    [Fact]
    public void Validator_rejection_of_wrong_proposal_is_distinct()
    {
        Assert.Equal("REJECTED_WRONG_PROPOSAL",
            HarnessLiftAccounting.ClassifyValidatorRejection(false, true));
    }

    [Fact]
    public void Validator_rejection_of_correct_proposal_is_distinct()
    {
        Assert.Equal("REJECTED_CORRECT_PROPOSAL",
            HarnessLiftAccounting.ClassifyValidatorRejection(true, true));
    }

    [Fact]
    public void First_loss_is_the_earliest_observed_stage()
    {
        var stage = HarnessLiftAccounting.ChooseFirstLoss(
            sourceSeen: false, candidateSelected: false, modelCalled: true,
            modelCorrect: false, finalCorrect: false, validatorRejected: true,
            markerChanged: true, structuralChanged: true);

        Assert.Equal(HarnessLossStage.SourceLoss, stage);
    }

    [Fact]
    public void Unavailable_repeat_statistic_is_not_coerced_to_zero()
    {
        var statistic = HarnessLiftAccounting.Summarize([]);

        Assert.Equal(0, statistic.Count);
        Assert.Null(statistic.Mean);
        Assert.Null(statistic.Stddev);
    }

    [Fact]
    public void Reference_join_happens_after_observed_run_keys_exist()
    {
        var joined = HarnessLiftAccounting.JoinReferenceIds(["p1", "p2"], ["p2", "p3"]);

        Assert.Equal(["p2"], joined.OrderBy(item => item).ToArray());
    }

    [Fact]
    public void Binary_metrics_use_valid_positive_and_negative_denominators()
    {
        var metrics = HarnessLiftAccounting.BinaryMetrics(8, 2, 1);

        Assert.Equal(0.8, metrics.Precision);
        Assert.Equal(8.0 / 9.0, metrics.Recall);
        Assert.Equal(2 * 0.8 * (8.0 / 9.0) / (0.8 + (8.0 / 9.0)), metrics.F1);
    }

    [Fact]
    public void Lift_is_null_when_the_population_is_not_measurable()
    {
        Assert.Null(HarnessLiftAccounting.Lift(1, 0, 0));
        Assert.Equal(0.2, HarnessLiftAccounting.Lift(7, 5, 10));
    }

    [Fact]
    public void Repeated_statistics_report_mean_min_max_and_population_stddev()
    {
        var statistic = HarnessLiftAccounting.Summarize([0.5, 0.75, 1.0]);

        Assert.Equal(3, statistic.Count);
        Assert.Equal(0.75, statistic.Mean);
        Assert.Equal(0.5, statistic.Min);
        Assert.Equal(1.0, statistic.Max);
        Assert.Equal(Math.Sqrt(0.125 / 3), statistic.Stddev);
    }

    [Fact]
    public void Current_trace_has_no_gold_or_expected_fields_for_model_input()
    {
        var trace = new HarnessCurrentTrace { DocumentId = "DOC-001", SourceId = "p1" };
        var json = JsonSerializer.Serialize(trace);

        Assert.DoesNotContain("gold", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expected", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deterministic_artifact_serialization_is_repeatable()
    {
        var value = new HarnessRepeatedStatistic(3, 0.75, 0.5, 1.0, Math.Sqrt(0.125 / 3));
        var first = JsonSerializer.Serialize(value);
        var second = JsonSerializer.Serialize(value);

        Assert.Equal(first, second);
    }

    private static HarnessReferenceRecord Reference(
        HarnessReferenceAuthority authority,
        HarnessCoverage coverage,
        params string[] metrics) => new()
        {
            ReferenceId = "reference",
            Authority = authority,
            SourcePath = "reference.key",
            Coverage = coverage,
            SupportedMetrics = metrics,
            Provenance = "test",
            Notes = "test",
        };
}
