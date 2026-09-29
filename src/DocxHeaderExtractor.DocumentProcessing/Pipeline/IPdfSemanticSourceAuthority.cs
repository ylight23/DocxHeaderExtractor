using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// What the PDF semantic core needs from its source universe: the structured segment atoms of
/// <see cref="PdfStructuredSourceAuthority"/>, the only PDF coordinate authority.
/// </summary>
internal interface IPdfSemanticSourceAuthority
{
    IReadOnlyList<PdfSemanticBlock> Blocks { get; }
    IReadOnlyDictionary<string, PdfSemanticSourceContext> Contexts { get; }
    DocumentSourceCatalog Catalog { get; }
    IReadOnlyList<SemanticSourceAlias> Aliases { get; }
    IReadOnlyDictionary<string, int> OrdinalBySourceId { get; }
    int ParserLineCount { get; }
    string SourceUniverseSha256 { get; }
    CanonicalSemanticProductionInput CreateProductionInput(string documentId);
}
