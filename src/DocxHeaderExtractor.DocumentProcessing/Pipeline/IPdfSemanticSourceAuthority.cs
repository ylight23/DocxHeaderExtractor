using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// What the PDF semantic core needs from a source universe, regardless of which coordinate
/// authority built it. <see cref="PdfCanonicalSourceUniverse"/> (occurrence blocks) and
/// <see cref="PdfStructuredSourceAuthority"/> (segment atoms) both satisfy this so that
/// checkpointing, hierarchy resolution, and structure materialization run unchanged over either -
/// only how the source is read differs, per <see cref="CanonicalSemanticPdfAuthorityAdapter"/>.
/// </summary>
internal interface IPdfSemanticSourceAuthority
{
    IReadOnlyList<PdfSemanticBlock> Blocks { get; }
    IReadOnlyDictionary<string, PdfCandidateContext> Contexts { get; }
    DocumentSourceCatalog Catalog { get; }
    IReadOnlyList<SemanticSourceAlias> Aliases { get; }
    IReadOnlyDictionary<string, int> OrdinalBySourceId { get; }
    int ParserLineCount { get; }
    string SourceUniverseSha256 { get; }
    CanonicalSemanticProductionInput CreateProductionInput(string documentId);
}
