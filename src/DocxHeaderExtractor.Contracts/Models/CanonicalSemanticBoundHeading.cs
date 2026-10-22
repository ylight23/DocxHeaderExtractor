using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

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
