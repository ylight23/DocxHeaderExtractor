using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.HarnessLift;

public enum HarnessHl3RouteOwner
{
    [JsonStringEnumMemberName("DETERMINISTIC_SOURCE_ROUTE")] DeterministicSourceRoute,
    [JsonStringEnumMemberName("DETERMINISTIC_MARKER_ROUTE")] DeterministicMarkerRoute,
    [JsonStringEnumMemberName("DETERMINISTIC_NUMBERING_ROUTE")] DeterministicNumberingRoute,
    [JsonStringEnumMemberName("DETERMINISTIC_PDF_FALLBACK")] DeterministicPdfFallback,
    [JsonStringEnumMemberName("SEMANTIC_MODEL_ROUTE")] SemanticModelRoute,
    [JsonStringEnumMemberName("MIXED_MODEL_PLUS_DETERMINISTIC")] MixedModelPlusDeterministic,
    [JsonStringEnumMemberName("FINAL_COMPATIBILITY_ROUTE")] FinalCompatibilityRoute,
    [JsonStringEnumMemberName("ROUTE_NOT_OBSERVABLE")] RouteNotObservable,
}

public enum HarnessHl3CandidateLossDisposition
{
    [JsonStringEnumMemberName("TRUE_CANDIDATE_NOT_CONSTRUCTED")] TrueCandidateNotConstructed,
    [JsonStringEnumMemberName("TRUE_CANDIDATE_NOT_SELECTED")] TrueCandidateNotSelected,
    [JsonStringEnumMemberName("TRUE_RANKING_BUDGET_LOSS")] TrueRankingBudgetLoss,
    [JsonStringEnumMemberName("DETERMINISTIC_BYPASS_CORRECT")] DeterministicBypassCorrect,
    [JsonStringEnumMemberName("DETERMINISTIC_BYPASS_WRONG")] DeterministicBypassWrong,
    [JsonStringEnumMemberName("REPRESENTATION_MISMATCH")] RepresentationMismatch,
    [JsonStringEnumMemberName("FINAL_SOURCE_LINEAGE_MISMATCH")] FinalSourceLineageMismatch,
    [JsonStringEnumMemberName("TRACE_NOT_OBSERVABLE")] TraceNotObservable,
    [JsonStringEnumMemberName("OTHER_PROVEN_STAGE")] OtherProvenStage,
}

public enum HarnessHl3FirstLossStage
{
    [JsonStringEnumMemberName("SOURCE_NOT_VISIBLE")] SourceNotVisible,
    [JsonStringEnumMemberName("REPRESENTATION_NOT_BRIDGED")] RepresentationNotBridged,
    [JsonStringEnumMemberName("CANDIDATE_NOT_CONSTRUCTED")] CandidateNotConstructed,
    [JsonStringEnumMemberName("CANDIDATE_CONSTRUCTED_NOT_SELECTED")] CandidateConstructedNotSelected,
    [JsonStringEnumMemberName("SELECTION_RANKING_BUDGET_LOSS")] SelectionRankingBudgetLoss,
    [JsonStringEnumMemberName("DETERMINISTIC_BYPASS_CORRECT")] DeterministicBypassCorrect,
    [JsonStringEnumMemberName("DETERMINISTIC_BYPASS_WRONG")] DeterministicBypassWrong,
    [JsonStringEnumMemberName("MODEL_EXPOSED")] ModelExposed,
    [JsonStringEnumMemberName("MODEL_PROPOSAL_WRONG")] ModelProposalWrong,
    [JsonStringEnumMemberName("PROPOSAL_VALIDATION_REJECTION")] ProposalValidationRejection,
    [JsonStringEnumMemberName("MARKER_RESOLUTION_ERROR")] MarkerResolutionError,
    [JsonStringEnumMemberName("STRUCTURAL_RESOLUTION_ERROR")] StructuralResolutionError,
    [JsonStringEnumMemberName("FINAL_PROJECTION_ERROR")] FinalProjectionError,
    [JsonStringEnumMemberName("TRACE_NOT_OBSERVABLE")] TraceNotObservable,
    [JsonStringEnumMemberName("NO_LOSS")] NoLoss,
}

public enum HarnessHl3ModelExposureStatus
{
    [JsonStringEnumMemberName("MODEL_RUN_OCCURRED_IN_DOCUMENT")] ModelRunOccurredInDocument,
    [JsonStringEnumMemberName("MODEL_OCCURRENCE_EXPOSED_PROVEN")] ModelOccurrenceExposedProven,
    [JsonStringEnumMemberName("MODEL_OCCURRENCE_EXPOSURE_UNKNOWN")] ModelOccurrenceExposureUnknown,
    [JsonStringEnumMemberName("MODEL_NOT_APPLICABLE")] ModelNotApplicable,
}

public sealed record HarnessHl3NamespaceComparison(
    [property: JsonPropertyName("namespaceA")] string NamespaceA,
    [property: JsonPropertyName("namespaceB")] string NamespaceB,
    [property: JsonPropertyName("countA")] int CountA,
    [property: JsonPropertyName("countB")] int CountB,
    [property: JsonPropertyName("exactIntersection")] int ExactIntersection,
    [property: JsonPropertyName("aOnly")] int AOnly,
    [property: JsonPropertyName("bOnly")] int BOnly,
    [property: JsonPropertyName("intersectionRate")] double IntersectionRate);

public sealed record HarnessHl3LossClassificationInput(
    bool SourceProven,
    bool RepresentationProven,
    bool DeterministicRouteProven,
    bool DeterministicCorrect,
    bool DeterministicWrong,
    bool CandidateRequired,
    bool CandidateConstructedProven,
    bool CandidateSelectedProven,
    bool RankingBudgetProven,
    bool ModelExposureProven,
    bool ModelProposalWrong,
    bool ProposalValidationRejected,
    bool MarkerResolutionError,
    bool StructuralResolutionError,
    bool FinalSourceLineageMismatch,
    bool FinalProjectionError);

public sealed record HarnessHl3FirstLossInput(
    bool SourceProven,
    bool RepresentationProven,
    bool DeterministicRouteProven,
    bool DeterministicCorrect,
    bool DeterministicWrong,
    bool CandidateRequired,
    bool CandidateConstructedProven,
    bool CandidateSelectedProven,
    bool RankingBudgetProven,
    bool ModelExposureProven,
    bool ModelProposalWrong,
    bool ProposalValidationRejected,
    bool MarkerResolutionError,
    bool StructuralResolutionError,
    bool FinalProjectionError);
