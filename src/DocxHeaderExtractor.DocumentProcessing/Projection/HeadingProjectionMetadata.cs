using DocxHeaderExtractor.Core.Models;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

/// <summary>
/// Outline payload used only while the existing HeadingRecord API remains public. It keeps
/// projection details out of source/span validation logic while allowing a lossless heading projection.
/// </summary>
public sealed record HeadingProjectionMetadata
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
