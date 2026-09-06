using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public enum A99Doc0205CanaryClassification
{
    [JsonStringEnumMemberName("PROVEN_SOURCE_READING_LOSS")] ProvenSourceReadingLoss,
    [JsonStringEnumMemberName("PROVEN_REPRESENTATION_LOSS")] ProvenRepresentationLoss,
    [JsonStringEnumMemberName("PROVEN_CANDIDATE_CONSTRUCTION_LOSS")] ProvenCandidateConstructionLoss,
    [JsonStringEnumMemberName("PROVEN_CANDIDATE_SELECTION_LOSS")] ProvenCandidateSelectionLoss,
    [JsonStringEnumMemberName("PROVEN_MODEL_NOT_EXPOSED")] ProvenModelNotExposed,
    [JsonStringEnumMemberName("PROVEN_FINAL_PROJECTION_LOSS")] ProvenFinalProjectionLoss,
    [JsonStringEnumMemberName("NO_PROVEN_PREMODEL_LOSS")] NoProvenPremodelLoss,
    [JsonStringEnumMemberName("UNRESOLVED")] Unresolved,

    [Obsolete("Use ProvenCandidateSelectionLoss.")]
    ProvenSelectionLoss = ProvenCandidateSelectionLoss,
    [Obsolete("Use ProvenFinalProjectionLoss.")]
    ProvenFinalLoss = ProvenFinalProjectionLoss,
    [Obsolete("Use NoProvenPremodelLoss.")]
    NoLoss = NoProvenPremodelLoss,
}

/// <summary>
/// The evidence available for one retained reference heading. Null means the current artifacts
/// do not prove that stage, and must never be converted into a loss label.
/// </summary>
public sealed record A99Doc0205CanaryObservation(
    bool ExactReferenceSpanKnown,
    string? OwningPhysicalSourceId,
    bool? LogicalSegmentExists,
    bool? CandidateConstructed,
    bool? CandidateSelected,
    bool? RequestMembership,
    bool? ModelExposed,
    bool? FinalLineagePresent);

public sealed record A99Doc0205CanaryEntry
{
    [JsonPropertyName("referenceId")] public required string ReferenceId { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("referenceExactSpan")] public A99ReviewSpan? ReferenceExactSpan { get; init; }
    [JsonPropertyName("referenceSpanStatus")] public required string ReferenceSpanStatus { get; init; }
    [JsonPropertyName("owningPhysicalSourceId")] public string? OwningPhysicalSourceId { get; init; }
    [JsonPropertyName("physicalSourceStatus")] public required string PhysicalSourceStatus { get; init; }
    [JsonPropertyName("owningPhysicalSourceEnvelope")] public A99ReviewSpan? OwningPhysicalSourceEnvelope { get; init; }
    [JsonPropertyName("logicalSegmentExists")] public bool? LogicalSegmentExists { get; init; }
    [JsonPropertyName("logicalSegmentStatus")] public required string LogicalSegmentStatus { get; init; }
    [JsonPropertyName("candidateConstructed")] public bool? CandidateConstructed { get; init; }
    [JsonPropertyName("candidateConstructionStatus")] public required string CandidateConstructionStatus { get; init; }
    [JsonPropertyName("candidateSelected")] public bool? CandidateSelected { get; init; }
    [JsonPropertyName("selectionStatus")] public required string SelectionStatus { get; init; }
    [JsonPropertyName("requestMembership")] public bool? RequestMembership { get; init; }
    [JsonPropertyName("requestMembershipStatus")] public required string RequestMembershipStatus { get; init; }
    [JsonPropertyName("modelExposed")] public bool? ModelExposed { get; init; }
    [JsonPropertyName("modelExposureStatus")] public required string ModelExposureStatus { get; init; }
    [JsonPropertyName("finalLineagePresent")] public bool? FinalLineagePresent { get; init; }
    [JsonPropertyName("finalLineageStatus")] public required string FinalLineageStatus { get; init; }
    [JsonPropertyName("classification")] public A99Doc0205CanaryClassification Classification { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}

public sealed record A99Doc0205ReconciliationCanaryReport
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_doc_0205_reconciliation_canary";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-doc-0205-reconciliation-v1";
    [JsonPropertyName("documentId")] public string DocumentId { get; init; } = "DOC-0205";
    [JsonPropertyName("referenceSource")] public required string ReferenceSource { get; init; }
    [JsonPropertyName("modelTraceSource")] public required string ModelTraceSource { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("referenceCount")] public int ReferenceCount { get; init; }
    [JsonPropertyName("classificationCounts")] public IReadOnlyDictionary<A99Doc0205CanaryClassification, int> ClassificationCounts { get; init; } = new Dictionary<A99Doc0205CanaryClassification, int>();
    [JsonPropertyName("entries")] public IReadOnlyList<A99Doc0205CanaryEntry> Entries { get; init; } = [];
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
    [JsonPropertyName("productionChanged")] public bool ProductionChanged { get; init; }
    [JsonPropertyName("representationCanary")] public A99Doc0205RepresentationReport RepresentationCanary { get; init; } = new();
    [JsonPropertyName("note")] public required string Note { get; init; }
}

public sealed record A99Doc0205RepresentationReport
{
    [JsonPropertyName("status")] public string Status { get; init; } = "NOT_RUN";
    [JsonPropertyName("physicalSourceId")] public string PhysicalSourceId { get; init; } = "body[1]/p[4]";
    [JsonPropertyName("logicalHeadingOccurrences")] public int LogicalHeadingOccurrences { get; init; }
    [JsonPropertyName("distinctOccurrenceIds")] public bool DistinctOccurrenceIds { get; init; }
    [JsonPropertyName("parentChildSharePhysicalSource")] public bool ParentChildSharePhysicalSource { get; init; }
    [JsonPropertyName("evaluatorTruePositives")] public int EvaluatorTruePositives { get; init; }
    [JsonPropertyName("evaluatorFalseNegatives")] public int EvaluatorFalseNegatives { get; init; }
}

public static class A99Doc0205ReconciliationCanary
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static A99Doc0205CanaryClassification Classify(A99Doc0205CanaryObservation evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.ExactReferenceSpanKnown) return A99Doc0205CanaryClassification.Unresolved;
        if (string.IsNullOrWhiteSpace(evidence.OwningPhysicalSourceId))
            return A99Doc0205CanaryClassification.ProvenSourceReadingLoss;
        if (evidence.LogicalSegmentExists == false)
            return A99Doc0205CanaryClassification.ProvenRepresentationLoss;
        if (evidence.LogicalSegmentExists is null) return A99Doc0205CanaryClassification.Unresolved;
        if (evidence.CandidateConstructed == false) return A99Doc0205CanaryClassification.ProvenCandidateConstructionLoss;
        if (evidence.CandidateConstructed is null) return A99Doc0205CanaryClassification.Unresolved;
        if (evidence.CandidateSelected == false) return A99Doc0205CanaryClassification.ProvenCandidateSelectionLoss;
        if (evidence.CandidateSelected is null) return A99Doc0205CanaryClassification.Unresolved;
        if (evidence.ModelExposed == false) return A99Doc0205CanaryClassification.ProvenModelNotExposed;
        if (evidence.ModelExposed is null || evidence.RequestMembership is null) return A99Doc0205CanaryClassification.Unresolved;
        if (evidence.RequestMembership == false) return A99Doc0205CanaryClassification.ProvenModelNotExposed;
        if (evidence.FinalLineagePresent == false) return A99Doc0205CanaryClassification.ProvenFinalProjectionLoss;
        if (evidence.FinalLineagePresent is null) return A99Doc0205CanaryClassification.Unresolved;
        return A99Doc0205CanaryClassification.NoProvenPremodelLoss;
    }

    public static A99Doc0205ReconciliationCanaryReport Run(
        string referenceBridgePath,
        string modelTracePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceBridgePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelTracePath);
        var referenceEnvelope = JsonSerializer.Deserialize<ReferenceEnvelope>(File.ReadAllText(referenceBridgePath), JsonOptions)
            ?? throw new InvalidDataException("DOC-0205 reference bridge is empty.");
        var modelEnvelope = JsonSerializer.Deserialize<ModelEnvelope>(File.ReadAllText(modelTracePath), JsonOptions)
            ?? throw new InvalidDataException("Model occurrence bridge is empty.");
        var references = referenceEnvelope.Occurrences.Where(x => x.DocumentId == "DOC-0205").ToArray();
        var modelBySource = modelEnvelope.Traces
            .Where(x => x.DocumentId == "DOC-0205")
            .GroupBy(x => x.SourceId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);

        var entries = references.Select(reference => BuildEntry(reference, modelBySource)).ToArray();
        var counts = entries.GroupBy(x => x.Classification)
            .ToDictionary(x => x.Key, x => x.Count());
        return new A99Doc0205ReconciliationCanaryReport
        {
            ReferenceSource = Path.GetFileName(referenceBridgePath),
            ModelTraceSource = Path.GetFileName(modelTracePath),
            Status = entries.Any(x => x.Classification == A99Doc0205CanaryClassification.Unresolved) ? "BLOCKED_ON_REFERENCE_SPAN_EVIDENCE" : "PASS",
            ReferenceCount = entries.Length,
            ClassificationCounts = counts,
            Entries = entries,
            ProviderCalls = 0,
            ProductionChanged = false,
            RepresentationCanary = RunRepresentabilityCanary(),
            Note = "This evaluation-only canary never changes production behavior. The current retained DOC-0205 reference bridge has no exact heading spans, so the canary must remain UNRESOLVED rather than infer a logical segment from a full source paragraph envelope.",
        };
    }

    /// <summary>
    /// Test-only proof that one physical source occurrence can carry multiple logical headings.
    /// This intentionally uses synthetic spans and never promotes historical references to Gold.
    /// </summary>
    public static A99Doc0205RepresentationReport RunRepresentabilityCanary()
    {
        var sourceId = "body[1]/p[4]";
        var firstSpan = new A99ReviewSpan(0, 9);
        var secondSpan = new A99ReviewSpan(10, 19);
        var parentId = A99HeadingOccurrenceIdentity.Create(sourceId, firstSpan);
        var childId = A99HeadingOccurrenceIdentity.Create(sourceId, secondSpan);
        var gold = new A99HumanGoldV3Document
        {
            DocumentId = "DOC-0205-REPRESENTABILITY",
            DocumentGroupId = "SYNTHETIC",
            Split = "DEV",
            ReviewerAlias = "test-only",
            ReviewedAt = DateTimeOffset.UnixEpoch,
            ReviewVersion = "canary",
            SourceDocumentSha256 = "synthetic",
            PacketSha256 = "synthetic",
            Rows = [
                new A99GoldV3Heading
                {
                    HeadingOccurrenceId = parentId, SourceId = sourceId, StableId = sourceId,
                    SourceOrdinal = 4, SourceSpan = new(0, 19), SourceTextHash = "synthetic",
                    HeadingSpan = firstSpan, Role = "heading", Level = 1, ParentHeadingOccurrenceId = "ROOT",
                },
                new A99GoldV3Heading
                {
                    HeadingOccurrenceId = childId, SourceId = sourceId, StableId = sourceId,
                    SourceOrdinal = 4, SourceSpan = new(0, 19), SourceTextHash = "synthetic",
                    HeadingSpan = secondSpan, Role = "heading", Level = 2, ParentHeadingOccurrenceId = parentId,
                },
            ],
        };
        var metrics = A99PositiveSetEvaluatorV3.Evaluate(gold, [
            new A99PositivePrediction(sourceId, new(firstSpan.Start, firstSpan.End), 1, "heading", "ROOT"),
            new A99PositivePrediction(sourceId, new(secondSpan.Start, secondSpan.End), 2, "heading", parentId),
        ]);
        return new A99Doc0205RepresentationReport
        {
            Status = metrics.TruePositives == 2 && metrics.FalseNegatives == 0 && parentId != childId
                ? "PASS" : "FAIL",
            LogicalHeadingOccurrences = 2,
            DistinctOccurrenceIds = parentId != childId,
            ParentChildSharePhysicalSource = true,
            EvaluatorTruePositives = metrics.TruePositives,
            EvaluatorFalseNegatives = metrics.FalseNegatives,
        };
    }

    private static A99Doc0205CanaryEntry BuildEntry(
        ReferenceRow reference,
        IReadOnlyDictionary<string, ModelTrace[]> modelBySource)
    {
        var exactSpan = reference.ExpectedSpan ?? reference.ReferenceOccurrenceIdentity?.Span;
        var sourceId = reference.ResolvedSourceId;
        modelBySource.TryGetValue(sourceId ?? string.Empty, out var traces);
        traces ??= [];
        var candidateConstructed = exactSpan is null ? null : Any(traces, x => x.CandidateConstructed);
        var candidateSelected = exactSpan is null ? null : Any(traces, x => x.CandidateSelected);
        var modelExposed = exactSpan is null ? null : Any(traces, x => x.ModelExposed);
        var finalLineage = exactSpan is null ? null : Any(traces, x => x.FinalIncluded);
        var requestMembership = (bool?)null;
        var evidence = new A99Doc0205CanaryObservation(
            exactSpan is not null,
            sourceId,
            exactSpan is null ? null : (bool?)null,
            candidateConstructed,
            candidateSelected,
            requestMembership,
            modelExposed,
            finalLineage);
        var classification = Classify(evidence);
        return new A99Doc0205CanaryEntry
        {
            ReferenceId = reference.ReferenceId ?? "UNKNOWN_REFERENCE",
            DocumentId = reference.DocumentId ?? "DOC-0205",
            ReferenceExactSpan = exactSpan,
            ReferenceSpanStatus = exactSpan is null ? "UNRESOLVED" : "PROVEN",
            OwningPhysicalSourceId = sourceId,
            PhysicalSourceStatus = string.IsNullOrWhiteSpace(sourceId) ? "UNRESOLVED" : "PROVEN",
            OwningPhysicalSourceEnvelope = reference.ResolvedSpan,
            LogicalSegmentExists = evidence.LogicalSegmentExists,
            LogicalSegmentStatus = StageStatus(evidence.LogicalSegmentExists, exactSpan is null),
            CandidateConstructed = evidence.CandidateConstructed,
            CandidateConstructionStatus = StageStatus(evidence.CandidateConstructed, exactSpan is null),
            CandidateSelected = evidence.CandidateSelected,
            SelectionStatus = StageStatus(evidence.CandidateSelected, exactSpan is null),
            RequestMembership = evidence.RequestMembership,
            RequestMembershipStatus = StageStatus(evidence.RequestMembership, exactSpan is null),
            ModelExposed = evidence.ModelExposed,
            ModelExposureStatus = StageStatus(evidence.ModelExposed, exactSpan is null),
            FinalLineagePresent = evidence.FinalLineagePresent,
            FinalLineageStatus = StageStatus(evidence.FinalLineagePresent, exactSpan is null),
            Classification = classification,
            Reason = ReasonFor(classification, exactSpan, sourceId),
        };
    }

    private static bool? Any(ModelTrace[] traces, Func<ModelTrace, bool?> selector)
    {
        if (traces.Length == 0) return null;
        var values = traces.Select(selector).ToArray();
        return values.Any(x => x == true) ? true : values.All(x => x is not null) ? false : null;
    }

    private static string StageStatus(bool? value, bool notEvaluated) =>
        notEvaluated ? "NOT_EVALUATED" : value switch
        {
            true => "PROVEN_TRUE",
            false => "PROVEN_FALSE",
            _ => "UNRESOLVED",
        };

    private static string ReasonFor(A99Doc0205CanaryClassification classification, A99ReviewSpan? exactSpan, string? sourceId) =>
        classification switch
        {
            A99Doc0205CanaryClassification.Unresolved when exactSpan is null => "exact reference heading span is not retained; source envelope alone cannot prove a logical segment",
            A99Doc0205CanaryClassification.ProvenSourceReadingLoss => "exact reference exists but no owning physical source was observed",
            A99Doc0205CanaryClassification.ProvenRepresentationLoss => "exact reference span has no owning logical source segment",
            A99Doc0205CanaryClassification.ProvenCandidateConstructionLoss => "exact logical source segment exists but no candidate was constructed",
            A99Doc0205CanaryClassification.ProvenCandidateSelectionLoss => "candidate was constructed but was not selected",
            A99Doc0205CanaryClassification.ProvenModelNotExposed => "selected candidate did not expose a model request/result for the reference occurrence",
            A99Doc0205CanaryClassification.ProvenFinalProjectionLoss => "model/selection evidence exists but final lineage is absent",
            A99Doc0205CanaryClassification.NoProvenPremodelLoss => "all canary stages are proven and final lineage is present",
            _ => $"stage evidence is incomplete for source {sourceId ?? "UNKNOWN"}",
        };

    private sealed class ReferenceEnvelope
    {
        [JsonPropertyName("occurrences")] public List<ReferenceRow> Occurrences { get; set; } = [];
    }

    private sealed class ReferenceRow
    {
        public string? ReferenceId { get; set; }
        public string? DocumentId { get; set; }
        public ReferenceIdentity? ReferenceOccurrenceIdentity { get; set; }
        public string? ResolvedSourceId { get; set; }
        public A99ReviewSpan? ResolvedSpan { get; set; }
        public A99ReviewSpan? ExpectedSpan { get; set; }
    }

    private sealed class ReferenceIdentity
    {
        public A99ReviewSpan? Span { get; set; }
    }

    private sealed class ModelEnvelope
    {
        public List<ModelTrace> Traces { get; set; } = [];
    }

    private sealed class ModelTrace
    {
        public string DocumentId { get; set; } = "";
        public string SourceId { get; set; } = "";
        public bool? CandidateConstructed { get; set; }
        public bool? CandidateSelected { get; set; }
        public bool ModelExposed { get; set; }
        public bool FinalIncluded { get; set; }
    }
}
