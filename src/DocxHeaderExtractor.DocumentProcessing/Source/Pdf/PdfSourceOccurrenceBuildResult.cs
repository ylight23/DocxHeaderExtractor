using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

/// <summary>PDF adapter output: common source IR plus parser-only diagnostics used by the PDF route.</summary>
internal sealed record PdfSourceOccurrenceBuildResult(
    Common.SourceOccurrenceUniverse Universe,
    PdfSourceOccurrenceDetails Details);

internal sealed record PdfSourceOccurrenceDetails(
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyDictionary<string, PdfSemanticSourceContext> Contexts,
    IReadOnlyDictionary<string, string> LayoutBlockByAtom,
    int ParserLineCount);
