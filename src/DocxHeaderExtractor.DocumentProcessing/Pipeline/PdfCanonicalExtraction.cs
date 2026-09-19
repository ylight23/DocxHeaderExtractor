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

        // The catalog the lane parsed, not a second one derived from the audit. The audit's block
        // text is a readable rendering meant for a person to read; the model was shown, and the
        // binder bound against, the declared projection. Reconstructing from the audit meant a
        // consumer could be handed different text for exactly the occurrences where those two
        // disagree - which is the divergence this lane exists to rule out.
        var catalog = authority.SourceCatalog ?? new DocumentSourceCatalog([]);
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
}
