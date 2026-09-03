namespace DocxHeaderExtractor.Application.Feedback;

/// <summary>Host-neutral submission for a human feedback artifact.</summary>
public sealed record HumanFeedbackSubmission(string ReviewBundleJson);

/// <summary>
/// Application port for human feedback persistence. Implementations own storage and format
/// compatibility; feedback never becomes authority implicitly.
/// </summary>
public interface IHumanFeedbackStore
{
    int Count { get; }

    Task<int> SaveChangedAsync(
        HumanFeedbackSubmission submission,
        CancellationToken cancellationToken = default);
}
