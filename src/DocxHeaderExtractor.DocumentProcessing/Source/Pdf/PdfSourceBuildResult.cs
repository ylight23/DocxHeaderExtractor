
namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

/// <summary>PDF adapter output: common source IR plus parser-only diagnostics used by the PDF route.</summary>
internal sealed record PdfSourceBuildResult(
    Common.DocumentSourceSnapshot Snapshot,
    PdfSourceDetails Details);

internal sealed record PdfSourceDetails(
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyDictionary<string, PdfSemanticSourceContext> Contexts,
    IReadOnlyDictionary<string, string> LayoutBlockByAtom,
    int ParserLineCount);
