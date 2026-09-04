namespace DocxHeaderExtractor.Application.Review;

public interface IHumanReviewStore
{
    Task PublishAsync(DocumentReviewResult review, CancellationToken cancellationToken = default);

    Task<DocumentReviewResult?> GetReviewAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    Task AppendAsync(
        HumanReviewRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HumanReviewRecord>> GetRecordsAsync(
        string documentId,
        CancellationToken cancellationToken = default);
}
