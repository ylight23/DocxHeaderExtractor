using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.HarnessLift;

public enum HarnessReferenceAuthority
{
    [JsonStringEnumMemberName("HUMAN_GOLD")] HumanGold,
    [JsonStringEnumMemberName("HUMAN_KEY")] HumanKey,
    [JsonStringEnumMemberName("SOURCE_STRUCTURAL_REFERENCE")] SourceStructuralReference,
    [JsonStringEnumMemberName("MODEL_ASSISTED_SILVER")] ModelAssistedSilver,
    [JsonStringEnumMemberName("HEURISTIC_REFERENCE")] HeuristicReference,
    [JsonStringEnumMemberName("UNLABELED")] Unlabeled,
    [JsonStringEnumMemberName("INVALID_REFERENCE")] InvalidReference,
}

public enum HarnessCoverage
{
    [JsonStringEnumMemberName("FULL")] Full,
    [JsonStringEnumMemberName("PARTIAL")] Partial,
    [JsonStringEnumMemberName("UNKNOWN")] Unknown,
}

public enum HarnessEvidenceGranularity
{
    [JsonStringEnumMemberName("OCCURRENCE")] Occurrence,
    [JsonStringEnumMemberName("DOCUMENT")] Document,
    [JsonStringEnumMemberName("AGGREGATE_ONLY")] AggregateOnly,
}

public enum HarnessEvidenceStrength
{
    [JsonStringEnumMemberName("PROVEN")] Proven,
    [JsonStringEnumMemberName("PARTIAL")] Partial,
    [JsonStringEnumMemberName("DIAGNOSTIC_ONLY")] DiagnosticOnly,
    [JsonStringEnumMemberName("NOT_OBSERVABLE")] NotObservable,
    [JsonStringEnumMemberName("UNKNOWN")] Unknown,
}

public enum HarnessKnowledge
{
    [JsonStringEnumMemberName("PROVEN")] Proven,
    [JsonStringEnumMemberName("PARTIAL")] Partial,
    [JsonStringEnumMemberName("NOT_APPLICABLE")] NotApplicable,
    [JsonStringEnumMemberName("NOT_OBSERVABLE")] NotObservable,
    [JsonStringEnumMemberName("UNKNOWN")] Unknown,
}

public enum HarnessLossStage
{
    [JsonStringEnumMemberName("SOURCE_LOSS")] SourceLoss,
    [JsonStringEnumMemberName("CANDIDATE_LOSS")] CandidateLoss,
    [JsonStringEnumMemberName("SELECTION_RANKING_BUDGET")] SelectionRankingBudget,
    [JsonStringEnumMemberName("MODEL_ROLE")] ModelRole,
    [JsonStringEnumMemberName("MODEL_LEVEL")] ModelLevel,
    [JsonStringEnumMemberName("MODEL_PARENT")] ModelParent,
    [JsonStringEnumMemberName("MODEL_SPAN")] ModelSpan,
    [JsonStringEnumMemberName("PROPOSAL_VALIDATION")] ProposalValidation,
    [JsonStringEnumMemberName("SPAN_TIMEOUT_WRAPPER")] SpanTimeoutWrapper,
    [JsonStringEnumMemberName("GROUNDING")] Grounding,
    [JsonStringEnumMemberName("ALIGNMENT")] Alignment,
    [JsonStringEnumMemberName("MARKER_RESOLUTION")] MarkerResolution,
    [JsonStringEnumMemberName("STRUCTURAL_RESOLUTION")] StructuralResolution,
    [JsonStringEnumMemberName("PRECISION_GATE")] PrecisionGate,
    [JsonStringEnumMemberName("FINAL_PROJECTION")] FinalProjection,
    [JsonStringEnumMemberName("UNKNOWN")] Unknown,
}

public enum HarnessMetric
{
    HeadingExistence,
    Role,
    Span,
    Level,
    Parent,
    Hierarchy,
}

public sealed record HarnessCorpusDocument
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("sourceSha256")] public required string SourceSha256 { get; init; }
    [JsonPropertyName("documentGroupId")] public string? DocumentGroupId { get; init; }
    [JsonPropertyName("split")] public string? Split { get; init; }
    [JsonPropertyName("sourceKind")] public required string SourceKind { get; init; }
    [JsonPropertyName("familyId")] public string? FamilyId { get; init; }
    [JsonPropertyName("familyAssignmentAuthority")] public string? FamilyAssignmentAuthority { get; init; }
    [JsonPropertyName("documentMode")] public string? DocumentMode { get; init; }
    [JsonPropertyName("documentModeStatus")] public string DocumentModeStatus { get; init; } = "NOT_OBSERVABLE";
    [JsonPropertyName("joinMethod")] public required string JoinMethod { get; init; }
}

public sealed record HarnessReferenceRecord
{
    [JsonPropertyName("referenceId")] public required string ReferenceId { get; init; }
    [JsonPropertyName("documentId")] public string? DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public string? DocumentGroupId { get; init; }
    [JsonPropertyName("authority")] public required HarnessReferenceAuthority Authority { get; init; }
    [JsonPropertyName("sourcePath")] public required string SourcePath { get; init; }
    [JsonPropertyName("sourceSha256")] public string? SourceSha256 { get; init; }
    [JsonPropertyName("coverage")] public required HarnessCoverage Coverage { get; init; }
    [JsonPropertyName("supportedMetrics")] public required IReadOnlyList<string> SupportedMetrics { get; init; }
    [JsonPropertyName("provenance")] public required string Provenance { get; init; }
    [JsonPropertyName("notes")] public required string Notes { get; init; }
}

public sealed record HarnessHistoricalEvidence
{
    [JsonPropertyName("evidenceId")] public required string EvidenceId { get; init; }
    [JsonPropertyName("documentId")] public string? DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public string? DocumentGroupId { get; init; }
    [JsonPropertyName("sourceArtifact")] public required string SourceArtifact { get; init; }
    [JsonPropertyName("sourceCommit")] public string? SourceCommit { get; init; }
    [JsonPropertyName("sourceProbe")] public string? SourceProbe { get; init; }
    [JsonPropertyName("sourceReferenceAuthority")] public HarnessReferenceAuthority SourceReferenceAuthority { get; init; }
    [JsonPropertyName("evidenceGranularity")] public HarnessEvidenceGranularity EvidenceGranularity { get; init; }
    [JsonPropertyName("evidenceStrength")] public HarnessEvidenceStrength EvidenceStrength { get; init; }
    [JsonPropertyName("occurrenceIdentity")] public HarnessOccurrenceIdentity? OccurrenceIdentity { get; init; }
    [JsonPropertyName("historicalStage")] public string? HistoricalStage { get; init; }
    [JsonPropertyName("historicalFinding")] public string? HistoricalFinding { get; init; }
    [JsonPropertyName("r18FirstLossCompatibility")] public string? R18FirstLossCompatibility { get; init; }
    [JsonPropertyName("fineGrainedLossStage")] public string? FineGrainedLossStage { get; init; }
    [JsonPropertyName("reusableForCurrentAttribution")] public string ReusableForCurrentAttribution { get; init; } = "NO";
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}

public sealed record HarnessOccurrenceIdentity
{
    [JsonPropertyName("sourceId")] public string? SourceId { get; init; }
    [JsonPropertyName("stableId")] public string? StableId { get; init; }
    [JsonPropertyName("sourceLineIds")] public IReadOnlyList<string> SourceLineIds { get; init; } = [];
    [JsonPropertyName("index")] public int? Index { get; init; }
    [JsonPropertyName("span")] public HarnessSpan? Span { get; init; }
}

public sealed record HarnessSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);

public sealed record HarnessMetricEligibility(
    [property: JsonPropertyName("headingExistence")] HarnessKnowledge HeadingExistence,
    [property: JsonPropertyName("role")] HarnessKnowledge Role,
    [property: JsonPropertyName("span")] HarnessKnowledge Span,
    [property: JsonPropertyName("level")] HarnessKnowledge Level,
    [property: JsonPropertyName("parent")] HarnessKnowledge Parent,
    [property: JsonPropertyName("hierarchy")] HarnessKnowledge Hierarchy);

public sealed record HarnessCurrentTrace
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public string? DocumentGroupId { get; init; }
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public int? SourceOrdinal { get; init; }
    [JsonPropertyName("documentMode")] public string? DocumentMode { get; init; }
    [JsonPropertyName("candidateSelected")] public bool? CandidateSelected { get; init; }
    [JsonPropertyName("candidateReason")] public string? CandidateReason { get; init; }
    [JsonPropertyName("modelCalled")] public bool? ModelCalled { get; init; }
    [JsonPropertyName("modelProposal")] public HarnessProposalTrace ModelProposal { get; init; } = new();
    [JsonPropertyName("proposalValidation")] public HarnessValidationTrace ProposalValidation { get; init; } = new();
    [JsonPropertyName("markerStage")] public HarnessStageTrace MarkerStage { get; init; } = new();
    [JsonPropertyName("structuralStage")] public HarnessStageTrace StructuralStage { get; init; } = new();
    [JsonPropertyName("final")] public HarnessFinalTrace Final { get; init; } = new();
    [JsonPropertyName("ownership")] public HarnessOwnershipTrace Ownership { get; init; } = new();
}

public sealed record HarnessProposalTrace
{
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("proposedLevel")] public int? ProposedLevel { get; init; }
    [JsonPropertyName("proposedParent")] public string? ProposedParent { get; init; }
    [JsonPropertyName("proposedSpan")] public HarnessSpan? ProposedSpan { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "NOT_OBSERVABLE";
}

public sealed record HarnessValidationTrace
{
    [JsonPropertyName("accepted")] public bool? Accepted { get; init; }
    [JsonPropertyName("issueCodes")] public IReadOnlyList<string> IssueCodes { get; init; } = [];
}

public sealed record HarnessStageTrace
{
    [JsonPropertyName("applicable")] public bool Applicable { get; init; }
    [JsonPropertyName("levelBefore")] public int? LevelBefore { get; init; }
    [JsonPropertyName("levelAfter")] public int? LevelAfter { get; init; }
    [JsonPropertyName("parentBefore")] public string? ParentBefore { get; init; }
    [JsonPropertyName("parentAfter")] public string? ParentAfter { get; init; }
    [JsonPropertyName("changedLevel")] public bool ChangedLevel { get; init; }
    [JsonPropertyName("changedParent")] public bool ChangedParent { get; init; }
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; init; }
}

public sealed record HarnessFinalTrace
{
    [JsonPropertyName("included")] public bool? Included { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("level")] public int? Level { get; init; }
    [JsonPropertyName("parent")] public string? Parent { get; init; }
    [JsonPropertyName("span")] public HarnessSpan? Span { get; init; }
}

public sealed record HarnessOwnershipTrace
{
    [JsonPropertyName("role")] public string Role { get; init; } = "NOT_OBSERVABLE";
    [JsonPropertyName("level")] public string Level { get; init; } = "NOT_OBSERVABLE";
    [JsonPropertyName("parent")] public string Parent { get; init; } = "NOT_OBSERVABLE";
    [JsonPropertyName("span")] public string Span { get; init; } = "NOT_OBSERVABLE";
}

public sealed record HarnessLiftAggregate
{
    [JsonPropertyName("population")] public int Population { get; init; }
    [JsonPropertyName("measured")] public int Measured { get; init; }
    [JsonPropertyName("correct")] public int Correct { get; init; }
    [JsonPropertyName("errors")] public int Errors { get; init; }
    [JsonPropertyName("precision")] public double? Precision { get; init; }
    [JsonPropertyName("recall")] public double? Recall { get; init; }
    [JsonPropertyName("f1")] public double? F1 { get; init; }
    [JsonPropertyName("accuracy")] public double? Accuracy { get; init; }
    [JsonPropertyName("unmeasured")] public int Unmeasured { get; init; }
}

public sealed record HarnessRepeatedStatistic(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("mean")] double? Mean,
    [property: JsonPropertyName("min")] double? Min,
    [property: JsonPropertyName("max")] double? Max,
    [property: JsonPropertyName("stddev")] double? Stddev);

