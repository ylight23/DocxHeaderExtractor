using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Source-derived audit record, deliberately separate from <see cref="ResolvedHeadingHierarchy"/>.</summary>
public sealed record HeadingHierarchyFactAudit(
    string Id,
    int SourceOrder,
    int Page,
    string StructuralScope,
    string DocumentRegime,
    string? MarkerFamily,
    int? MarkerDepth,
    bool MarkerIsPath,
    string? MarkerPath,
    string? PreviousValidatedId,
    string? MarkerPrefixParentId,
    int? ResolvedLevel,
    string ParentResolution,
    IReadOnlyList<string> Evidence)
{
    /// <summary>
    /// M8.1a readable occurrence id: <c>p{page}:{blockId}:s{start}-{end}</c>. It is opaque to
    /// consumers — page, block, and span authority live in their own fields, so a future format
    /// change cannot silently break an identity bridge that parsed the string.
    /// </summary>
    public string FactId { get; init; } = "";

    /// <summary>Immutable source authority: the whole raw block text the span points into.</summary>
    public string SourceBlockText { get; init; } = "";

    public string SourceBlockTextSha256 { get; init; } = "";

    /// <summary>Source pointer into <see cref="SourceBlockText"/>; End is exclusive.</summary>
    public TextOffsetSpan HeadingSpan { get; init; } = new(0, 0);

    /// <summary>Deterministic slice of <see cref="SourceBlockText"/>. Never model-authored.</summary>
    public string HeadingText { get; init; } = "";

    /// <summary>
    /// Complete numeric components as the marker parser observed them. Observation only: it is not
    /// the source of <see cref="MarkerPath"/>, <see cref="ResolvedLevel"/>, or any parent relation.
    /// A disagreement with <see cref="MarkerDepth"/> is impossible; a disagreement with
    /// <see cref="MarkerPath"/> is expected wherever the source lost its separators.
    /// </summary>
    public IReadOnlyList<int> MarkerComponents { get; init; } = [];

    /// <summary>Parser line identities, kept only to correlate with line-level M7 artifacts.</summary>
    public IReadOnlyList<string> LineIds { get; init; } = [];

    public PdfSourceGeometry Geometry { get; init; } = new(0, 0, 0, 0);
}

public sealed record PdfSourceGeometry(double Left, double TopY, double Right, double BottomY);
