using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>
/// Parser-owned address used by the semantic contract. The model may name this address, but it
/// cannot manufacture coordinates or source text outside the address.
/// </summary>
public sealed record SemanticSourceAlias(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sourceSpan")] StructuralSpan SourceSpan,
    [property: JsonIgnore] SourceAnchor? SourceAnchor = null);
