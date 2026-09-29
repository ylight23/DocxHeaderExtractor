using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// One coordinate atom: a piece of source text the harness owns and the model may name.
/// <para>
/// Deliberately not a PDF type. The layout coordinates it carries - page, row, position within the
/// row - are what a locality rule needs and are as true of a two-column page as of a wrapped
/// paragraph, so the binding contract does not have to know how the atoms were reconstructed.
/// </para>
/// </summary>
public sealed record SemanticSourceAtom(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("segment")] int Segment,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// One piece of a semantic claim: an atom, and which part of it.
/// <para>
/// A claim is a list of these rather than a list of aliases, because an atom is a coordinate unit
/// and not a semantic one. A run-in heading occupies part of one atom; a heading that wraps
/// occupies all of two. Naming only aliases would reproduce, one level down, exactly the assumption
/// this work exists to remove - that a semantic boundary must coincide with a parser boundary.
/// </para>
/// </summary>
public sealed record SemanticSourcePart(
    [property: JsonPropertyName("sourceAlias")] string SourceAlias,
    [property: JsonPropertyName("selectionMode")] string SelectionMode,
    [property: JsonPropertyName("verbatimText")] string? VerbatimText = null,
    [property: JsonPropertyName("occurrence")] int? Occurrence = null,
    [property: JsonPropertyName("leftExactContext")] string? LeftExactContext = null,
    [property: JsonPropertyName("rightExactContext")] string? RightExactContext = null);

/// <summary>What the model returns under the structured contract. Still no numeric coordinates.</summary>
public sealed record SemanticSourcePartsProposal(
    [property: JsonPropertyName("sourceParts")] IReadOnlyList<SemanticSourcePart> SourceParts,
    [property: JsonPropertyName("isHeading")] bool IsHeading = true,
    [property: JsonPropertyName("semanticRole")] string? SemanticRole = null,
    [property: JsonPropertyName("relationHints")] IReadOnlyList<string>? RelationHints = null);

/// <summary>How two consecutive parts sit in the source. Owned by the harness, never by the model.</summary>
public enum SemanticSourceLocality
{
    /// <summary>Both selections lie in one atom, in order and without overlapping.</summary>
    SameSegment,

    /// <summary>The next segment of the same row - a bullet and its text, a cell and the next.</summary>
    SameRowNextSegment,

    /// <summary>The next row of the same page - a heading or sentence continuing over a line break.</summary>
    NextRowCompatible,

    /// <summary>Retired with GENERIC_MULTIPART_BINDER_V2: the binder no longer produces it. Kept so recorded bindings still read.</summary>
    PageTransitionNotSupported,

    /// <summary>Retired with GENERIC_MULTIPART_BINDER_V2: the binder no longer produces it. Kept so recorded bindings still read.</summary>
    NonLocal,

    /// <summary>
    /// A later row of the same page that is not the next one - a title line in one column resuming
    /// after a row of the other column. Valid when the part is named explicitly.
    /// </summary>
    SamePageNonAdjacent,

    /// <summary>A later page - a title continued over a page break. Valid when the part is named explicitly.</summary>
    CrossPage,
}
public enum SemanticSourcePartsStatus
{
    Bound,
    NoParts,
    UnknownAlias,
    MissingVerbatimText,
    UnexpectedVerbatimText,
    UnknownSelectionMode,
    TextNotInAtom,
    AmbiguousSelection,
    DuplicatePart,
    OutOfSourceOrder,
    OverlappingParts,
    /// <summary>Retired with GENERIC_MULTIPART_BINDER_V2; kept so recorded refusals still read.</summary>
    NonLocalChain,

    /// <summary>Retired with GENERIC_MULTIPART_BINDER_V2; kept so recorded refusals still read.</summary>
    PageTransitionNotSupported,
}

/// <summary>One part after the harness has resolved where it actually is.</summary>
public sealed record BoundSourcePart(
    string Alias,
    string SourceId,
    int Ordinal,
    int Start,
    int End,
    string Text,
    SemanticSourceLocality LocalityFromPrevious);

/// <summary>
/// The result of binding. <see cref="Identity"/> is the authoritative form of the claim: an ordered
/// tuple of harness-resolved coordinates, never a string the model produced and never a rendering.
/// </summary>
public sealed record SemanticSourcePartsBinding(
    SemanticSourcePartsStatus Status,
    IReadOnlyList<BoundSourcePart> Parts,
    string? Reason = null)
{
    public bool IsBound => Status == SemanticSourcePartsStatus.Bound;

    /// <summary>
    /// <c>alias:start-end</c> per part, joined in source order. Two claims are the same claim when
    /// this is the same, whatever either one was rendered as.
    /// </summary>
    public string Identity =>
        string.Join("|", Parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
}
