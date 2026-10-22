using MessagePack;
using DocxHeaderExtractor.Sources.Common;

namespace DocxHeaderExtractor.Sources.Pdf;

/// <summary>
/// Immutable facts observed by the PDF parser and layout filter. Model output is deliberately
/// represented separately so it cannot overwrite text, geometry, or source identity.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfSourceFacts(
    [property: Key(0)] string SourceId,
    [property: Key(1)] string RawText,
    [property: Key(2)] int Page,
    [property: Key(3)] int LineCount,
    [property: Key(4)] double Left,
    [property: Key(5)] double TopY,
    [property: Key(6)] double Right,
    [property: Key(7)] double BottomY,
    [property: Key(8)] string StructuralScope,
    [property: Key(9)] IReadOnlyList<string> ObservedEvidence)
{
    /// <summary>Parser-derived only; model proposals cannot alter this marker fact.</summary>
    [Key(10)]
    public SourceMarkerFact? Marker { get; init; }

    /// <summary>
    /// Format the document declares about this occurrence, as opposed to where it sits. A PDF has
    /// no style names, so weight, slant and size are the only declarations available - and for a
    /// PDF they carry most of the heading signal, which is why they belong on the facts rather than
    /// being reconstructed downstream from geometry.
    /// </summary>
    [Key(11)]
    public double BoldRatio { get; init; }

    [Key(12)]
    public double ItalicRatio { get; init; }

    /// <summary>Absolute point size. Only ever reported to the model relative to the document.</summary>
    [Key(13)]
    public double FontSize { get; init; }

    /// <summary>
    /// The block's typography with each fact's origin (<see cref="PdfLineTypography"/>, averaged over its
    /// lines; the font name and bold evidence source of its first line that has one). Null when the lines
    /// carry none.
    /// </summary>
    [Key(14)]
    public PdfLineTypography? Typography { get; init; }

    /// <summary>Stable parser line identities retained for source/audit correlation.</summary>
    [Key(15)]
    public IReadOnlyList<string> LineIds { get; init; } = [];

    /// <summary>
    /// Physical position, reported to the model in place of <see cref="StructuralScope"/>: the vertical
    /// position of the block's top line (0 = lowest text in the document, 1 = highest), and on how
    /// many pages - first to last - its normalized text recurs. For a multi-line block that is its least-recurring line, since the block
    /// recurs as a whole at most that often. Null where no line annotation was available.
    /// </summary>
    [Key(16)]
    public double? VerticalPosition { get; init; }

    [Key(17)]
    public int? SameNormalizedTextPageCount { get; init; }

    [Key(18)]
    public int? SameNormalizedTextFirstPage { get; init; }

    [Key(19)]
    public int? SameNormalizedTextLastPage { get; init; }

    /// <summary>Structured fact provenance for validator authority checks.</summary>
    [Key(20)]
    public IReadOnlyList<PdfObservedEvidence> EvidenceDetails { get; init; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfObservedEvidence([property: Key(0)] string Kind, [property: Key(1)] string Value, [property: Key(2)] string Origin);

/// <summary>Bounded parser-owned source context supplied to semantic inference; no heading authority.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfSourceContext(
    [property: Key(0)] PdfSourceFacts Source,
    [property: Key(1)] IReadOnlyList<string> PreviousBlocks,
    [property: Key(2)] IReadOnlyList<string> NextBlocks,
    [property: Key(3)] IReadOnlyList<string> AllowedParentIds,
    [property: Key(4)] string DocumentRegime)
{
}
