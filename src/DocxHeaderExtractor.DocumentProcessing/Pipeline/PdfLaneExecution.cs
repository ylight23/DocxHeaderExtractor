namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Runs one lane under a hard wall-clock budget. The returned timeout result intentionally does
/// not await a provider task that ignored cancellation: a stuck semantic call must never hold the
/// independently scheduled visual lane hostage.
/// </summary>
internal static class PdfLaneExecution
{
    internal sealed record Result<T>(T? Value, bool TimedOut, bool Cancelled, Exception? Fault = null)
    {
        /// <summary>Work that outlived the hard deadline and may still touch its checkpoint.</summary>
        public Task? DetachedTask { get; init; }
    }

    public static async Task<Result<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan deadline,
        CancellationToken callerCancellation)
    {
        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        var work = action(laneCancellation.Token);
        var deadlineTask = Task.Delay(deadline, callerCancellation);
        var completed = await Task.WhenAny(work, deadlineTask).ConfigureAwait(false);
        if (completed == work)
        {
            try
            {
                return new Result<T>(await work.ConfigureAwait(false), false, false);
            }
            catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
            {
                return new Result<T>(default, false, true);
            }
            catch (Exception ex)
            {
                return new Result<T>(default, false, false, ex);
            }
        }

        laneCancellation.Cancel();
        // Observe an eventual provider failure without retaining it as an unobserved task fault.
        _ = work.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return new Result<T>(default, !callerCancellation.IsCancellationRequested, callerCancellation.IsCancellationRequested)
        {
            DetachedTask = work,
        };
    }
}
