using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public enum GoldSourceLabel
{
    [JsonStringEnumMemberName("HEADING")] Heading,
    [JsonStringEnumMemberName("NON_HEADING")] NonHeading,
    [JsonStringEnumMemberName("AMBIGUOUS")] Ambiguous,
    [JsonStringEnumMemberName("NOT_OBSERVABLE")] NotObservable,
}

public enum Accuracy99FirstLossStage
{
    [JsonStringEnumMemberName("SOURCE_READING")] SourceReading,
    [JsonStringEnumMemberName("CANDIDATE_GENERATION")] CandidateGeneration,
    [JsonStringEnumMemberName("PROPOSAL")] Proposal,
    [JsonStringEnumMemberName("VALIDATION")] Validation,
    [JsonStringEnumMemberName("SPAN")] Span,
    [JsonStringEnumMemberName("LEVEL")] Level,
    [JsonStringEnumMemberName("PARENT")] Parent,
    [JsonStringEnumMemberName("FINAL_PROJECTION")] FinalProjection,
    [JsonStringEnumMemberName("UNMEASURED")] Unmeasured,
}

public sealed record Accuracy99Span(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End)
{
    public bool IsValidFor(string text) => Start >= 0 && End >= Start && End <= text.Length;
}

public sealed record HumanGoldSourceOccurrence
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public int SourceOrdinal { get; init; }
    [JsonPropertyName("span")] public required Accuracy99Span Span { get; init; }
    [JsonPropertyName("label")] public required GoldSourceLabel Label { get; init; }
}

public sealed record HumanGoldHeading
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("headingSpan")] public required Accuracy99Span HeadingSpan { get; init; }
    [JsonPropertyName("level")] public required int Level { get; init; }
    [JsonPropertyName("parentSourceId")] public string? ParentSourceId { get; init; }
}

/// <summary>
/// General-heading human annotation. This contract deliberately has no prediction fields and
/// treats source occurrences as the exhaustive review universe when ExhaustiveSourceLabels is true.
/// </summary>
public sealed record HumanGoldArtifact
{
    [JsonPropertyName("artifactKind")] public required string ArtifactKind { get; init; }
    [JsonPropertyName("authorityClass")] public required string AuthorityClass { get; init; }
    [JsonPropertyName("reviewerId")] public required string ReviewerId { get; init; }
    [JsonPropertyName("adjudicationVersion")] public required string AdjudicationVersion { get; init; }
    [JsonPropertyName("createdUtc")] public required DateTimeOffset CreatedUtc { get; init; }
    [JsonPropertyName("sourceDocumentSha256")] public required string SourceDocumentSha256 { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("mediaType")] public required string MediaType { get; init; }
    [JsonPropertyName("split")] public string Split { get; init; } = "UNASSIGNED";
    [JsonPropertyName("exhaustiveSourceLabels")] public bool ExhaustiveSourceLabels { get; init; }
    [JsonPropertyName("sourceOccurrences")] public IReadOnlyList<HumanGoldSourceOccurrence> SourceOccurrences { get; init; } = [];
    [JsonPropertyName("headings")] public IReadOnlyList<HumanGoldHeading> Headings { get; init; } = [];
}

public sealed record Accuracy99Prediction(
    string SourceId,
    Accuracy99Span? Span,
    int? Level,
    string? Text,
    string? OriginalText = null,
    string? BoundarySource = null,
    string? ParentSourceId = null,
    bool HasParent = false,
    double? Confidence = null);

public sealed record Accuracy99DocumentMetrics
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("truePositives")] public int TruePositives { get; init; }
    [JsonPropertyName("falsePositives")] public int FalsePositives { get; init; }
    [JsonPropertyName("falseNegatives")] public int FalseNegatives { get; init; }
    [JsonPropertyName("precision")] public double? Precision { get; init; }
    [JsonPropertyName("recall")] public double? Recall { get; init; }
    [JsonPropertyName("f1")] public double? F1 { get; init; }
    [JsonPropertyName("exactSpanMatches")] public int ExactSpanMatches { get; init; }
    [JsonPropertyName("spanEvaluated")] public int SpanEvaluated { get; init; }
    [JsonPropertyName("levelCorrect")] public int LevelCorrect { get; init; }
    [JsonPropertyName("levelEvaluated")] public int LevelEvaluated { get; init; }
    [JsonPropertyName("parentCorrect")] public int ParentCorrect { get; init; }
    [JsonPropertyName("parentEvaluated")] public int ParentEvaluated { get; init; }
    [JsonPropertyName("hierarchyCorrect")] public int HierarchyCorrect { get; init; }
    [JsonPropertyName("hierarchyEvaluated")] public int HierarchyEvaluated { get; init; }
    [JsonPropertyName("sourceUnjoined")] public int SourceUnjoined { get; init; }
    [JsonPropertyName("unmeasured")] public int Unmeasured { get; init; }
    [JsonPropertyName("firstLosses")] public IReadOnlyDictionary<Accuracy99FirstLossStage, int> FirstLosses { get; init; } = new Dictionary<Accuracy99FirstLossStage, int>();
    [JsonPropertyName("documentExactMatch")] public bool? DocumentExactMatch { get; init; }
}

public sealed record Accuracy99AggregateMetrics
{
    [JsonPropertyName("documentCount")] public int DocumentCount { get; init; }
    [JsonPropertyName("micro")] public required Accuracy99DocumentMetrics Micro { get; init; }
    [JsonPropertyName("macroPrecision")] public double? MacroPrecision { get; init; }
    [JsonPropertyName("macroRecall")] public double? MacroRecall { get; init; }
    [JsonPropertyName("macroF1")] public double? MacroF1 { get; init; }
    [JsonPropertyName("documents")] public IReadOnlyList<Accuracy99DocumentMetrics> Documents { get; init; } = [];
}

public sealed record BlindSourceStyleFacts
{
    [JsonPropertyName("styleId")] public string? StyleId { get; init; }
    [JsonPropertyName("styleName")] public string? StyleName { get; init; }
    [JsonPropertyName("bold")] public bool Bold { get; init; }
    [JsonPropertyName("italic")] public bool Italic { get; init; }
    [JsonPropertyName("underline")] public bool Underline { get; init; }
    [JsonPropertyName("allCaps")] public bool AllCaps { get; init; }
    [JsonPropertyName("fontSizePt")] public double? FontSizePt { get; init; }
    [JsonPropertyName("alignment")] public string? Alignment { get; init; }
}

public sealed record BlindSourceNumberingFacts
{
    [JsonPropertyName("numberingId")] public int? NumberingId { get; init; }
    [JsonPropertyName("numberLabel")] public string? NumberLabel { get; init; }
    [JsonPropertyName("numberingFormat")] public string? NumberingFormat { get; init; }
}

public sealed record BlindSourceLayoutFacts
{
    [JsonPropertyName("inContentControl")] public bool InContentControl { get; init; }
    [JsonPropertyName("keepNext")] public bool KeepNext { get; init; }
    [JsonPropertyName("pageBreakBefore")] public bool PageBreakBefore { get; init; }
    [JsonPropertyName("tableDepth")] public int TableDepth { get; init; }
    [JsonPropertyName("sectionIndex")] public int SectionIndex { get; init; }
}

public sealed record BlindSourceOccurrence
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public int SourceOrdinal { get; init; }
    [JsonPropertyName("rawText")] public required string RawText { get; init; }
    [JsonPropertyName("fullSpan")] public required Accuracy99Span FullSpan { get; init; }
    [JsonPropertyName("style")] public required BlindSourceStyleFacts Style { get; init; }
    [JsonPropertyName("numbering")] public required BlindSourceNumberingFacts Numbering { get; init; }
    [JsonPropertyName("layout")] public required BlindSourceLayoutFacts Layout { get; init; }
}

public sealed record BlindSourcePacket
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "blind_source_packet";
    [JsonPropertyName("authorityClass")] public string AuthorityClass { get; init; } = "PARSER_SOURCE_FACTS";
    [JsonPropertyName("packetVersion")] public string PacketVersion { get; init; } = "accuracy99-blind-v1";
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("fileName")] public required string FileName { get; init; }
    [JsonPropertyName("sourceKind")] public required string SourceKind { get; init; }
    [JsonPropertyName("sourceDocumentSha256")] public required string SourceDocumentSha256 { get; init; }
    [JsonPropertyName("occurrences")] public required IReadOnlyList<BlindSourceOccurrence> Occurrences { get; init; }
}

public enum Accuracy99DatasetClassification
{
    [JsonStringEnumMemberName("HUMAN_GOLD")] HumanGold,
    [JsonStringEnumMemberName("SILVER_ONLY")] SilverOnly,
    [JsonStringEnumMemberName("UNLABELED")] Unlabeled,
    [JsonStringEnumMemberName("INVALID_SOURCE")] InvalidSource,
}

public sealed record Accuracy99DatasetEntry
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("classification")] public required Accuracy99DatasetClassification Classification { get; init; }
    [JsonPropertyName("goldPath")] public string? GoldPath { get; init; }
    [JsonPropertyName("split")] public string Split { get; init; } = "UNASSIGNED";
    [JsonPropertyName("contentGroup")] public required string ContentGroup { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed record Accuracy99DatasetInventory
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "accuracy99_dataset_inventory";
    [JsonPropertyName("freezeStatus")] public required string FreezeStatus { get; init; }
    [JsonPropertyName("root")] public required string Root { get; init; }
    [JsonPropertyName("humanGoldCount")] public int HumanGoldCount { get; init; }
    [JsonPropertyName("silverOnlyCount")] public int SilverOnlyCount { get; init; }
    [JsonPropertyName("unlabeledCount")] public int UnlabeledCount { get; init; }
    [JsonPropertyName("invalidSourceCount")] public int InvalidSourceCount { get; init; }
    [JsonPropertyName("duplicateContentGroups")] public int DuplicateContentGroups { get; init; }
    [JsonPropertyName("entries")] public required IReadOnlyList<Accuracy99DatasetEntry> Entries { get; init; }
}

public sealed record HumanGoldValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public void ThrowIfInvalid()
    {
        if (!IsValid)
            throw new InvalidDataException(string.Join("; ", Errors));
    }
}
