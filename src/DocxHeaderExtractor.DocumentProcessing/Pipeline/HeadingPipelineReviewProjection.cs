using DocxHeaderExtractor.Application.Review;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Adapts pipeline output to the application review contract without changing authority.</summary>
public static class HeadingPipelineReviewProjection
{
    public static DocumentReviewResult ToReviewResult(
        string documentId,
        HeadingPipelineResult pipelineResult,
        IEnumerable<SourceFacts> sourceFacts)
    {
        ArgumentNullException.ThrowIfNull(pipelineResult);
        ArgumentNullException.ThrowIfNull(sourceFacts);

        var diagnostics = pipelineResult.Diagnostics.Select(diagnostic => new ReviewDiagnosticDto(
            diagnostic.Reason is null
                ? $"pipeline.{diagnostic.Status}"
                : $"pipeline.{diagnostic.Status}.{diagnostic.Reason}",
            diagnostic.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase)
                ? "error"
                : diagnostic.Status.Equals("discarded", StringComparison.OrdinalIgnoreCase)
                    ? "warning"
                    : "info",
            diagnostic.SourceId,
            diagnostic.Reason ?? diagnostic.Status,
            diagnostic.Provenance));

        return DocumentReviewResultMapper.FromValidatedHeadings(
            documentId,
            pipelineResult.Headings,
            sourceFacts,
            diagnostics);
    }
}
