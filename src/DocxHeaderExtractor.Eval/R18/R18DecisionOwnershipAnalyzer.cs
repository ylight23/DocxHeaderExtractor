using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.R18;

public static class R18DecisionOwnershipAnalyzer
{
    public static R18DecisionOwnershipReport Build(
        SourceDocument source,
        AuthorityPipelineExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(execution);

        var outline = execution.CompatibilityOutline;
        var audit = outline.RouteAudit;
        var observations = BuildObservations(source, execution).Select(AssignOwnership).ToArray();
        var firstLosses = observations.Select(AssignFirstLoss).ToArray();
        var firstLossSummary = BuildFirstLossSummary(firstLosses);
        var mode = outline.DocumentMode;
        return new R18DecisionOwnershipReport
        {
            DocumentId = source.DocumentId,
            SourceKind = source.SourceKind,
            ModeEvidence = mode is null ? null : new R18ModeEvidence(
                mode.Mode.ToString(), mode.Paragraphs, mode.StyledHeadings, mode.OutlineLevelRatio,
                mode.VietnameseAdminRatio, mode.TypedNumberRatio, mode.NumberingRatio,
                mode.LegalMarkerRatio, mode.FormatDiffers),
            Route = execution.Result.Provenance.Route,
            Observations = firstLosses,
            DisagreementMetrics = BuildDisagreementMetrics(firstLosses),
            FirstLossSummary = firstLossSummary,
            ProviderCalls = execution.Result.Provenance.ProviderCalls,
            ReferenceAuthorityObserved = firstLosses.Select(item => item.Reference.Authority)
                .Distinct().OrderBy(item => item).ToArray(),
            ReferenceBackedObservationCount = firstLosses.Count(item => item.Reference.IsComparable),
            Direction = firstLosses.Any(item => item.Reference.IsComparable)
                ? DeriveDirection(firstLosses)
                : "NOT_DECIDABLE_WITHOUT_REFERENCE",
        };
    }

    public static R18OwnershipClass ClassifyRole(R18DecisionObservation item)
    {
        if (item.FinalRoleStatus != R18ObservationStatus.Observable) return R18OwnershipClass.NotObservable;
        if (item.WasModelCalled == false) return R18OwnershipClass.RoleDeterministicAssigned;
        if (item.ProposedRoleStatus != R18ObservationStatus.Observable) return R18OwnershipClass.NotObservable;
        if (string.Equals(item.ProposedRole, item.FinalRole, StringComparison.Ordinal)) return R18OwnershipClass.RoleModelOwned;
        if (item.Reference.IsComparable && !string.Equals(item.Reference.ExpectedRole, item.FinalRole, StringComparison.Ordinal))
            return R18OwnershipClass.RoleModelErrorSurvived;
        if (item.Reference.IsComparable && string.Equals(item.Reference.ExpectedRole, item.FinalRole, StringComparison.Ordinal))
            return R18OwnershipClass.RoleModelErrorCorrected;
        if (item.ValidationStatus is "rejected" or "not-heading") return R18OwnershipClass.RoleModelRejected;
        return R18OwnershipClass.NotObservable;
    }

    public static R18OwnershipClass ClassifyLevel(R18DecisionObservation item)
    {
        if (item.FinalLevelStatus != R18ObservationStatus.Observable) return R18OwnershipClass.NotObservable;
        if (item.ProposedLevelStatus == R18ObservationStatus.Observable &&
            item.MarkerResolvedLevelStatus == R18ObservationStatus.Observable &&
            item.ProposedLevel != item.MarkerResolvedLevel)
        {
            if (item.FinalLevel == item.MarkerResolvedLevel)
                return item.Reference.IsComparable && item.Reference.ExpectedLevel != item.FinalLevel
                    ? R18OwnershipClass.LevelModelErrorSurvived
                    : R18OwnershipClass.LevelModelErrorCorrected;
            if (item.StructuralResolvedLevelStatus == R18ObservationStatus.Observable && item.FinalLevel == item.StructuralResolvedLevel)
                return R18OwnershipClass.LevelStructuralOwned;
        }
        if (item.StructuralResolvedLevelStatus == R18ObservationStatus.Observable &&
            item.MarkerResolvedLevelStatus == R18ObservationStatus.Observable &&
            item.StructuralResolvedLevel != item.MarkerResolvedLevel && item.FinalLevel == item.StructuralResolvedLevel)
            return R18OwnershipClass.LevelStructuralOwned;
        if (item.MarkerResolvedLevelStatus == R18ObservationStatus.Observable && item.FinalLevel == item.MarkerResolvedLevel)
            return R18OwnershipClass.LevelMarkerOwned;
        if (item.ProposedLevelStatus == R18ObservationStatus.Observable && item.FinalLevel == item.ProposedLevel)
            return R18OwnershipClass.LevelModelOwned;
        return R18OwnershipClass.NotObservable;
    }

    public static R18OwnershipClass ClassifyParent(R18DecisionObservation item)
    {
        if (item.FinalParentStatus != R18ObservationStatus.Observable) return R18OwnershipClass.NotObservable;
        if (item.ProposedParentStatus == R18ObservationStatus.Observable && item.FinalParentId == item.ProposedParentId)
            return R18OwnershipClass.ParentModelOwned;
        if (item.MarkerResolvedParentStatus == R18ObservationStatus.Observable && item.FinalParentId == item.MarkerResolvedParentId)
            return R18OwnershipClass.ParentMarkerOwned;
        if (item.StructuralResolvedParentStatus == R18ObservationStatus.Observable && item.FinalParentId == item.StructuralResolvedParentId)
            return R18OwnershipClass.ParentStructuralOwned;
        if (item.Reference.IsComparable && item.ProposedParentStatus == R18ObservationStatus.Observable &&
            item.Reference.ExpectedParentId == item.FinalParentId)
            return R18OwnershipClass.ParentModelErrorCorrected;
        if (item.Reference.IsComparable && item.ProposedParentStatus == R18ObservationStatus.Observable)
            return R18OwnershipClass.ParentModelErrorSurvived;
        return R18OwnershipClass.NotObservable;
    }

    public static R18OwnershipClass ClassifySpan(R18DecisionObservation item)
    {
        if (item.ParserBoundaryStatus == R18ObservationStatus.Observable &&
            item.ProposedSpanStatus == R18ObservationStatus.Observable &&
            item.FinalSpanStatus != R18ObservationStatus.Observable)
            return R18OwnershipClass.SpanRejectedByParserBoundary;
        if (item.ProposedSpanStatus == R18ObservationStatus.Observable &&
            item.FinalSpanStatus == R18ObservationStatus.Observable)
            return R18OwnershipClass.SpanModelProposed;
        if (item.FinalSpanStatus == R18ObservationStatus.Observable &&
            item.ParserBoundaryStatus == R18ObservationStatus.Observable)
            return R18OwnershipClass.SpanParserValidated;
        return R18OwnershipClass.NotObservable;
    }

    private static IEnumerable<R18DecisionObservation> BuildObservations(
        SourceDocument source,
        AuthorityPipelineExecutionResult execution)
    {
        var outline = execution.CompatibilityOutline;
        var audit = outline.RouteAudit;
        var sourceById = source.Paragraphs.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var candidateIds = (audit?.CandidateBlocks ?? [])
            .Select(item => item.Id)
            .Concat(audit?.CandidateStageTraces.Select(item => item.Id) ?? [])
            .Concat(audit?.BlockDecisions.Select(item => item.Id) ?? [])
            .Concat(outline.Headings.Select(item => item.SourceId ?? item.StableId ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);
        var stageById = (audit?.CandidateStageTraces ?? [])
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var decisionById = (audit?.BlockDecisions ?? [])
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var markerById = (audit?.HierarchyFacts ?? [])
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var structuralBySourceId = (audit?.ValidatedStructures ?? [])
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var finalElementsById = execution.Result.Structure.Elements
            .ToDictionary(element => element.Id, StringComparer.Ordinal);
        var finalElementBySourceId = execution.Result.Structure.Elements
            .SelectMany(element => element.Sources.Select(source => (source.SourceId, Element: element)))
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Element, StringComparer.Ordinal);
        var finalHeadingsBySourceId = outline.Headings
            .Select(item => (Key: item.SourceId ?? item.StableId, Heading: item))
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Heading, StringComparer.Ordinal);
        // Lane configuration is not proof that a provider ran. Raw completions are the only
        // source-level signal currently available for an observed model execution.
        bool? modelCalled = audit is null ? null : audit.RawAnalystResponses.Count > 0;
        var modelCompletionObserved = audit?.RawAnalystResponses.Count > 0;

        foreach (var id in source.Paragraphs.Select(item => item.SourceId)
                     .Concat(candidateIds).Distinct(StringComparer.Ordinal))
        {
            sourceById.TryGetValue(id, out var sourceParagraph);
            stageById.TryGetValue(id, out var stage);
            decisionById.TryGetValue(id, out var decision);
            markerById.TryGetValue(id, out var marker);
            structuralBySourceId.TryGetValue(id, out var structural);
            finalElementBySourceId.TryGetValue(id, out var finalElement);
            finalHeadingsBySourceId.TryGetValue(id, out var finalHeading);
            var proposedRole = decision?.Role;
            var finalSource = finalElement?.Sources.FirstOrDefault();
            var finalSpan = finalSource is null ? null : new R18Span(finalSource.Span.Start, finalSource.Span.End);
            var sourceSpan = sourceParagraph is null ? null : new R18Span(0, sourceParagraph.Text.Length);
            var proposedRoleStatus = decision is null ? R18ObservationStatus.NotObservable : R18ObservationStatus.Observable;
            var deterministicRoute = modelCalled == false;
            yield return new R18DecisionObservation
            {
                SourceId = id,
                SourceOrdinal = sourceParagraph?.SourceOrdinal,
                SourceSpan = sourceSpan,
                WasCandidate = audit is null ? null : candidateIds.Contains(id),
                CandidateReason = stage?.Reason,
                ProposedRole = proposedRole,
                ProposedRoleStatus = proposedRoleStatus,
                ProposedLevelStatus = R18ObservationStatus.NotObservable,
                ProposedParentStatus = R18ObservationStatus.NotObservable,
                ProposedSpanStatus = decision is null || modelCompletionObserved != true
                    ? R18ObservationStatus.NotObservable
                    : R18ObservationStatus.Observable,
                ValidationStatus = stage?.ValidationStatus,
                ValidationReason = stage?.Reason,
                ParserBoundaryStatus = stage is null || stage.SpanStatus == "not-applicable"
                    ? R18ObservationStatus.NotObservable
                    : R18ObservationStatus.Observable,
                MarkerResolvedLevel = marker?.ResolvedLevel,
                MarkerResolvedLevelStatus = marker is null || marker.MarkerFamily is null
                    ? R18ObservationStatus.NotObservable
                    : R18ObservationStatus.Observable,
                MarkerResolvedParentId = marker?.MarkerPrefixParentCandidate,
                MarkerResolvedParentStatus = marker is null || marker.MarkerFamily is null
                    ? R18ObservationStatus.NotObservable
                    : R18ObservationStatus.Observable,
                StructuralResolvedLevel = structural?.Level,
                StructuralResolvedLevelStatus = structural is null ? R18ObservationStatus.NotObservable : R18ObservationStatus.Observable,
                StructuralResolvedParentId = structural?.ParentId,
                StructuralResolvedParentStatus = structural is null ? R18ObservationStatus.NotObservable : R18ObservationStatus.Observable,
                FinalPresent = audit is null ? null : finalHeading is not null,
                FinalRole = finalHeading is null
                    ? null
                    : finalElement?.Role.ToString() ?? finalHeading.Source.ToString(),
                FinalRoleStatus = audit is null ? R18ObservationStatus.NotObservable : R18ObservationStatus.Observable,
                FinalSpan = finalSpan,
                FinalSpanStatus = finalHeading is null ? R18ObservationStatus.NotObservable : R18ObservationStatus.Observable,
                FinalLevel = finalElement?.Level,
                FinalLevelStatus = finalHeading is null ? R18ObservationStatus.NotObservable : R18ObservationStatus.Observable,
                FinalParentId = finalElement?.ParentId is { } parentId && finalElementsById.TryGetValue(parentId, out var parentElement)
                    ? parentElement.Sources.FirstOrDefault()?.SourceId
                    : null,
                FinalParentStatus = finalHeading is null ? R18ObservationStatus.NotObservable : R18ObservationStatus.Observable,
                // A deterministic route is explicit from the execution configuration. A model
                // route remains per-candidate ambiguous because RouteExecutionAudit does not map
                // every raw completion to a source id.
                WasModelCalled = deterministicRoute ? false : modelCalled,
            };
        }
    }

    private static R18DecisionObservation AssignOwnership(R18DecisionObservation item) => item with
    {
        RoleOwnership = ClassifyRole(item),
        LevelOwnership = ClassifyLevel(item),
        ParentOwnership = ClassifyParent(item),
        SpanOwnership = ClassifySpan(item),
    };

    private static R18DecisionObservation AssignFirstLoss(R18DecisionObservation item) => item with
    {
        FirstLoss = DetermineFirstLoss(item),
    };

    private static R18FirstLossStage? DetermineFirstLoss(R18DecisionObservation item)
    {
        if (!item.Reference.IsComparable) return null;
        var roleError = item.Reference.ExpectedRole is not null && item.FinalRoleStatus == R18ObservationStatus.Observable &&
            !string.Equals(item.Reference.ExpectedRole, item.FinalRole, StringComparison.Ordinal);
        var levelError = item.Reference.ExpectedLevel is not null && item.FinalLevelStatus == R18ObservationStatus.Observable &&
            item.Reference.ExpectedLevel != item.FinalLevel;
        var parentError = item.Reference.ExpectedParentId is not null && item.FinalParentStatus == R18ObservationStatus.Observable &&
            !string.Equals(item.Reference.ExpectedParentId, item.FinalParentId, StringComparison.Ordinal);
        var spanError = item.Reference.ExpectedSpan is not null && item.FinalSpanStatus == R18ObservationStatus.Observable &&
            item.Reference.ExpectedSpan != item.FinalSpan;
        if (!roleError && !levelError && !parentError && !spanError) return null;
        if (item.WasCandidate == false) return R18FirstLossStage.CandidateLoss;
        if (spanError && item.SpanOwnership == R18OwnershipClass.SpanRejectedByParserBoundary) return R18FirstLossStage.SpanError;
        if (roleError && item.RoleOwnership is R18OwnershipClass.RoleModelErrorSurvived) return R18FirstLossStage.RoleModelError;
        if (levelError && item.LevelOwnership is R18OwnershipClass.LevelModelErrorSurvived) return R18FirstLossStage.LevelModelErrorSurvived;
        if (levelError && item.MarkerResolvedLevelStatus == R18ObservationStatus.Observable) return R18FirstLossStage.LevelDeterministicError;
        if (parentError) return R18FirstLossStage.ParentError;
        if (spanError) return R18FirstLossStage.SpanError;
        return R18FirstLossStage.FinalProjectionError;
    }

    private static R18DisagreementMetrics BuildDisagreementMetrics(IReadOnlyList<R18DecisionObservation> items)
    {
        var levelProposals = items.Where(item => item.ProposedLevelStatus == R18ObservationStatus.Observable).ToArray();
        var levelMarker = levelProposals.Where(item => item.MarkerResolvedLevelStatus == R18ObservationStatus.Observable).ToArray();
        var levelFinal = levelProposals.Where(item => item.FinalLevelStatus == R18ObservationStatus.Observable).ToArray();
        var parentFinal = items.Where(item => item.ProposedParentStatus == R18ObservationStatus.Observable &&
            item.FinalParentStatus == R18ObservationStatus.Observable).ToArray();
        var disagreement = levelFinal.Where(item => item.ProposedLevel != item.FinalLevel).ToArray();
        var noDisagreement = levelFinal.Where(item => item.ProposedLevel == item.FinalLevel).ToArray();
        var comparableDisagreement = disagreement.Where(item => item.Reference.IsComparable && item.Reference.ExpectedLevel is not null).ToArray();
        var comparableNoDisagreement = noDisagreement.Where(item => item.Reference.IsComparable && item.Reference.ExpectedLevel is not null).ToArray();
        var disagreementErrors = comparableDisagreement.Count(IsFinalLevelError);
        var noDisagreementErrors = comparableNoDisagreement.Count(IsFinalLevelError);
        return new R18DisagreementMetrics(
            levelProposals.Length,
            levelMarker.Count(item => item.ProposedLevel != item.MarkerResolvedLevel), Rate(levelMarker.Count(item => item.ProposedLevel != item.MarkerResolvedLevel), levelMarker.Length),
            disagreement.Length, Rate(disagreement.Length, levelFinal.Length),
            parentFinal.Count(item => item.ProposedParentId != item.FinalParentId), Rate(parentFinal.Count(item => item.ProposedParentId != item.FinalParentId), parentFinal.Length),
            ComparableCount(disagreement, error: false), ComparableCount(disagreement, error: true),
            ComparableCount(noDisagreement, error: false), ComparableCount(noDisagreement, error: true),
            Rate(disagreementErrors, comparableDisagreement.Length), Rate(noDisagreementErrors, comparableNoDisagreement.Length));
    }

    private static int? ComparableCount(IEnumerable<R18DecisionObservation> items, bool error)
    {
        var comparable = items.Where(item => item.Reference.IsComparable && item.Reference.ExpectedLevel is not null).ToArray();
        if (comparable.Length == 0) return null;
        return comparable.Count(item => IsFinalLevelError(item) == error);
    }

    private static bool IsFinalLevelError(R18DecisionObservation item) =>
        item.Reference.ExpectedLevel is { } expected && item.FinalLevel != expected;

    private static R18FirstLossSummary BuildFirstLossSummary(IReadOnlyList<R18DecisionObservation> items)
    {
        var errors = items.Where(item => item.FirstLoss is not null).ToArray();
        var modelLevelErrors = items.Where(item => item.Reference.IsComparable && item.Reference.ExpectedLevel is not null &&
            item.ProposedLevelStatus == R18ObservationStatus.Observable && item.ProposedLevel != item.Reference.ExpectedLevel).ToArray();
        var corrected = modelLevelErrors.Count(item => item.FinalLevel == item.Reference.ExpectedLevel);
        var modelRoleErrors = items.Where(item => item.Reference.IsComparable && item.Reference.ExpectedRole is not null &&
            item.ProposedRoleStatus == R18ObservationStatus.Observable && item.ProposedRole != item.Reference.ExpectedRole).ToArray();
        var roleCorrected = modelRoleErrors.Count(item => item.FinalRole == item.Reference.ExpectedRole);
        return new R18FirstLossSummary(
            errors.Length,
            errors.Count(item => item.Reference.ExpectedRole is not null && item.FinalRole != item.Reference.ExpectedRole),
            errors.Count(item => item.Reference.ExpectedLevel is not null && item.FinalLevel != item.Reference.ExpectedLevel),
            errors.Count(item => item.Reference.ExpectedParentId is not null && item.FinalParentId != item.Reference.ExpectedParentId),
            errors.Count(item => item.Reference.ExpectedSpan is not null && item.FinalSpan != item.Reference.ExpectedSpan),
            modelLevelErrors.Length, corrected, modelLevelErrors.Length - corrected,
            modelRoleErrors.Length, roleCorrected, modelRoleErrors.Length - roleCorrected,
            errors.GroupBy(item => item.FirstLoss!.Value).ToDictionary(group => group.Key, group => group.Count()));
    }

    private static string DeriveDirection(IReadOnlyList<R18DecisionObservation> items)
    {
        var modelRole = items.Count(item => item.RoleOwnership is
            R18OwnershipClass.RoleModelErrorSurvived or R18OwnershipClass.RoleModelErrorCorrected);
        var modelLevel = items.Count(item => item.LevelOwnership is
            R18OwnershipClass.LevelModelErrorSurvived or R18OwnershipClass.LevelModelErrorCorrected);
        var markerlessSurvivors = items.Count(item => item.LevelOwnership == R18OwnershipClass.LevelModelErrorSurvived &&
            item.MarkerResolvedLevelStatus == R18ObservationStatus.NotObservable);
        if (modelRole > modelLevel) return "ROLE";
        if (markerlessSurvivors > 0) return "MARKERLESS_LEVEL";
        if (modelRole > 0 && modelLevel > 0) return "BOTH";
        return "NO_ACTIONABLE_MODEL_GAP";
    }

    private static double? Rate(int numerator, int denominator) => denominator == 0 ? null : (double)numerator / denominator;
}

public static class R18DecisionOwnershipAuditRunner
{
    public static async Task<R18DecisionOwnershipReport> RunAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var source = new OpenXmlDocumentSource().Read(inputPath);
        var options = new PipelineOptions { DisableLlm = true };
        using var pipeline = new AuthorityExtractionPipeline(options);
        var execution = await pipeline.RunDocumentExecutionAsync(inputPath, null, cancellationToken);
        return R18DecisionOwnershipAnalyzer.Build(source, execution);
    }
}
