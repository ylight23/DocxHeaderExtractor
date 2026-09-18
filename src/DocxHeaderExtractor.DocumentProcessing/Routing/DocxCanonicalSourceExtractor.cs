using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>The DOCX lane, owning DOCX uploads and nothing else.</summary>
public sealed class DocxCanonicalSourceExtractor(AuthorityExtractionPipeline pipeline) : ICanonicalSourceExtractor
{
    public SourceType Handles => SourceType.Docx;

    public Task<DocumentExtractionResult> ExtractAsync(UploadedFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return pipeline.RunDocumentAsync(file.LocalPath, ct);
    }
}

/// <summary>
/// The PDF lane's place in the dispatcher, declared but not yet built.
/// <para>
/// Registering it makes a PDF upload fail with the reason rather than with "unsupported format",
/// which would be wrong: the format is supported in principle and the lane exists in
/// <c>PdfLayoutEvidenceOutline</c> - it just cannot yet run from a PDF alone. Every entry into it
/// still calls <c>PdfTextbookOutline.FindSiblingPdf</c>, so today it can only be reached as a
/// companion to a DOCX. Wiring it is the S2 task: each of those call sites has to be classified as
/// needing the uploaded PDF, needing an explicit companion, or being corpus discovery that must
/// leave production.
/// </para>
/// </summary>
public sealed class PdfCanonicalSourceExtractorNotWired : ICanonicalSourceExtractor
{
    public SourceType Handles => SourceType.Pdf;

    public Task<DocumentExtractionResult> ExtractAsync(UploadedFile file, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"'{file.OriginalFileName}' is a PDF. The PDF lane cannot yet run from an uploaded PDF alone: " +
            "its entry points still discover a companion file from the filesystem. See S2.");
}
