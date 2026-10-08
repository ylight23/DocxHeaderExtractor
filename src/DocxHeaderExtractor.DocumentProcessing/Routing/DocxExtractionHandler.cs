using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>The DOCX lane, owning DOCX uploads and nothing else.</summary>
public sealed class DocxExtractionHandler(DocxExtractionPipeline pipeline) : IDocumentExtractionHandler
{
    public SourceType Handles => SourceType.Docx;

    public Task<DocumentExtractionExecutionResult> ExtractAsync(
        UploadedFile file,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return pipeline.RunDocumentExecutionAsync(file.LocalPath, quarantinedIndexes, ct);
    }
}
