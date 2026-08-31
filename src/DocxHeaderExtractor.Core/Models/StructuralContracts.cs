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

/// <summary>Stable source identity and the exact source span owned by the parser.</summary>
public sealed record SourceReference(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("span")] StructuralSpan Span);

/// <summary>
/// Untrusted structural proposal. Text, source span, and source identity remain outside the
/// proposal so a model cannot become authority over observed document facts.
/// </summary>
public sealed record StructuralProposal
{
    [JsonPropertyName("sourceId")]
    public required string SourceId { get; init; }

    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required StructuralElementType Type { get; init; }

    [JsonPropertyName("role")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ProposedRole Role { get; init; }

    [JsonPropertyName("parentId")]
    public string? ParentId { get; init; }

    [JsonPropertyName("level")]
    public int? Level { get; init; }
}

/// <summary>Generic decision metadata carried after validation, independent of heading output.</summary>
public sealed record StructuralDecision(
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("confidenceBasis")] string ConfidenceBasis,
    [property: JsonPropertyName("disputed")] bool Disputed = false);

public sealed record StructuralValidation(
    [property: JsonPropertyName("sourceGrounded")] bool SourceGrounded,
    [property: JsonPropertyName("spanValid")] bool SpanValid,
    [property: JsonPropertyName("typeValid")] bool TypeValid,
    [property: JsonPropertyName("levelValid")] bool LevelValid,
    [property: JsonPropertyName("parentValid")] bool ParentValid,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason)
{
    [JsonIgnore]
    public bool Accepted => RejectionReason is null;
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

    [JsonPropertyName("source")]
    public required SourceReference Source { get; init; }

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
