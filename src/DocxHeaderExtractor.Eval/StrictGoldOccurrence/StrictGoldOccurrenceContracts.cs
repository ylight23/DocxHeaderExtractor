using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.StrictGoldOccurrence;

public sealed record StrictGoldOccurrenceSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End)
{
    public bool IsValidFor(string text) => Start >= 0 && End > Start && End <= text.Length;
}

public sealed record StrictGoldOccurrenceBinding
{
    [JsonPropertyName("headingOrdinal")] public required int HeadingOrdinal { get; init; }
    [JsonPropertyName("headingOccurrenceId")] public required string HeadingOccurrenceId { get; init; }
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("headingSpan")] public required StrictGoldOccurrenceSpan HeadingSpan { get; init; }
    [JsonPropertyName("rawSourceText")] public required string RawSourceText { get; init; }
    [JsonPropertyName("approvedHeadingText")] public required string ApprovedHeadingText { get; init; }
    [JsonPropertyName("semanticRole")] public required string SemanticRole { get; init; }
    [JsonPropertyName("level")] public int? Level { get; init; }
    [JsonPropertyName("parentHeadingOccurrenceId")] public string? ParentHeadingOccurrenceId { get; init; }
    [JsonPropertyName("bindingMethod")] public required string BindingMethod { get; init; }
    [JsonPropertyName("comparisonRule")] public required string ComparisonRule { get; init; }
    [JsonPropertyName("sourceReferencePath")] public required string SourceReferencePath { get; init; }
    [JsonPropertyName("sourceReferenceSha256")] public required string SourceReferenceSha256 { get; init; }
    [JsonPropertyName("exactRawSubstringVerified")] public bool ExactRawSubstringVerified { get; init; }
}

public sealed record StrictGoldOccurrenceCapabilities
{
    [JsonPropertyName("occurrenceEvaluable")] public bool OccurrenceEvaluable { get; init; }
    [JsonPropertyName("characterSpanEvaluable")] public bool CharacterSpanEvaluable { get; init; }
    [JsonPropertyName("roleEvaluable")] public bool RoleEvaluable { get; init; }
    [JsonPropertyName("levelEvaluable")] public bool LevelEvaluable { get; init; }
    [JsonPropertyName("parentEvaluable")] public bool ParentEvaluable { get; init; }
    [JsonPropertyName("hierarchyEvaluable")] public bool HierarchyEvaluable { get; init; }
}

public sealed record StrictGoldOccurrenceArtifact
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_strict_gold_occurrence_binding";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-strict-gold-occurrence-v1";
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public required string DocumentGroupId { get; init; }
    [JsonPropertyName("sourceSha256")] public required string SourceSha256 { get; init; }
    [JsonPropertyName("strictGoldArtifactPath")] public required string StrictGoldArtifactPath { get; init; }
    [JsonPropertyName("strictGoldArtifactSha256")] public required string StrictGoldArtifactSha256 { get; init; }
    [JsonPropertyName("semanticHeadingTotal")] public int SemanticHeadingTotal { get; init; }
    [JsonPropertyName("materializedOccurrenceCount")] public int MaterializedOccurrenceCount { get; init; }
    [JsonPropertyName("bindings")] public IReadOnlyList<StrictGoldOccurrenceBinding> Bindings { get; init; } = [];
    [JsonPropertyName("discrepancies")] public IReadOnlyList<string> Discrepancies { get; init; } = [];
    [JsonPropertyName("capabilities")] public required StrictGoldOccurrenceCapabilities Capabilities { get; init; }
}

public sealed record StrictGoldOccurrenceDocumentSummary
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("expected")] public int Expected { get; init; }
    [JsonPropertyName("materialized")] public int Materialized { get; init; }
    [JsonPropertyName("ambiguous")] public int Ambiguous { get; init; }
    [JsonPropertyName("missing")] public int Missing { get; init; }
    [JsonPropertyName("occurrenceEvaluable")] public bool OccurrenceEvaluable { get; init; }
    [JsonPropertyName("characterSpanEvaluable")] public bool CharacterSpanEvaluable { get; init; }
    [JsonPropertyName("parentEvaluable")] public bool ParentEvaluable { get; init; }
    [JsonPropertyName("hierarchyEvaluable")] public bool HierarchyEvaluable { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("discrepancies")] public IReadOnlyList<string> Discrepancies { get; init; } = [];
}

public sealed record StrictGoldOccurrenceMaterializationReport
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_strict_gold_occurrence_materialization";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-strict-gold-occurrence-materialization-v1";
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("expectedDocuments")] public int ExpectedDocuments { get; init; }
    [JsonPropertyName("materializedDocuments")] public int MaterializedDocuments { get; init; }
    [JsonPropertyName("expectedOccurrences")] public int ExpectedOccurrences { get; init; }
    [JsonPropertyName("materializedOccurrences")] public int MaterializedOccurrences { get; init; }
    [JsonPropertyName("perDocument")] public required IReadOnlyList<StrictGoldOccurrenceDocumentSummary> PerDocument { get; init; }
}
