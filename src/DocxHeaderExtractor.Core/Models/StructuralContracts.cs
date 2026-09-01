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
    /// <summary>Optional stable source identity retained for compatibility projections.</summary>
    [JsonPropertyName("stableId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StableId { get; init; }
}

/// <summary>Untrusted source/span selection proposed by a model or visual boundary pass.</summary>
public sealed record ProposedSourceReference(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("span")] StructuralSpan Span);

/// <summary>
/// A parser/deterministic candidate. Its source facts and observed spans are authority inputs; a
/// proposal may refer to this candidate but cannot replace its source facts.
/// </summary>
public sealed record StructuralCandidate
{
    [JsonPropertyName("candidateId")]
    public required string CandidateId { get; init; }

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
/// Untrusted structural proposal. CandidateId is a routing key; type, role, proposed span,
/// parent, and level remain subject to validation against the candidate's observed facts.
/// </summary>
public sealed record StructuralProposal
{
    [JsonPropertyName("candidateId")]
    public required string CandidateId { get; init; }

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

/// <summary>Generic decision metadata carried after validation, independent of heading output.</summary>
public sealed record StructuralDecision(
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("confidenceBasis")] string ConfidenceBasis,
    [property: JsonPropertyName("disputed")] bool Disputed = false);

public sealed record StructuralValidation(
    [property: JsonPropertyName("candidateGrounded")] bool CandidateGrounded,
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
/// Compatibility payload used only while the existing HeadingRecord API remains public. It keeps
/// projection details out of generic validation logic while allowing a lossless heading projection.
/// </summary>
public sealed record StructuralProjectionMetadata
{
    /// <summary>Legacy output identity when it differs from the generic source identity.</summary>
    public string? CompatibilitySourceId { get; init; }
    /// <summary>Legacy output level, including an intentional null value.</summary>
    [JsonIgnore]
    public int? CompatibilityLevel { get; init; }
    /// <summary>Whether the compatibility level should override the generic structural level.</summary>
    [JsonIgnore]
    public bool CompatibilityLevelIsSet { get; init; }
    public string? OriginalText { get; init; }
    public string? InlineBody { get; init; }
    public StructuralSpan? InlineBodySpan { get; init; }
    public string? BoundarySource { get; init; }
    public string? StyleId { get; init; }
    public bool ModelConfirmed { get; init; }
    public bool CriticConfirmed { get; init; }
    public string? AcceptanceSignature { get; init; }
    public int CalibrationSamples { get; init; }
    public HeadingEvidence? Evidence { get; init; }
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
/// Validates relation proposals before they enter the structural authority graph. This contract is
/// intentionally open to additional relation types without making ParentId a second authority.
/// </summary>
public static class StructuralRelationProposalValidator
{
    public static StructuralRelationValidation Validate(
        StructuralRelationProposal proposal,
        IReadOnlySet<string> knownStructuralElementIds,
        IReadOnlyDictionary<string, StructuralElementType>? structuralElementTypes = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(knownStructuralElementIds);
        var endpointsPresent = knownStructuralElementIds.Contains(proposal.FromId) &&
            knownStructuralElementIds.Contains(proposal.ToId);
        var distinctEndpoints = !string.Equals(proposal.FromId, proposal.ToId, StringComparison.Ordinal);
        var typeValid = Enum.IsDefined(proposal.Type);
        var semanticValid = structuralElementTypes is null || !endpointsPresent ||
            IsSemanticallyCompatible(proposal, structuralElementTypes);
        var reason = !endpointsPresent ? "relation-endpoint-not-grounded" :
            !distinctEndpoints ? "relation-self-reference" :
            !typeValid ? "relation-type-unsupported" :
            !semanticValid ? "relation-type-incompatible" : null;
        return new StructuralRelationValidation(endpointsPresent, distinctEndpoints, typeValid, reason);
    }

    public static IReadOnlyList<StructuralRelation> Materialize(
        IReadOnlySet<string> knownStructuralElementIds,
        IEnumerable<StructuralRelationProposal> proposals,
        IReadOnlyDictionary<string, StructuralElementType>? structuralElementTypes = null)
    {
        ArgumentNullException.ThrowIfNull(knownStructuralElementIds);
        ArgumentNullException.ThrowIfNull(proposals);
        var relations = new List<StructuralRelation>();
        var seen = new HashSet<(string FromId, string ToId, StructuralRelationType Type)>();
        var parentByChild = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var proposal in proposals)
        {
            var validation = Validate(proposal, knownStructuralElementIds, structuralElementTypes);
            if (!validation.Accepted)
                throw new InvalidOperationException(validation.RejectionReason);

            if (proposal.Type == StructuralRelationType.ParentChild &&
                parentByChild.TryGetValue(proposal.ToId, out var existingParent) &&
                !string.Equals(existingParent, proposal.FromId, StringComparison.Ordinal))
                throw new InvalidOperationException("multiple-parent-relations");

            parentByChild[proposal.ToId] = proposal.FromId;
            if (seen.Add((proposal.FromId, proposal.ToId, proposal.Type)))
                relations.Add(new StructuralRelation(proposal.FromId, proposal.ToId, proposal.Type));
        }
        return relations;
    }

    private static bool IsSemanticallyCompatible(
        StructuralRelationProposal proposal,
        IReadOnlyDictionary<string, StructuralElementType> structuralElementTypes)
    {
        if (!structuralElementTypes.TryGetValue(proposal.FromId, out var from) ||
            !structuralElementTypes.TryGetValue(proposal.ToId, out var to))
            return false;

        return proposal.Type switch
        {
            StructuralRelationType.ParentChild => true,
            StructuralRelationType.CaptionOf => from == StructuralElementType.Caption &&
                to == StructuralElementType.Figure,
            StructuralRelationType.Labels =>
                (from == StructuralElementType.FigureTitle && to == StructuralElementType.Figure) ||
                (from == StructuralElementType.TableTitle && to == StructuralElementType.Table),
            _ => false,
        };
    }
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
        // ParentId is a compatibility view. It is always projected from the validated graph.
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
    /// intentionally included so the compatibility projection does not silently drop them while
    /// the generic taxonomy remains closed to the initial three element types.
    /// </summary>
    public IReadOnlyList<ValidatedStructuralElement> OutlineElements =>
        Elements.Where(element => element.Type is StructuralElementType.Title or
            StructuralElementType.Subtitle or StructuralElementType.Heading).ToArray();

    [Obsolete("Use OutlineElements for compatibility projection; this legacy view is heading-only.")]
    public IReadOnlyList<ValidatedStructuralElement> Headings =>
        Elements.Where(element => element.Type == StructuralElementType.Heading).ToArray();

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
