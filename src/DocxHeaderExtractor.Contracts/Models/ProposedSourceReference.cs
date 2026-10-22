using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>Untrusted source/span selection checked by the materialization gate.</summary>
public sealed record ProposedSourceReference(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("span")] StructuralSpan Span);
