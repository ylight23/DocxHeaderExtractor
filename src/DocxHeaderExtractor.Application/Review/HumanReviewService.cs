using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Application.Review;

public sealed class HumanReviewService
{
    private readonly IHumanReviewStore _store;

    public HumanReviewService(IHumanReviewStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task PublishAsync(
        DocumentReviewResult review,
        CancellationToken cancellationToken = default) =>
        _store.PublishAsync(review, cancellationToken);

    public Task<DocumentReviewResult?> GetReviewAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        _store.GetReviewAsync(documentId, cancellationToken);

    public Task<DocumentReviewResult?> GetAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        _store.GetReviewAsync(documentId, cancellationToken);

    public Task<IReadOnlyList<HumanReviewRecord>> GetRecordsAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        _store.GetRecordsAsync(documentId, cancellationToken);

    public async Task<ApprovedWritebackPlan?> BuildWritebackPlanAsync(
        string documentId,
        SourceDocument? source = null,
        bool allowSourceDocumentIdAlias = false,
        CancellationToken cancellationToken = default)
    {
        var review = await _store.GetReviewAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (review is null) return null;
        var records = await _store.GetRecordsAsync(documentId, cancellationToken).ConfigureAwait(false);
        return ApprovedWritebackPlanProjector.Build(
            review, records, source, allowSourceDocumentIdAlias);
    }

    public async Task<HumanReviewRecord> RecordAsync(
        string documentId,
        HumanReviewDecision decision,
        CancellationToken cancellationToken = default)
    {
        var review = await _store.GetReviewAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (review is null)
            throw new InvalidOperationException("review-session-not-found");

        var record = HumanReviewDecisionRecorder.Record(documentId, review, decision);
        await _store.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }
}
