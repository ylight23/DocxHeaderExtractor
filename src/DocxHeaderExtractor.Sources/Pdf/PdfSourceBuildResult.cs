using DocxHeaderExtractor.Sources.Snapshot;

namespace DocxHeaderExtractor.Sources.Pdf;

/// <summary>PDF adapter output: common source IR plus parser-only diagnostics used by the PDF route.</summary>
internal sealed record PdfSourceBuildResult(
    DocumentSourceSnapshot Snapshot,
    PdfSourceDetails Details);

internal sealed record PdfSourceDetails(
    IReadOnlyList<PdfLayoutBlock> Blocks,
    IReadOnlyDictionary<string, PdfSourceContext> Contexts,
    IReadOnlyDictionary<string, string> LayoutBlockByAtom,
    int ParserLineCount);

/// <summary>
/// Opaque, one-run hand-off of a PDF build from the lane to a selector. It deliberately exposes no
/// parser-only types outside the Sources assembly: a selector can consume the exact build but cannot
/// reinterpret or mutate it.
/// </summary>
public sealed class PdfPreparedSource
{
    internal PdfPreparedSource(PdfSourceBuildResult build) => Build = build ?? throw new ArgumentNullException(nameof(build));
    internal PdfSourceBuildResult Build { get; }
}

/// <summary>
/// Optional PDF-specific priming seam. A selector that implements it consumes the already-built,
/// byte-identified source for one matching path; other selectors retain the independent parse path.
/// Priming is one run's hand-off: the lane unprimes in a finally block, so a run that never reaches
/// the selector leaves nothing behind.
/// </summary>
public interface IPrimedPdfSourceSelector
{
    void PrimePdfSource(string path, PdfPreparedSource source);

    void UnprimePdfSource(string path);
}
