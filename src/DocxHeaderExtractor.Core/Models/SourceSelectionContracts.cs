using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Exact source-text coordinates. End is exclusive, matching <see cref="TextOffsetSpan"/>.</summary>
public sealed record StructuralSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End)
{
    public bool IsValidFor(string text) => Start >= 0 && End > Start && End <= text.Length;
}

/// <summary>Observed source identity and parser-owned text span.</summary>
public sealed record SourceReference(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("span")] StructuralSpan Span)
{
    /// <summary>Optional stable source identity retained for outline projections.</summary>
    [JsonPropertyName("stableId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StableId { get; init; }
}

/// <summary>Untrusted source/span selection checked by the materialization gate.</summary>
public sealed record ProposedSourceReference(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("span")] StructuralSpan Span);

/// <summary>
/// A parser/deterministic source occurrence. Its source facts and observed spans are authority
/// inputs; a proposal may refer to this routing id but cannot replace its source facts.
/// </summary>
public sealed record StructuralSourceOccurrence
{
    [JsonPropertyName("sourceOccurrenceId")]
    public required string SourceOccurrenceId { get; init; }

    [JsonIgnore]
    public required IReadOnlyList<SourceFacts> ObservedSourceFacts { get; init; }

    [JsonPropertyName("observedSources")]
    public IReadOnlyList<SourceReference> ObservedSources => ObservedSourceFacts
        .Select((facts, index) => new SourceReference(
            facts.SourceId,
            facts.Source.ParagraphIndex ?? index,
            new StructuralSpan(facts.RawSpan.Start, facts.RawSpan.End)))
        .ToArray();

    [JsonPropertyName("observedEvidence")]
    public IReadOnlyList<ObservedEvidence> ObservedEvidence { get; init; } = [];
}
