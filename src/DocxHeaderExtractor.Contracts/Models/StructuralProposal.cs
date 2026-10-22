using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>
/// Untrusted heading materialization proposal. SourceOccurrenceId is the source occurrence routing key; type, role,
/// proposed span, parent, and level remain subject to validation against observed source facts.
/// </summary>
public sealed record StructuralProposal
{
    [JsonPropertyName("sourceOccurrenceId")]
    public required string SourceOccurrenceId { get; init; }

    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required StructuralElementType Type { get; init; }

    [JsonPropertyName("role")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ProposedRole Role { get; init; }

    [JsonPropertyName("proposedSources")]
    public IReadOnlyList<ProposedSourceReference>? ProposedSources { get; init; }

    [JsonPropertyName("proposedParentId")]
    public string? ProposedParentId { get; init; }

    [JsonPropertyName("proposedLevel")]
    public int? ProposedLevel { get; init; }
}
