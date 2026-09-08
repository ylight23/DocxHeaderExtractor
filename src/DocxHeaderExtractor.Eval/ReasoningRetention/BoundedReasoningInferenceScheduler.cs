using System.Diagnostics;
using System.Threading.Channels;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// One campaign-wide bounded queue for model inference.  It deliberately owns the only
/// concurrency gate so document and segment parallelism cannot multiply the vLLM budget.
/// </summary>
public sealed class BoundedReasoningInferenceScheduler<TWork, TResult> : IAsyncDisposable
{
    private readonly Channel<WorkItem> _queue;
    private readonly Func<TWork, CancellationToken, Task<TResult>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private int _active;
    private int _peakInflight;
    private int _completed;
    private int _failed;
    private int _disposed;

    public BoundedReasoningInferenceScheduler(
        int maxInflight,
        Func<TWork, CancellationToken, Task<TResult>> handler,
        int? queueCapacity = null)
    {
        if (maxInflight is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(maxInflight), "The local vLLM campaign budget is 1..8.");
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        var capacity = queueCapacity ?? maxInflight * 2;
        if (capacity < maxInflight) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _workers = Enumerable.Range(0, maxInflight).Select(_ => WorkerAsync(_shutdown.Token)).ToArray();
    }

    public int MaxInflight => _workers.Length;
    public int PeakInflight => Volatile.Read(ref _peakInflight);
    public int CompletedCount => Volatile.Read(ref _completed);
    public int FailedCount => Volatile.Read(ref _failed);

    public async ValueTask<Task<ScheduledInferenceResult<TWork, TResult>>> EnqueueAsync(
        TWork work,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var completion = new TaskCompletionSource<ScheduledInferenceResult<TWork, TResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(work, completion, Stopwatch.GetTimestamp());
        await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        return completion.Task;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _queue.Writer.TryComplete();
        await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var queueWait = Stopwatch.GetElapsedTime(item.EnqueuedTimestamp);
                var active = Interlocked.Increment(ref _active);
                UpdatePeak(active);
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var result = await _handler(item.Work, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _completed);
                    item.Completion.TrySetResult(new ScheduledInferenceResult<TWork, TResult>(
                        item.Work, result, queueWait, Stopwatch.GetElapsedTime(started), null));
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    item.Completion.TrySetResult(new ScheduledInferenceResult<TWork, TResult>(
                        item.Work, default, queueWait, Stopwatch.GetElapsedTime(started), ex));
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    // A failed item is data, not a campaign-wide cancellation signal. Other
                    // documents remain eligible to finish on the shared queue.
                    item.Completion.TrySetResult(new ScheduledInferenceResult<TWork, TResult>(
                        item.Work, default, queueWait, Stopwatch.GetElapsedTime(started), ex));
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            while (_queue.Reader.TryRead(out var abandoned))
                abandoned.Completion.TrySetCanceled(cancellationToken);
        }
    }

    private void UpdatePeak(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _peakInflight);
            if (active <= current || Interlocked.CompareExchange(ref _peakInflight, active, current) == current)
                return;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }

    private sealed record WorkItem(
        TWork Work,
        TaskCompletionSource<ScheduledInferenceResult<TWork, TResult>> Completion,
        long EnqueuedTimestamp);
}

public sealed record ScheduledInferenceResult<TWork, TResult>(
    TWork Work,
    TResult? Result,
    TimeSpan QueueWait,
    TimeSpan Inference,
    Exception? Error)
{
    public bool Succeeded => Error is null;
}
