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
    [JsonPropertyName("contextSegmentId")] public required string ContextSegmentId { get; init; }
    [JsonPropertyName("systemPrompt")] public required string SystemPrompt { get; init; }
    [JsonPropertyName("userPrompt")] public required string UserPrompt { get; init; }
    [JsonPropertyName("sourceOccurrenceIds")] public required IReadOnlyList<string> SourceOccurrenceIds { get; init; }
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

public sealed record ReasoningModelResponse(
    [property: JsonPropertyName("documentSummary")] string? DocumentSummary,
    [property: JsonPropertyName("headings")] IReadOnlyList<ReasoningHeadingProposal> Headings,
    [property: JsonPropertyName("decisionEvidence")] IReadOnlyList<ReasoningDecisionEvidence> DecisionEvidence,
    [property: JsonPropertyName("rawResponseHash")] string? RawResponseHash = null);

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
