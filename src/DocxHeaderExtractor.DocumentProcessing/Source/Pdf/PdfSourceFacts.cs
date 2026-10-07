using System.Collections.Immutable;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Immutable facts observed by the PDF parser and layout filter. Model output is deliberately
/// represented separately so it cannot overwrite text, geometry, or source identity.
/// </summary>
internal sealed record PdfSourceFacts(
    string SourceId,
    string RawText,
    int Page,
    int LineCount,
    double Left,
    double TopY,
    double Right,
    double BottomY,
    string StructuralScope,
    IReadOnlyList<string> ObservedEvidence)
{
    /// <summary>Parser-derived only; model proposals cannot alter this marker fact.</summary>
    public SourceMarkerFact? Marker { get; init; }

    /// <summary>
    /// Format the document declares about this occurrence, as opposed to where it sits. A PDF has
    /// no style names, so weight, slant and size are the only declarations available - and for a
    /// PDF they carry most of the heading signal, which is why they belong on the facts rather than
    /// being reconstructed downstream from geometry.
    /// </summary>
    public double BoldRatio { get; init; }

    public double ItalicRatio { get; init; }

    /// <summary>Absolute point size. Only ever reported to the model relative to the document.</summary>
    public double FontSize { get; init; }

    /// <summary>
    /// The block's typography with each fact's origin (<see cref="PdfLineTypography"/>, averaged over its
    /// lines; the font name and bold evidence source of its first line that has one). Null when the lines
    /// carry none.
    /// </summary>
    public PdfLineTypography? Typography { get; init; }

    /// <summary>Stable parser line identities retained for source/audit correlation.</summary>
    public IReadOnlyList<string> LineIds { get; init; } = [];

    /// <summary>
    /// Physical position, reported to the model in place of <see cref="StructuralScope"/>: the vertical
    /// position of the block's top line (0 = lowest text in the document, 1 = highest), and on how
    /// many pages - first to last - its normalized text recurs. For a multi-line block that is its least-recurring line, since the block
    /// recurs as a whole at most that often. Null where no line annotation was available.
    /// </summary>
    public double? VerticalPosition { get; init; }

    public int? SameNormalizedTextPageCount { get; init; }

    public int? SameNormalizedTextFirstPage { get; init; }

    public int? SameNormalizedTextLastPage { get; init; }

    /// <summary>Structured fact provenance for validator authority checks.</summary>
    public IReadOnlyList<PdfObservedEvidence> EvidenceDetails { get; init; } = [];
}

internal sealed record PdfObservedEvidence(string Kind, string Value, string Origin);

/// <summary>Small, stable context for a 9B semantic pass; no document-wide free-text prompt.</summary>
internal sealed record PdfSemanticSourceContext(
    PdfSourceFacts Source,
    IReadOnlyList<string> PreviousBlocks,
    IReadOnlyList<string> NextBlocks,
    IReadOnlyList<string> AllowedParentIds,
    string DocumentRegime)
{
}
