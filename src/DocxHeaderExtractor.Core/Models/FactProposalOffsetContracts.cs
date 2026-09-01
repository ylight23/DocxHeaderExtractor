using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>One exact UTF-16 coordinate in a source excerpt. End is exclusive.</summary>
public sealed record FactTextOffset(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End,
    [property: JsonPropertyName("text")] string Text);

/// <summary>Source text plus an offset-safe, non-normalized coordinate map for a model prompt.</summary>
public sealed record FactProposalOffsetSource(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("offsets")] IReadOnlyList<FactTextOffset> Offsets);
