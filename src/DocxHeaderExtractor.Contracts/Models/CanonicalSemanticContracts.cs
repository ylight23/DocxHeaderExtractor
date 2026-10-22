using System.Text.Json.Serialization;
using DocxHeaderExtractor.Contracts.Models.Inference;

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

public sealed record CanonicalSemanticBoundHeading(
    string Alias,
    string SourceId,
    int SourceOrdinal,
    string Text,
    string SemanticRole,
    string StructuralType,
    string Scope,
    IReadOnlyList<string> RelationHints,
    int Start,
    int End,
    bool IsHeading = true)
{
    /// <summary>All exact source pieces for a composite heading, in model-declared order.</summary>
    [JsonPropertyName("parts")]
    public IReadOnlyList<CanonicalSemanticBoundPart> Parts { get; init; } = [];
}

public sealed record CanonicalSemanticBoundPart(
    string Alias,
    string SourceId,
    int SourceOrdinal,
    string Text,
    int Start,
    int End);

