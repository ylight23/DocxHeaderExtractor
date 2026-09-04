using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.R18;

public enum R18ObservationStatus
{
    [JsonStringEnumMemberName("OBSERVABLE")] Observable,
    [JsonStringEnumMemberName("NOT_OBSERVABLE")] NotObservable,
}

public enum R18OwnershipClass
{
    [JsonStringEnumMemberName("ROLE_MODEL_OWNED")] RoleModelOwned,
    [JsonStringEnumMemberName("ROLE_DETERMINISTIC_ASSIGNED")] RoleDeterministicAssigned,
    [JsonStringEnumMemberName("ROLE_MODEL_REJECTED")] RoleModelRejected,
    [JsonStringEnumMemberName("ROLE_MODEL_ERROR_SURVIVED")] RoleModelErrorSurvived,
    [JsonStringEnumMemberName("ROLE_MODEL_ERROR_CORRECTED")] RoleModelErrorCorrected,
    [JsonStringEnumMemberName("LEVEL_MODEL_OWNED")] LevelModelOwned,
    [JsonStringEnumMemberName("LEVEL_MARKER_OWNED")] LevelMarkerOwned,
    [JsonStringEnumMemberName("LEVEL_STRUCTURAL_OWNED")] LevelStructuralOwned,
    [JsonStringEnumMemberName("LEVEL_MODEL_ERROR_CORRECTED")] LevelModelErrorCorrected,
    [JsonStringEnumMemberName("LEVEL_MODEL_ERROR_SURVIVED")] LevelModelErrorSurvived,
    [JsonStringEnumMemberName("PARENT_MODEL_OWNED")] ParentModelOwned,
    [JsonStringEnumMemberName("PARENT_MARKER_OWNED")] ParentMarkerOwned,
    [JsonStringEnumMemberName("PARENT_STRUCTURAL_OWNED")] ParentStructuralOwned,
    [JsonStringEnumMemberName("PARENT_MODEL_ERROR_CORRECTED")] ParentModelErrorCorrected,
    [JsonStringEnumMemberName("PARENT_MODEL_ERROR_SURVIVED")] ParentModelErrorSurvived,
    [JsonStringEnumMemberName("SPAN_MODEL_PROPOSED")] SpanModelProposed,
    [JsonStringEnumMemberName("SPAN_PARSER_VALIDATED")] SpanParserValidated,
    [JsonStringEnumMemberName("SPAN_REJECTED_BY_PARSER_BOUNDARY")] SpanRejectedByParserBoundary,
    [JsonStringEnumMemberName("NOT_OBSERVABLE")] NotObservable,
}

public enum R18ReferenceAuthority
{
    [JsonStringEnumMemberName("HUMAN_GOLD")] HumanGold,
    [JsonStringEnumMemberName("HUMAN_KEY")] HumanKey,
    [JsonStringEnumMemberName("SOURCE_STRUCTURAL_REFERENCE")] SourceStructuralReference,
    [JsonStringEnumMemberName("SILVER")] Silver,
    [JsonStringEnumMemberName("UNKNOWN")] Unknown,
}

public enum R18FirstLossStage
{
    [JsonStringEnumMemberName("SOURCE_LOSS")] SourceLoss,
    [JsonStringEnumMemberName("CANDIDATE_LOSS")] CandidateLoss,
    [JsonStringEnumMemberName("ROLE_MODEL_ERROR")] RoleModelError,
    [JsonStringEnumMemberName("MODEL_VALIDATION_REJECTION")] ModelValidationRejection,
    [JsonStringEnumMemberName("SPAN_ERROR")] SpanError,
    [JsonStringEnumMemberName("LEVEL_MODEL_ERROR_SURVIVED")] LevelModelErrorSurvived,
    [JsonStringEnumMemberName("LEVEL_DETERMINISTIC_ERROR")] LevelDeterministicError,
    [JsonStringEnumMemberName("PARENT_ERROR")] ParentError,
    [JsonStringEnumMemberName("FINAL_PROJECTION_ERROR")] FinalProjectionError,
    [JsonStringEnumMemberName("UNKNOWN")] Unknown,
}

public sealed record R18Span(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);

public sealed record R18ModeEvidence(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("paragraphs")] int Paragraphs,
    [property: JsonPropertyName("styledHeadings")] int StyledHeadings,
    [property: JsonPropertyName("outlineLevelRatio")] double OutlineLevelRatio,
    [property: JsonPropertyName("vietnameseAdminRatio")] double VietnameseAdminRatio,
    [property: JsonPropertyName("typedNumberRatio")] double TypedNumberRatio,
    [property: JsonPropertyName("numberingRatio")] double NumberingRatio,
    [property: JsonPropertyName("legalMarkerRatio")] double LegalMarkerRatio,
    [property: JsonPropertyName("formatDiffers")] bool FormatDiffers);

/// <summary>Optional trusted reference attached by an evaluation profile; never inferred by the audit.</summary>
public sealed record R18ReferenceOutcome
{
    [JsonPropertyName("authority")] public R18ReferenceAuthority Authority { get; init; } = R18ReferenceAuthority.Unknown;
    [JsonPropertyName("expectedRole")] public string? ExpectedRole { get; init; }
    [JsonPropertyName("expectedLevel")] public int? ExpectedLevel { get; init; }
    [JsonPropertyName("expectedParentId")] public string? ExpectedParentId { get; init; }
    [JsonPropertyName("expectedSpan")] public R18Span? ExpectedSpan { get; init; }

    [JsonIgnore]
    public bool IsComparable => Authority is not R18ReferenceAuthority.Unknown &&
        Authority is not R18ReferenceAuthority.Silver;
}

/// <summary>One candidate's observable trace. Null values are paired with status fields to avoid inferred ownership.</summary>
public sealed record R18DecisionObservation
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public int? SourceOrdinal { get; init; }
    [JsonPropertyName("sourceSpan")] public R18Span? SourceSpan { get; init; }

    [JsonPropertyName("wasCandidate")] public bool? WasCandidate { get; init; }
    [JsonPropertyName("candidateReason")] public string? CandidateReason { get; init; }
    [JsonPropertyName("wasModelCalled")] public bool? WasModelCalled { get; init; }

    [JsonPropertyName("proposedRole")] public string? ProposedRole { get; init; }
    [JsonPropertyName("proposedRoleStatus")] public R18ObservationStatus ProposedRoleStatus { get; init; }
    [JsonPropertyName("proposedLevel")] public int? ProposedLevel { get; init; }
    [JsonPropertyName("proposedLevelStatus")] public R18ObservationStatus ProposedLevelStatus { get; init; }
    [JsonPropertyName("proposedParentId")] public string? ProposedParentId { get; init; }
    [JsonPropertyName("proposedParentStatus")] public R18ObservationStatus ProposedParentStatus { get; init; }
    [JsonPropertyName("proposedSpan")] public R18Span? ProposedSpan { get; init; }
    [JsonPropertyName("proposedSpanStatus")] public R18ObservationStatus ProposedSpanStatus { get; init; }

    [JsonPropertyName("validationStatus")] public string? ValidationStatus { get; init; }
    [JsonPropertyName("validationReason")] public string? ValidationReason { get; init; }
    [JsonPropertyName("parserBoundaryStatus")] public R18ObservationStatus ParserBoundaryStatus { get; init; }

    [JsonPropertyName("markerResolvedLevel")] public int? MarkerResolvedLevel { get; init; }
    [JsonPropertyName("markerResolvedLevelStatus")] public R18ObservationStatus MarkerResolvedLevelStatus { get; init; }
    [JsonPropertyName("markerResolvedParentId")] public string? MarkerResolvedParentId { get; init; }
    [JsonPropertyName("markerResolvedParentStatus")] public R18ObservationStatus MarkerResolvedParentStatus { get; init; }

    [JsonPropertyName("structuralResolvedLevel")] public int? StructuralResolvedLevel { get; init; }
    [JsonPropertyName("structuralResolvedLevelStatus")] public R18ObservationStatus StructuralResolvedLevelStatus { get; init; }
    [JsonPropertyName("structuralResolvedParentId")] public string? StructuralResolvedParentId { get; init; }
    [JsonPropertyName("structuralResolvedParentStatus")] public R18ObservationStatus StructuralResolvedParentStatus { get; init; }

    [JsonPropertyName("finalPresent")] public bool? FinalPresent { get; init; }
    [JsonPropertyName("finalRole")] public string? FinalRole { get; init; }
    [JsonPropertyName("finalRoleStatus")] public R18ObservationStatus FinalRoleStatus { get; init; }
    [JsonPropertyName("finalSpan")] public R18Span? FinalSpan { get; init; }
    [JsonPropertyName("finalSpanStatus")] public R18ObservationStatus FinalSpanStatus { get; init; }
    [JsonPropertyName("finalLevel")] public int? FinalLevel { get; init; }
    [JsonPropertyName("finalLevelStatus")] public R18ObservationStatus FinalLevelStatus { get; init; }
    [JsonPropertyName("finalParentId")] public string? FinalParentId { get; init; }
    [JsonPropertyName("finalParentStatus")] public R18ObservationStatus FinalParentStatus { get; init; }

    [JsonPropertyName("reference")] public R18ReferenceOutcome Reference { get; init; } = new();

    [JsonPropertyName("roleOwnership")] public R18OwnershipClass RoleOwnership { get; init; } = R18OwnershipClass.NotObservable;
    [JsonPropertyName("levelOwnership")] public R18OwnershipClass LevelOwnership { get; init; } = R18OwnershipClass.NotObservable;
    [JsonPropertyName("parentOwnership")] public R18OwnershipClass ParentOwnership { get; init; } = R18OwnershipClass.NotObservable;
    [JsonPropertyName("spanOwnership")] public R18OwnershipClass SpanOwnership { get; init; } = R18OwnershipClass.NotObservable;

    [JsonPropertyName("firstLoss")] public R18FirstLossStage? FirstLoss { get; init; }
}

public sealed record R18DisagreementMetrics(
    [property: JsonPropertyName("modelLevelProposalCount")] int ModelLevelProposalCount,
    [property: JsonPropertyName("proposalLevelVsMarkerDisagreement")] int ProposalLevelVsMarkerDisagreement,
    [property: JsonPropertyName("proposalLevelVsMarkerRate")] double? ProposalLevelVsMarkerRate,
    [property: JsonPropertyName("proposalLevelVsFinalDisagreement")] int ProposalLevelVsFinalDisagreement,
    [property: JsonPropertyName("proposalLevelVsFinalRate")] double? ProposalLevelVsFinalRate,
    [property: JsonPropertyName("proposalParentVsFinalDisagreement")] int ProposalParentVsFinalDisagreement,
    [property: JsonPropertyName("proposalParentVsFinalRate")] double? ProposalParentVsFinalRate,
    [property: JsonPropertyName("finalCorrectGivenDisagreement")] int? FinalCorrectGivenDisagreement,
    [property: JsonPropertyName("finalWrongGivenDisagreement")] int? FinalWrongGivenDisagreement,
    [property: JsonPropertyName("finalCorrectWithoutDisagreement")] int? FinalCorrectWithoutDisagreement,
    [property: JsonPropertyName("finalWrongWithoutDisagreement")] int? FinalWrongWithoutDisagreement,
    [property: JsonPropertyName("pFinalErrorGivenDisagreement")] double? PFinalErrorGivenDisagreement,
    [property: JsonPropertyName("pFinalErrorGivenNoDisagreement")] double? PFinalErrorGivenNoDisagreement);

public sealed record R18FirstLossSummary(
    [property: JsonPropertyName("totalFinalErrors")] int TotalFinalErrors,
    [property: JsonPropertyName("roleErrors")] int RoleErrors,
    [property: JsonPropertyName("levelErrors")] int LevelErrors,
    [property: JsonPropertyName("parentErrors")] int ParentErrors,
    [property: JsonPropertyName("spanErrors")] int SpanErrors,
    [property: JsonPropertyName("modelLevelErrorsBeforeResolver")] int ModelLevelErrorsBeforeResolver,
    [property: JsonPropertyName("modelLevelErrorsCorrectedByPipeline")] int ModelLevelErrorsCorrectedByPipeline,
    [property: JsonPropertyName("modelLevelErrorsSurvived")] int ModelLevelErrorsSurvived,
    [property: JsonPropertyName("modelRoleErrors")] int ModelRoleErrors,
    [property: JsonPropertyName("modelRoleErrorsCorrected")] int ModelRoleErrorsCorrected,
    [property: JsonPropertyName("modelRoleErrorsSurvived")] int ModelRoleErrorsSurvived,
    [property: JsonPropertyName("byFirstLoss")] IReadOnlyDictionary<R18FirstLossStage, int> ByFirstLoss);

public sealed record R18DecisionOwnershipReport
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "r18_decision_ownership_report";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "r18-decision-ownership-v1";
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("sourceKind")] public required string SourceKind { get; init; }
    [JsonPropertyName("modeEvidence")] public R18ModeEvidence? ModeEvidence { get; init; }
    [JsonPropertyName("route")] public string? Route { get; init; }
    [JsonPropertyName("observations")] public required IReadOnlyList<R18DecisionObservation> Observations { get; init; }
    [JsonPropertyName("disagreementMetrics")] public required R18DisagreementMetrics DisagreementMetrics { get; init; }
    [JsonPropertyName("firstLossSummary")] public required R18FirstLossSummary FirstLossSummary { get; init; }
    [JsonPropertyName("deterministicDiagnostics")] public required R18DeterministicDiagnosticsReport DeterministicDiagnostics { get; init; }
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
    [JsonPropertyName("referenceAuthorityObserved")] public IReadOnlyList<R18ReferenceAuthority> ReferenceAuthorityObserved { get; init; } = [];
    [JsonPropertyName("referenceBackedObservationCount")] public int ReferenceBackedObservationCount { get; init; }
    [JsonPropertyName("direction")] public string Direction { get; init; } = "NOT_DECIDABLE_WITHOUT_REFERENCE";
    [JsonPropertyName("accuracyClaim")] public string AccuracyClaim { get; init; } = "NOT_MEASURED";
}
