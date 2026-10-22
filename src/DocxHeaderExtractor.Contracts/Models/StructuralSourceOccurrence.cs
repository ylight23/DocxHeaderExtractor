using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

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
}
