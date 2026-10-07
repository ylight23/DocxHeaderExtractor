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

/// <summary>
/// Outline payload used only while the existing HeadingRecord API remains public. It keeps
/// projection details out of source/span validation logic while allowing a lossless heading projection.
/// </summary>
public sealed record StructuralProjectionMetadata
{
    /// <summary>Outline output identity when it differs from the generic source identity.</summary>
    public string? OutlineSourceId { get; init; }
    /// <summary>Outline heading index when it differs from the generic source ordinal.</summary>
    public int? OutlineSourceOrdinal { get; init; }
    /// <summary>Outline heading stable identity when it differs from the generic source identity.</summary>
    public string? OutlineStableId { get; init; }
    /// <summary>Outline heading span when it differs from the validated source span.</summary>
    public StructuralSpan? OutlineHeadingSpan { get; init; }
    /// <summary>Outline heading text when the generic source unit contains a wider observed block.</summary>
    public string? OutlineText { get; init; }
    /// <summary>Outline output level, including an intentional null value.</summary>
    [JsonIgnore]
    public int? OutlineLevel { get; init; }
    /// <summary>Whether the outline level should override the materialized heading level.</summary>
    [JsonIgnore]
    public bool OutlineLevelIsSet { get; init; }
    /// <summary>
    /// Why this heading has, or does not have, a level. "model-out-of-hierarchy" is a decision —
    /// a title or running header that holds no position in the section tree — while "unresolved"
    /// is the absence of one and belongs in a review queue. Both end with a null level, so the
    /// reason is the only thing that tells them apart.
    /// </summary>
    public string? HierarchyResolution { get; init; }
    public string? OriginalText { get; init; }
    public string? InlineBody { get; init; }
    public StructuralSpan? InlineBodySpan { get; init; }
    public string? BoundarySource { get; init; }
    public string? StyleId { get; init; }
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

    [JsonPropertyName("parentId")]
    public string? ParentId { get; init; }

    [JsonPropertyName("validation")]
    public required StructuralValidation Validation { get; init; }

    [JsonPropertyName("decision")]
    public required StructuralDecision Decision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StructuralProjectionMetadata? ProjectionMetadata { get; init; }
}
