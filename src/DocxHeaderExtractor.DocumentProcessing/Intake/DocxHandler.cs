using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.DocumentProcessing.Lanes.Docx;
using DocxHeaderExtractor.Contracts.Routing;

namespace DocxHeaderExtractor.DocumentProcessing.Intake;

/// <summary>The DOCX lane, owning DOCX uploads and nothing else.</summary>
public sealed class DocxHandler(DocxPipeline pipeline) : IDocumentExtractionHandler
{
    public SourceType Handles => SourceType.Docx;

    public Task<RunResult> ExtractAsync(
        UploadedFile file,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return pipeline.RunDocumentExecutionAsync(file.LocalPath, ct);
    }
}
