using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Turns an uploaded PDF into a canonical document, the same shape the DOCX lane produces.
/// <para>
/// The projections after the semantic stage - source catalog, sections, chunks - are the shared
/// ones, so a caller receives the same contract whichever format it uploaded and can project either
/// with the same intent. What must not be shared is the content: this reads the PDF and only the
/// PDF.
/// </para>
/// </summary>
public static class PdfCanonicalExtraction
{
    public static async Task<DocumentExtractionResult> RunAsync(
        UploadedFile file,
        PipelineOptions options,
        IHeaderClassifier? analyst = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        if (file.DetectedType != SourceType.Pdf)
            throw new UnsupportedSourceException(file);

        var authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
            file.LocalPath, options.DisableLlm ? null : analyst, ct);

        var catalog = authority.Audit is null
            ? new DocumentSourceCatalog([])
            : PdfSourceCatalogOf(authority);
        var sections = StructuralSectionProjection.Project(authority.Structure, catalog);
        var chunks = SectionChunkProjection.Project(
            sections, catalog, authority.Structure,
            new DocumentChunkingPolicy(Math.Max(1, options.Chunking.TokenBudget)));

        return new DocumentExtractionResult(
            new DocumentIdentity(
                Path.GetFileNameWithoutExtension(file.LocalPath),
                file.OriginalFileName,
                "pdf",
                file.LocalPath),
            catalog,
            authority.Structure,
            sections,
            chunks,
            new DocumentExtractionProvenance(
                "pdf-canonical-vnext",
                "pdf-source-document",
                authority.Audit?.RawAnalystResponses.Count ?? 0)
            {
                ExecutionContract = ExecutionContracts.ExplicitUploadedPdfCanonical,
            });
    }

    /// <summary>
    /// Rebuilds the catalog from the blocks the audit recorded, so the units a consumer sees are
    /// the units the model was shown - not a second parse that could disagree with the first.
    /// </summary>
    private static DocumentSourceCatalog PdfSourceCatalogOf(Authority.StructuralAuthorityResult authority) =>
        new(authority.Audit!.CandidateBlocks.Select((block, index) => new DocumentSourceUnit(
            block.Id,
            index,
            block.Text ?? string.Empty,
            new SourceAnchor
            {
                SourceType = "pdf",
                ParagraphId = block.Id,
                ParagraphIndex = index,
            },
            new StructuralSpan(0, (block.Text ?? string.Empty).Length))));
}
