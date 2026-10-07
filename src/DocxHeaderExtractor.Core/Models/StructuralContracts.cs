using DocxHeaderExtractor.Core.Semantics.Validation;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Structural taxonomy currently supported by the generic authority contract.</summary>
public enum StructuralElementType
{
    Title,
    Subtitle,
    Heading,
    ListItem,
    Caption,
    TableTitle,
    FigureTitle,
    Figure,
    Table,
}

public enum StructuralRelationType
{
    ParentChild,
    CaptionOf,
    Labels,
}

/// <summary>Exact source-text coordinates. End is exclusive, matching <see cref="TextOffsetSpan"/>.</summary>
public sealed record StructuralSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End)
{
    public bool IsValidFor(string text) => Start >= 0 && End > Start && End <= text.Length;
}

/// <summary>Observed source identity and parser-owned text span.</summary>
public sealed record SourceReference(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("span")] StructuralSpan Span)
{
/// <summary>Optional stable source identity retained for outline projections.</summary>
    [JsonPropertyName("stableId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StableId { get; init; }
}

/// <summary>Untrusted source/span selection proposed by a model or visual boundary pass.</summary>
public sealed record ProposedSourceReference(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("span")] StructuralSpan Span);

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

    [JsonPropertyName("observedEvidence")]
    public IReadOnlyList<ObservedEvidence> ObservedEvidence { get; init; } = [];
}

/// <summary>
/// Untrusted structural proposal. SourceOccurrenceId is the source occurrence routing key; type, role,
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

/// <summary>Generic decision metadata carried after validation, independent of heading output.</summary>
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
/// projection details out of generic validation logic while allowing a lossless heading projection.
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
    /// <summary>Whether the outline level should override the generic structural level.</summary>
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

/// <summary>Source-grounded structural element consumed by relations and downstream projections.</summary>
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

public sealed record StructuralRelation(
    [property: JsonPropertyName("fromId")] string FromId,
    [property: JsonPropertyName("toId")] string ToId,
    [property: JsonPropertyName("type")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StructuralRelationType Type);

/// <summary>Untrusted relation proposal. Endpoints are structural element IDs, never source IDs.</summary>
public sealed record StructuralRelationProposal(
    [property: JsonPropertyName("fromId")] string FromId,
    [property: JsonPropertyName("toId")] string ToId,
    [property: JsonPropertyName("type")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StructuralRelationType Type);

public sealed record StructuralRelationValidation(
    [property: JsonPropertyName("endpointsPresent")] bool EndpointsPresent,
    [property: JsonPropertyName("distinctEndpoints")] bool DistinctEndpoints,
    [property: JsonPropertyName("typeValid")] bool TypeValid,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason)
{
    [JsonIgnore]
    public bool Accepted => RejectionReason is null;
}

/// <summary>
/// The generic structural authority graph. It contains only validated elements and relations;
/// heading-specific consumers enter through a projection.
/// </summary>
public sealed class ValidatedStructure
{
    public ValidatedStructure(
        IReadOnlyList<ValidatedStructuralElement> elements,
        IReadOnlyList<StructuralRelation>? relations = null)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var materialized = elements.ToArray();
        var ids = materialized.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != materialized.Length)
            throw new InvalidOperationException("duplicate-structural-element-id");
        var proposedRelations = (relations ?? [])
            .Select(relation => new StructuralRelationProposal(relation.FromId, relation.ToId, relation.Type));
        var types = materialized.ToDictionary(element => element.Id, element => element.Type, StringComparer.Ordinal);
        Relations = StructuralRelationProposalValidator.Materialize(ids, proposedRelations, types);
        var parentByChild = Relations
            .Where(relation => relation.Type == StructuralRelationType.ParentChild)
            .ToDictionary(relation => relation.ToId, relation => relation.FromId, StringComparer.Ordinal);
        // ParentId is an outline view. It is always projected from the validated graph.
        Elements = materialized.Select(element => element with
        {
            ParentId = parentByChild.GetValueOrDefault(element.Id),
        }).ToArray();
    }

    [JsonPropertyName("elements")]
    public IReadOnlyList<ValidatedStructuralElement> Elements { get; }

    [JsonPropertyName("relations")]
    public IReadOnlyList<StructuralRelation> Relations { get; }

    /// <summary>
    /// Elements that the existing document-outline contract can represent. Title and Subtitle are
    /// intentionally included so the outline projection does not silently drop them while
    /// the generic taxonomy remains closed to the initial three element types.
    /// </summary>
    public IReadOnlyList<ValidatedStructuralElement> OutlineElements =>
        Elements.Where(element => element.Type is StructuralElementType.Title or
            StructuralElementType.Subtitle or StructuralElementType.Heading).ToArray();

    public static ValidatedStructure FromElements(
        IEnumerable<ValidatedStructuralElement> elements,
        IEnumerable<StructuralRelationProposal>? relationProposals = null)
    {
        var materialized = elements?.ToArray() ?? throw new ArgumentNullException(nameof(elements));
        var ids = materialized.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        var proposals = relationProposals?.ToArray() ?? materialized
            .Where(element => element.ParentId is not null)
            .Select(element => new StructuralRelationProposal(
                element.ParentId!, element.Id, StructuralRelationType.ParentChild))
            .ToArray();
        var types = materialized.ToDictionary(element => element.Id, element => element.Type, StringComparer.Ordinal);
        var relations = StructuralRelationProposalValidator.Materialize(ids, proposals, types);
        return new ValidatedStructure(materialized, relations);
    }
}
