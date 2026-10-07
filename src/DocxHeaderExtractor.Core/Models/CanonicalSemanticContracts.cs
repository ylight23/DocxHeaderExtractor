using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

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
    [property: JsonIgnore] SourceAnchor? SourceAnchor = null)
{
    public bool Contains(StructuralSpan span) =>
        span.Start >= 0 && span.End <= Text.Length && span.IsValidFor(Text);
}

/// <summary>Model-facing semantic output. There are intentionally no numeric offsets.</summary>
public sealed record CanonicalSemanticProposal(
    [property: JsonPropertyName("sourceAlias")] string SourceAlias,
    [property: JsonPropertyName("isHeading")] bool IsHeading,
    [property: JsonPropertyName("verbatimText")] string? VerbatimText,
    [property: JsonPropertyName("verbatimParts")] IReadOnlyList<string>? VerbatimParts = null,
    [property: JsonPropertyName("semanticRole")] string? SemanticRole = null,
    [property: JsonPropertyName("structuralType")] string? StructuralType = null,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("relationHints")] IReadOnlyList<string>? RelationHints = null,
    [property: JsonPropertyName("sourceAliases")] IReadOnlyList<string>? SourceAliases = null,
    [property: JsonPropertyName("occurrence")] int? Occurrence = null,
    [property: JsonPropertyName("leftExactContext")] string? LeftExactContext = null,
    [property: JsonPropertyName("rightExactContext")] string? RightExactContext = null,
    [property: JsonPropertyName("selectionMode")] string? SelectionMode = null,
    // The complete ordered tuple a structured-coordinate contract's reply carries, and the only
    // thing that identifies such a claim: a heading occupying part of one atom and a heading
    // spanning two are different claims that no scalar alias can tell apart. Null for contracts
    // that do not issue this shape, and omitted from serialization when null so every artifact
    // written under the earlier shape keeps its bytes.
    [property: JsonPropertyName("sourceParts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SemanticSourcePart>? SourceParts = null);

public static class CanonicalSemanticSelectionMode
{
    public const string VerbatimText = "VERBATIM_TEXT";
    public const string WholeAlias = "WHOLE_ALIAS";
}

public static class CanonicalSemanticContract
{
    public const string ProtocolVersion = "a99-canonical-semantic-vnext-v1";

    /// <summary>Schema used by the model. Numeric coordinates are deliberately not accepted.</summary>
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            headings = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        sourceAlias = new { type = "string", minLength = 1 },
                        sourceAliases = new { type = "array", minItems = 1, items = new { type = "string", minLength = 1 } },
                        isHeading = new { type = "boolean" },
                        verbatimText = new { type = "string" },
                        verbatimParts = new { type = "array", items = new { type = "string" } },
                        semanticRole = new { type = "string" },
                        structuralType = new { type = "string" },
                        scope = new { type = "string" },
                        relationHints = new { type = "array", items = new { type = "string" } },
                        occurrence = new { type = "integer", minimum = 1 },
                        leftExactContext = new { type = "string" },
                        rightExactContext = new { type = "string" },
                        selectionMode = new { type = "string", @enum = new[] { CanonicalSemanticSelectionMode.VerbatimText, CanonicalSemanticSelectionMode.WholeAlias } },
                    },
                    required = new[] { "sourceAlias", "isHeading" },
                },
            },
        },
        required = new[] { "headings" },
    };
}

public enum CanonicalSemanticBindingStatus
{
    Bound,
    NonHeadingIgnored,
    UnknownAlias,
    MissingVerbatimText,
    NonVerbatimText,
    AmbiguousBinding,
    OutOfOwnedSegment,
    DuplicateBinding,
}

public sealed record CanonicalSemanticBindingObservation(
    int Ordinal,
    CanonicalSemanticProposal Proposal,
    CanonicalSemanticBindingStatus Status,
    string? SourceId,
    int? Start,
    int? End,
    string? Reason);

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

public sealed record CanonicalSemanticGraphOccurrence(
    string OccurrenceId,
    string SemanticNodeId,
    string SourceAlias,
    string SourceId,
    int SourceOrdinal,
    string Text,
    string SemanticRole,
    string StructuralType,
    string Scope,
    int Start,
    int End,
    string OccurrenceKind,
    string? ParentOccurrenceId)
{
    /// <summary>Optional structural facts resolved after semantic binding; absent means unresolved.</summary>
    public int? Level { get; init; }

    /// <summary>Coordinate authority for this canonical occurrence. Text occurrences use
    /// UTF-16; visual-only occurrences use a visual-region identity.</summary>
    public string BindingMode { get; init; } = "TEXT_UTF16";

    /// <summary>Parser/render-owned order used to merge text and visual occurrences.</summary>
    [JsonIgnore]
    public CanonicalSemanticDocumentOrder? DocumentOrder { get; init; }
}

public sealed record CanonicalSemanticDocumentOrder(int Page, double Top, double Left, int Layer, int LocalOrdinal);

public sealed record CanonicalSemanticGraph(
    IReadOnlyList<CanonicalSemanticGraphOccurrence> Occurrences,
    IReadOnlyList<CanonicalSemanticGraphOccurrence> OutlineProjection);
