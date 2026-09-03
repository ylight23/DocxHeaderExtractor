using DocxHeaderExtractor.DocumentProcessing.Review;
using DocxHeaderExtractor.Application.Feedback;
using DocxHeaderExtractor.Infrastructure.Learning;

namespace DocxHeaderExtractor.Infrastructure.Feedback;

/// <summary>Infrastructure adapter preserving the existing append-only CorrectionMemory format.</summary>
public sealed class CorrectionMemoryFeedbackStore(CorrectionMemory memory) : IHumanFeedbackStore
{
    private readonly CorrectionMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));

    public int Count => _memory.Count;

    public Task<int> SaveChangedAsync(
        HumanFeedbackSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var bundle = ReviewBundle.Parse(submission.ReviewBundleJson);
        return _memory.SaveChangedAsync(bundle, cancellationToken);
    }
}
