using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public enum ReasoningRoute
{
    ModelCapabilityCeiling,
    ProductionSystem,
    ReasoningPreservingShadow,
}

public enum FirstSystemLossStage
{
    None,
    Source,
    Representation,
    Candidate,
    ModelRequest,
    ModelProposalTransfer,
    SemanticValidator,
    MarkerResolver,
    StructuralResolver,
    TaskProjection,
    FinalOutput,
}

public enum RetentionClassification
{
    BothCorrect,
    ModelCapabilityGap,
    SystemInducedLoss,
    SystemRescue,
    SystemNeutralDifference,
    NotEvaluable,
}

public sealed record ReasoningSourceOccurrence
{
    [JsonPropertyName("sourceOccurrenceId")] public required string SourceOccurrenceId { get; init; }
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public required int SourceOrdinal { get; init; }
    [JsonPropertyName("rawText")] public required string RawText { get; init; }
    [JsonPropertyName("sourceSpan")] public required StructuralSpan SourceSpan { get; init; }
    [JsonPropertyName("candidateHint")] public required CandidateHint CandidateHint { get; init; }
    [JsonPropertyName("styleFacts")] public IReadOnlyDictionary<string, object?> StyleFacts { get; init; } = new Dictionary<string, object?>();
    [JsonPropertyName("layoutFacts")] public IReadOnlyDictionary<string, object?> LayoutFacts { get; init; } = new Dictionary<string, object?>();
    [JsonPropertyName("numberingFacts")] public IReadOnlyDictionary<string, object?> NumberingFacts { get; init; } = new Dictionary<string, object?>();
}

public sealed record CandidateHint(
    [property: JsonPropertyName("candidate")] bool Candidate,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);

public sealed record ReasoningContextSegment
{
    [JsonPropertyName("contextSegmentId")] public required string ContextSegmentId { get; init; }
    [JsonPropertyName("ordinal")] public required int Ordinal { get; init; }
    [JsonPropertyName("sourceOccurrenceIds")] public required IReadOnlyList<string> SourceOccurrenceIds { get; init; }
    [JsonPropertyName("ownedSourceOccurrenceIds")] public IReadOnlyList<string> OwnedSourceOccurrenceIds { get; init; } = [];
    [JsonPropertyName("visibleStartOrdinal")] public int? VisibleStartOrdinal { get; init; }
    [JsonPropertyName("visibleEndOrdinal")] public int? VisibleEndOrdinal { get; init; }
    [JsonPropertyName("ownedStartOrdinal")] public int? OwnedStartOrdinal { get; init; }
    [JsonPropertyName("ownedEndOrdinal")] public int? OwnedEndOrdinal { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
}

public sealed record ReasoningContextPack
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("contextStrategy")] public required string ContextStrategy { get; init; }
    [JsonPropertyName("sourceCharacters")] public required int SourceCharacters { get; init; }
    [JsonPropertyName("modelVisibleCharacters")] public required int ModelVisibleCharacters { get; init; }
    [JsonPropertyName("sourceOccurrenceCoverage")] public required double SourceOccurrenceCoverage { get; init; }
    [JsonPropertyName("windowCount")] public required int WindowCount { get; init; }
    [JsonPropertyName("overlapCharacters")] public required int OverlapCharacters { get; init; }
    [JsonPropertyName("globalConsolidationUsed")] public required bool GlobalConsolidationUsed { get; init; }
    [JsonPropertyName("occurrences")] public required IReadOnlyList<ReasoningSourceOccurrence> Occurrences { get; init; }
    [JsonPropertyName("segments")] public required IReadOnlyList<ReasoningContextSegment> Segments { get; init; }
}

public sealed record ReasoningModelRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("route")] public required string Route { get; init; }
    [JsonPropertyName("semanticPassId")] public required string SemanticPassId { get; init; }
    [JsonPropertyName("contextSegmentId")] public required string ContextSegmentId { get; init; }
    [JsonPropertyName("systemPrompt")] public required string SystemPrompt { get; init; }
    [JsonPropertyName("userPrompt")] public required string UserPrompt { get; init; }
    [JsonPropertyName("sourceOccurrenceIds")] public required IReadOnlyList<string> SourceOccurrenceIds { get; init; }
    [JsonPropertyName("ownedSourceOccurrenceIds")] public required IReadOnlyList<string> OwnedSourceOccurrenceIds { get; init; }
    [JsonPropertyName("ownedStartOrdinal")] public int? OwnedStartOrdinal { get; init; }
    [JsonPropertyName("ownedEndOrdinal")] public int? OwnedEndOrdinal { get; init; }
    [JsonPropertyName("configurationSignature")] public required string ConfigurationSignature { get; init; }
}

public sealed record ReasoningDecisionEvidence(
    [property: JsonPropertyName("evidenceType")] string EvidenceType,
    [property: JsonPropertyName("sourceReference")] string SourceReference,
    [property: JsonPropertyName("shortEvidenceCode")] string ShortEvidenceCode);

public sealed record ReasoningHeadingProposal
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("headingSpan")] public required StructuralSpan HeadingSpan { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("semanticRole")] public required string SemanticRole { get; init; }
    [JsonPropertyName("proposedLevel")] public int? ProposedLevel { get; init; }
    [JsonPropertyName("proposedParent")] public string? ProposedParent { get; init; }
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
    [JsonPropertyName("decisionEvidence")] public IReadOnlyList<ReasoningDecisionEvidence> DecisionEvidence { get; init; } = [];
}

public sealed record ReasoningOrdinalRange(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);

public sealed record ReasoningModelResponse(
    [property: JsonPropertyName("documentSummary")] string? DocumentSummary,
    [property: JsonPropertyName("headings")] IReadOnlyList<ReasoningHeadingProposal> Headings,
    [property: JsonPropertyName("decisionEvidence")] IReadOnlyList<ReasoningDecisionEvidence> DecisionEvidence,
    [property: JsonPropertyName("rawResponseHash")] string? RawResponseHash = null,
    [property: JsonPropertyName("ownedRange")] ReasoningOrdinalRange? OwnedRange = null,
    [property: JsonPropertyName("complete")] bool Complete = true,
    [property: JsonPropertyName("requestId")] string? RequestId = null,
    [property: JsonPropertyName("semanticPassId")] string? SemanticPassId = null);

public static class ReasoningCompletionFailureClass
{
    public const string ProviderOutputLimit = "PROVIDER_OUTPUT_LIMIT";
    public const string TransportTruncation = "TRANSPORT_TRUNCATION";
    public const string CompleteResponseSchemaInvalid = "COMPLETE_RESPONSE_SCHEMA_INVALID";
    public const string Timeout = "TIMEOUT";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string ProviderAuthFailure = "PROVIDER_AUTH_FAILURE";
    public const string OtherProviderFailure = "OTHER_PROVIDER_FAILURE";
}

public sealed class ReasoningCompletionTelemetry
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
    [JsonPropertyName("route")] public string Route { get; set; } = "";
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("semanticPassId")] public string SemanticPassId { get; set; } = "";
    [JsonPropertyName("contextSegmentId")] public string ContextSegmentId { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("temperature")] public double Temperature { get; set; }
    [JsonPropertyName("seed")] public int? Seed { get; set; }
    [JsonPropertyName("inputCharacters")] public int InputCharacters { get; set; }
    [JsonPropertyName("estimatedInputTokens")] public int? EstimatedInputTokens { get; set; }
    [JsonPropertyName("configuredMaxOutputTokens")] public int ConfiguredMaxOutputTokens { get; set; }
    [JsonPropertyName("httpStatus")] public int? HttpStatus { get; set; }
    [JsonPropertyName("finishReason")] public string? FinishReason { get; set; }
    [JsonPropertyName("providerRequestId")] public string? ProviderRequestId { get; set; }
    [JsonPropertyName("reportedInputTokens")] public int? ReportedInputTokens { get; set; }
    [JsonPropertyName("reportedOutputTokens")] public int? ReportedOutputTokens { get; set; }
    [JsonPropertyName("reportedTotalTokens")] public int? ReportedTotalTokens { get; set; }
    [JsonPropertyName("receivedContentCharacters")] public int ReceivedContentCharacters { get; set; }
    [JsonPropertyName("receivedContentBytes")] public int ReceivedContentBytes { get; set; }
    [JsonPropertyName("firstContentHash")] public string? FirstContentHash { get; set; }
    [JsonPropertyName("lastContentHash")] public string? LastContentHash { get; set; }
    [JsonPropertyName("fullContentSha256")] public string? FullContentSha256 { get; set; }
    [JsonPropertyName("jsonParseSucceeded")] public bool JsonParseSucceeded { get; set; }
    [JsonPropertyName("jsonParseErrorOffset")] public long? JsonParseErrorOffset { get; set; }
    [JsonPropertyName("completionEnvelopeComplete")] public bool CompletionEnvelopeComplete { get; set; }
    [JsonPropertyName("streamCompletedNormally")] public bool StreamCompletedNormally { get; set; }
    [JsonPropertyName("transportException")] public string? TransportException { get; set; }
    [JsonPropertyName("failureClass")] public string? FailureClass { get; set; }
}

public sealed class ReasoningCompletionException : Exception
{
    public ReasoningCompletionException(
        string failureClass,
        string message,
        ReasoningCompletionTelemetry telemetry,
        Exception? innerException = null,
        IReadOnlyList<ReasoningCompletionTelemetry>? attemptTelemetry = null)
        : base(message, innerException)
    {
        FailureClass = failureClass;
        Telemetry = telemetry;
        AttemptTelemetry = attemptTelemetry ?? [telemetry];
    }

    public string FailureClass { get; }
    public ReasoningCompletionTelemetry Telemetry { get; }
    public IReadOnlyList<ReasoningCompletionTelemetry> AttemptTelemetry { get; }
}

public interface IReasoningCompletionTelemetrySource
{
    IReadOnlyList<ReasoningCompletionTelemetry> CompletionTelemetry { get; }
}

public sealed record ReasoningCompletionRunStats(
    [property: JsonPropertyName("attemptCount")] int AttemptCount = 0,
    [property: JsonPropertyName("successfulCompletionCount")] int SuccessfulCompletionCount = 0,
    [property: JsonPropertyName("failedCompletionCount")] int FailedCompletionCount = 0,
    [property: JsonPropertyName("rangeSplitCount")] int RangeSplitCount = 0,
    [property: JsonPropertyName("retryCount")] int RetryCount = 0,
    [property: JsonPropertyName("semanticPassCount")] int SemanticPassCount = 0,
    [property: JsonPropertyName("consolidationPassCount")] int ConsolidationPassCount = 0,
    [property: JsonPropertyName("outOfScopeProposalCount")] int OutOfScopeProposalCount = 0);

public sealed record ReasoningCompletionStats(
    [property: JsonPropertyName("completion")] ReasoningCompletionRunStats Completion,
    [property: JsonPropertyName("failureClasses")] IReadOnlyList<string> FailureClasses);

public interface IReasoningSemanticModel
{
    string ModelName { get; }
    string ProviderName { get; }
    int ContextSize { get; }
    int ProviderCalls { get; }
    Task<ReasoningModelResponse> CompleteAsync(ReasoningModelRequest request, CancellationToken ct = default);
}

public sealed record ReasoningValidatedProposal(
    [property: JsonPropertyName("proposal")] ReasoningHeadingProposal Proposal,
    [property: JsonPropertyName("elementId")] string ElementId,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason);

public sealed record ReasoningRouteObservation
{
    [JsonPropertyName("route")] public required ReasoningRoute Route { get; init; }
    [JsonPropertyName("contextVisible")] public required IReadOnlySet<string> ContextVisible { get; init; }
    [JsonPropertyName("proposed")] public required IReadOnlyList<ReasoningHeadingProposal> Proposed { get; init; }
    [JsonPropertyName("validated")] public required IReadOnlyList<ReasoningValidatedProposal> Validated { get; init; }
    [JsonPropertyName("finalIncluded")] public required IReadOnlySet<string> FinalIncluded { get; init; }
    [JsonPropertyName("providerCalls")] public required int ProviderCalls { get; init; }
    [JsonPropertyName("completionStats")] public ReasoningCompletionStats CompletionStats { get; init; } =
        new(new ReasoningCompletionRunStats(), []);
    [JsonPropertyName("sourceToModelContextCoverage")] public double SourceToModelContextCoverage { get; init; }
    [JsonPropertyName("sourceToSemanticPassCoverage")] public double SourceToSemanticPassCoverage { get; init; }
    [JsonPropertyName("sourceOutputOwnershipCoverage")] public double SourceOutputOwnershipCoverage { get; init; }
}

public sealed record ReasoningGoldOccurrence
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("goldOccurrenceId")] public required string GoldOccurrenceId { get; init; }
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("headingSpan")] public StructuralSpan? HeadingSpan { get; init; }
    [JsonPropertyName("goldRole")] public string? GoldRole { get; init; }
    [JsonPropertyName("goldLevel")] public int? GoldLevel { get; init; }
    [JsonPropertyName("goldParent")] public string? GoldParent { get; init; }
    [JsonPropertyName("exactText")] public required string ExactText { get; init; }
}

public sealed record ReasoningRetentionLedgerEntry
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("goldOccurrenceId")] public required string GoldOccurrenceId { get; init; }
    [JsonPropertyName("goldRole")] public string? GoldRole { get; init; }
    [JsonPropertyName("goldLevel")] public int? GoldLevel { get; init; }
    [JsonPropertyName("goldParent")] public string? GoldParent { get; init; }
    [JsonPropertyName("sourcePresent")] public required bool SourcePresent { get; init; }
    [JsonPropertyName("sourceOccurrenceId")] public string? SourceOccurrenceId { get; init; }
    [JsonPropertyName("ceiling")] public required ReasoningObservationSnapshot Ceiling { get; init; }
    [JsonPropertyName("production")] public required ReasoningProductionSnapshot Production { get; init; }
    [JsonPropertyName("shadow")] public required ReasoningObservationSnapshot Shadow { get; init; }
    [JsonPropertyName("firstSystemLossStage")] public required FirstSystemLossStage FirstSystemLossStage { get; init; }
    [JsonPropertyName("classification")] public required RetentionClassification Classification { get; init; }
}

public sealed record ReasoningObservationSnapshot(
    [property: JsonPropertyName("contextVisible")] bool ContextVisible,
    [property: JsonPropertyName("proposed")] bool Proposed,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("level")] int? Level,
    [property: JsonPropertyName("parent")] string? Parent,
    [property: JsonPropertyName("finalIncluded")] bool FinalIncluded);

public sealed record ReasoningProductionSnapshot(
    [property: JsonPropertyName("sourcePresent")] bool SourcePresent,
    [property: JsonPropertyName("representationPresent")] bool RepresentationPresent,
    [property: JsonPropertyName("candidatePresent")] bool CandidatePresent,
    [property: JsonPropertyName("modelRequestPresent")] bool ModelRequestPresent,
    [property: JsonPropertyName("modelProposed")] bool ModelProposed,
    [property: JsonPropertyName("postValidatorPresent")] bool PostValidatorPresent,
    [property: JsonPropertyName("postResolverPresent")] bool PostResolverPresent,
    [property: JsonPropertyName("finalIncluded")] bool FinalIncluded,
    [property: JsonPropertyName("finalRole")] string? FinalRole,
    [property: JsonPropertyName("finalLevel")] int? FinalLevel,
    [property: JsonPropertyName("finalParent")] string? FinalParent);

public sealed record RetentionMetric(
    [property: JsonPropertyName("precision")] double? Precision,
    [property: JsonPropertyName("recall")] double? Recall,
    [property: JsonPropertyName("f1")] double? F1,
    [property: JsonPropertyName("roleAccuracy")] double? RoleAccuracy,
    [property: JsonPropertyName("levelAccuracy")] double? LevelAccuracy,
    [property: JsonPropertyName("parentAccuracy")] double? ParentAccuracy,
    [property: JsonPropertyName("hierarchyAccuracy")] double? HierarchyAccuracy);

public sealed record RetentionPairMetrics(
    [property: JsonPropertyName("ceiling")] RetentionMetric Ceiling,
    [property: JsonPropertyName("production")] RetentionMetric Production,
    [property: JsonPropertyName("shadow")] RetentionMetric Shadow,
    [property: JsonPropertyName("retentionGapF1")] double? RetentionGapF1,
    [property: JsonPropertyName("retentionRatioF1")] double? RetentionRatioF1,
    [property: JsonPropertyName("ceilingCorrectSystemCorrect")] int CeilingCorrectSystemCorrect,
    [property: JsonPropertyName("ceilingCorrectSystemWrong")] int CeilingCorrectSystemWrong,
    [property: JsonPropertyName("ceilingWrongSystemCorrect")] int CeilingWrongSystemCorrect,
    [property: JsonPropertyName("ceilingWrongSystemWrong")] int CeilingWrongSystemWrong);
