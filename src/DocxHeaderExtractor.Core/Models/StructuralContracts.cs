using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Closed set for the first structural-authority migration.</summary>
public enum StructuralElementType
{
    Title,
    Subtitle,
    Heading,
}

public enum StructuralRelationType
{
    ParentChild,
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
    [property: JsonPropertyName("span")] StructuralSpan Span);

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
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason)
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
        Elements = elements ?? throw new ArgumentNullException(nameof(elements));
        Relations = relations ?? [];
    }

    [JsonPropertyName("elements")]
    public IReadOnlyList<ValidatedStructuralElement> Elements { get; }

    [JsonPropertyName("relations")]
    public IReadOnlyList<StructuralRelation> Relations { get; }

    public IReadOnlyList<ValidatedStructuralElement> Headings =>
        Elements.Where(element => element.Type == StructuralElementType.Heading).ToArray();

    public static ValidatedStructure FromElements(IEnumerable<ValidatedStructuralElement> elements)
    {
        var materialized = elements?.ToArray() ?? throw new ArgumentNullException(nameof(elements));
        var ids = materialized.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        var relations = materialized
            .Where(element => element.ParentId is not null && ids.Contains(element.ParentId))
            .Select(element => new StructuralRelation(
                element.ParentId!, element.Id, StructuralRelationType.ParentChild))
            .ToArray();
        return new ValidatedStructure(materialized, relations);
    }
}
