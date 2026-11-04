namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>A failed review batch and its original candidate indexes. Other batches remain usable.</summary>
public sealed record GatherReviewBatchFailure(int Start, int Count, string Error);

/// <summary>Verdicts use indexes in the original run; failed batches remain UNSURE.</summary>
public sealed record GatherBatchedReviewResult(GatherReviewResult Review, IReadOnlyList<GatherReviewBatchFailure> Failures);

/// <summary>Source-bound semantic review with bounded batches and bounded concurrency.</summary>
public static class GatherBatchedReview
{
    public static async Task<GatherBatchedReviewResult> ReviewAsync(string goal, GatherRun run, IReadOnlyList<GatherSource> sources,
        IGatherModel model, int batchSize = 10, int concurrency = 4, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        var slices = Enumerable.Range(0, (run.Facts.Count + batchSize - 1) / batchSize)
            .Select(i => (Start: i * batchSize, Facts: run.Facts.Skip(i * batchSize).Take(batchSize).ToArray())).ToArray();
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = slices.Select(async slice =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var part = run with { Facts = slice.Facts };
                var review = await GatherSemanticReview.ReviewAsync(goal, part, sources, model, ct).ConfigureAwait(false);
                return (slice.Start, Count: slice.Facts.Length, Result: review);
            }
            finally { gate.Release(); }
        });
        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        var failures = completed.Where(b => b.Result.Error is not null)
            .Select(b => new GatherReviewBatchFailure(b.Start, b.Count, b.Result.Error!)).ToArray();
        var verdicts = completed.SelectMany(b => b.Result.Verdicts.Select(v => v with { Index = b.Start + v.Index }))
            .OrderBy(v => v.Index).ToArray();
        return new GatherBatchedReviewResult(new GatherReviewResult(verdicts, null, run.Refused.Count), failures);
    }
}
