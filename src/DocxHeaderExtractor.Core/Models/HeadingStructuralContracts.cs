using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Production materialization admits headings only. The historical numeric identity is retained.</summary>
public enum StructuralElementType
{
    Heading = 2,
}

/// <summary>Role of a materialized heading; distinct from the model's upstream semantic-role vocabulary.</summary>
public enum ProposedRole
{
    HeadingTopic = 0,
}

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

/// <summary>Who produced a structural decision. The producing lane declares it; nothing infers it.</summary>
public static class StructuralDecisionOrigin
{
    /// <summary>A model claim that bound to the source and passed validation.</summary>
    public const string Model = "model";

    /// <summary>A reviewer's correction applied to the exact source paragraph.</summary>
    public const string HumanCorrection = "human-correction";
}

/// <summary>Decision metadata retained after heading validation.</summary>
public sealed record StructuralDecision(
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("confidenceBasis")] string ConfidenceBasis,
    [property: JsonPropertyName("disputed")] bool Disputed = false);

public sealed record StructuralValidation(
    [property: JsonPropertyName("sourceOccurrenceGrounded")] bool SourceOccurrenceGrounded,
    [property: JsonPropertyName("sourceFactsPresent")] bool SourceFactsPresent,
    [property: JsonPropertyName("proposedSpanValid")] bool ProposedSpanValid,
    [property: JsonPropertyName("sourceSelectionValid")] bool SourceSelectionValid,
    [property: JsonPropertyName("validatedSourceCount")] int ValidatedSourceCount,
    [property: JsonPropertyName("typeValid")] bool TypeValid,
    [property: JsonPropertyName("levelValid")] bool LevelValid,
    [property: JsonPropertyName("parentValid")] bool ParentValid,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason,
    [property: JsonPropertyName("typeRoleValid")] bool TypeRoleValid = true)
{
    [JsonIgnore]
    public bool Accepted => RejectionReason is null;
}

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
