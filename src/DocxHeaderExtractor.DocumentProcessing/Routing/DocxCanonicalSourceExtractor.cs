using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
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
/// The PDF lane, owning PDF uploads and nothing else.
/// <para>
/// It runs the same semantic stage as the DOCX lane through
/// <c>CanonicalSemanticPdfAuthorityAdapter</c>, reading only the uploaded PDF. It never looks for a
/// DOCX, and its result stands alone: a PDF and a DOCX of the same document are two independent
/// canonical documents unless a user asks for them to be compared.
/// </para>
/// </summary>
public sealed class PdfCanonicalSourceExtractor(PipelineOptions options, IHeaderClassifier? analyst = null)
    : ICanonicalSourceExtractor
{
    public SourceType Handles => SourceType.Pdf;

    public Task<DocumentExtractionResult> ExtractAsync(UploadedFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return PdfCanonicalExtraction.RunAsync(file, options, analyst, ct);
    }
}
