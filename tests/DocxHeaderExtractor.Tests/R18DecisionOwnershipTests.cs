using DocxHeaderExtractor.Eval.R18;

namespace DocxHeaderExtractor.Tests;

public sealed class R18DecisionOwnershipTests
{
    [Fact]
    public void Marker_overrides_proposed_level_and_is_attributed_to_pipeline()
    {
        var observation = Observation() with
        {
            ProposedLevel = 1,
            ProposedLevelStatus = R18ObservationStatus.Observable,
            MarkerResolvedLevel = 2,
            MarkerResolvedLevelStatus = R18ObservationStatus.Observable,
            FinalLevel = 2,
            FinalLevelStatus = R18ObservationStatus.Observable,
        };

        Assert.Equal(R18OwnershipClass.LevelModelErrorCorrected,
            R18DecisionOwnershipAnalyzer.ClassifyLevel(observation));
    }

    [Fact]
    public void Markerless_model_level_remains_model_owned()
    {
        var observation = Observation() with
        {
            ProposedLevel = 2,
            ProposedLevelStatus = R18ObservationStatus.Observable,
            MarkerResolvedLevelStatus = R18ObservationStatus.NotObservable,
            FinalLevel = 2,
            FinalLevelStatus = R18ObservationStatus.Observable,
        };

        Assert.Equal(R18OwnershipClass.LevelModelOwned,
            R18DecisionOwnershipAnalyzer.ClassifyLevel(observation));
    }

    [Fact]
    public void Structural_resolver_correction_is_attributed_separately()
    {
        var observation = Observation() with
        {
            ProposedLevel = 1,
            ProposedLevelStatus = R18ObservationStatus.Observable,
            MarkerResolvedLevel = 2,
            MarkerResolvedLevelStatus = R18ObservationStatus.Observable,
            StructuralResolvedLevel = 3,
            StructuralResolvedLevelStatus = R18ObservationStatus.Observable,
            FinalLevel = 3,
            FinalLevelStatus = R18ObservationStatus.Observable,
        };

        Assert.Equal(R18OwnershipClass.LevelStructuralOwned,
            R18DecisionOwnershipAnalyzer.ClassifyLevel(observation));
    }

    [Fact]
    public void Parser_boundary_rejection_is_not_reported_as_model_owned_span()
    {
        var observation = Observation() with
        {
            ProposedSpan = new R18Span(1, 4),
            ProposedSpanStatus = R18ObservationStatus.Observable,
            ParserBoundaryStatus = R18ObservationStatus.Observable,
            FinalSpanStatus = R18ObservationStatus.NotObservable,
        };

        Assert.Equal(R18OwnershipClass.SpanRejectedByParserBoundary,
            R18DecisionOwnershipAnalyzer.ClassifySpan(observation));
    }

    [Fact]
    public void Role_error_survives_only_when_reference_proves_the_final_error()
    {
        var observation = Observation() with
        {
            WasModelCalled = true,
            ProposedRole = "TableHeader",
            ProposedRoleStatus = R18ObservationStatus.Observable,
            FinalRole = "HeadingTopic",
            FinalRoleStatus = R18ObservationStatus.Observable,
            Reference = new R18ReferenceOutcome
            {
                Authority = R18ReferenceAuthority.HumanKey,
                ExpectedRole = "Caption",
            },
        };

        Assert.Equal(R18OwnershipClass.RoleModelErrorSurvived,
            R18DecisionOwnershipAnalyzer.ClassifyRole(observation));
    }

    [Fact]
    public void Missing_stage_telemetry_stays_not_observable()
    {
        var observation = Observation() with
        {
            FinalLevel = 2,
            FinalLevelStatus = R18ObservationStatus.NotObservable,
            ProposedLevel = 1,
            ProposedLevelStatus = R18ObservationStatus.Observable,
        };

        Assert.Equal(R18OwnershipClass.NotObservable,
            R18DecisionOwnershipAnalyzer.ClassifyLevel(observation));
    }

    private static R18DecisionObservation Observation() => new()
    {
        SourceId = "source-1",
        FinalRoleStatus = R18ObservationStatus.NotObservable,
        ProposedRoleStatus = R18ObservationStatus.NotObservable,
        ProposedLevelStatus = R18ObservationStatus.NotObservable,
        ProposedParentStatus = R18ObservationStatus.NotObservable,
        ProposedSpanStatus = R18ObservationStatus.NotObservable,
        ParserBoundaryStatus = R18ObservationStatus.NotObservable,
        MarkerResolvedLevelStatus = R18ObservationStatus.NotObservable,
        MarkerResolvedParentStatus = R18ObservationStatus.NotObservable,
        StructuralResolvedLevelStatus = R18ObservationStatus.NotObservable,
        StructuralResolvedParentStatus = R18ObservationStatus.NotObservable,
        FinalSpanStatus = R18ObservationStatus.NotObservable,
        FinalLevelStatus = R18ObservationStatus.NotObservable,
        FinalParentStatus = R18ObservationStatus.NotObservable,
    };
}
