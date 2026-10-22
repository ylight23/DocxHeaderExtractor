using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>Source-grounded heading consumed by hierarchy and downstream projections.</summary>
public sealed record ValidatedStructuralElement
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required StructuralElementType Type { get; init; }

    [JsonPropertyName("role")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ProposedRole Role { get; init; }

    [JsonPropertyName("sources")]
    public required IReadOnlyList<SourceReference> Sources { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("level")]
    public int? Level { get; init; }

    /// <summary>
    /// Public/wire compatibility view derived from validated ParentChild relations by the graph factory.
    /// Incoming DTO values are ignored on graph admission; use explicit relation proposals instead.
    /// A standalone deserialized element is not an admitted graph and cannot grant parent authority.
    /// </summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; init; }

    [JsonPropertyName("validation")]
    public required StructuralValidation Validation { get; init; }

    [JsonPropertyName("decision")]
    public required StructuralDecision Decision { get; init; }
}
