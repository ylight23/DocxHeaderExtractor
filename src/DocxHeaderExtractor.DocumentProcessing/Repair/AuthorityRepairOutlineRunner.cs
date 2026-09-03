using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Repair;

/// <summary>
/// Repair adapter that delegates outline production to the normal authority pipeline.
/// Legacy extraction remains outside this repair adapter until its physical retirement step.
/// </summary>
public sealed class AuthorityRepairOutlineRunner : IRepairOutlineRunner
{
    private readonly AuthorityExtractionPipeline _pipeline;

    public AuthorityRepairOutlineRunner(PipelineOptions options)
    {
        _pipeline = new AuthorityExtractionPipeline(options);
    }

    public Task<DocumentOutline> RunAsync(
        string inputPath,
        CancellationToken cancellationToken = default) =>
        _pipeline.RunAsync(inputPath, cancellationToken);

    public void Dispose() => _pipeline.Dispose();
}
