using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Repair;

/// <summary>
/// Repair adapter that delegates outline production to the normal authority pipeline.
/// It deliberately does not remove or alter the historical HeaderExtractionPipeline.
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
