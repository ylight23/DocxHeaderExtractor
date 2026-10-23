namespace DocxHeaderExtractor.DocumentProcessing.Harness;

/// <summary>
/// Runs one lane under a hard wall-clock budget. A timeout/cancellation returns without awaiting
/// work that ignored cancellation, but the detached work is always observed and its late output is
/// quarantined behind the execution lease.
/// </summary>
internal static class LaneExecution
{
    internal sealed record Result<T>(T? Value, bool TimedOut, bool Cancelled, Exception? Fault = null)
    {
        public LaneState State { get; init; }
        public LaneLease Lease { get; init; } = null!;

        /// <summary>Observed wrapper for work that outlived the deadline.</summary>
        public Task? DetachedTask { get; init; }
    }

    public static async Task<Result<T>> RunAsync<T>(
        Func<LaneLease, CancellationToken, Task<T>> action,
        TimeSpan deadline,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));

        var lease = new LaneLease();
        if (callerCancellation.IsCancellationRequested)
        {
            lease.TryTransition(LaneState.Cancelled);
            return new Result<T>(default, false, true)
            {
                State = LaneState.Cancelled,
                Lease = lease,
            };
        }

        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        Task<T> work;
        try
        {
            work = action(lease, laneCancellation.Token) ??
                throw new InvalidOperationException("The lane action returned a null task.");
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            lease.TryTransition(LaneState.Cancelled);
            return new Result<T>(default, false, true)
            {
                State = LaneState.Cancelled,
                Lease = lease,
            };
        }
        catch (Exception ex)
        {
            lease.TryTransition(LaneState.Failed);
            return new Result<T>(default, false, false, ex)
            {
                State = LaneState.Failed,
                Lease = lease,
            };
        }

        var deadlineTask = Task.Delay(deadline);
        var cancellationTask = WaitForCancellationAsync(callerCancellation);
        var completed = await Task.WhenAny(work, deadlineTask, cancellationTask).ConfigureAwait(false);
        if (completed == work)
        {
            try
            {
                var value = await work.ConfigureAwait(false);
                if (lease.TryTransition(LaneState.Succeeded))
                    return new Result<T>(value, false, false)
                    {
                        State = LaneState.Succeeded,
                        Lease = lease,
                    };

                lease.RecordLateCompletion();
                return TerminalResultAfterLateCompletion<T>(lease);
            }
            catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
            {
                lease.TryTransition(LaneState.Cancelled);
                return new Result<T>(default, false, true)
                {
                    State = LaneState.Cancelled,
                    Lease = lease,
                };
            }
            catch (Exception ex)
            {
                if (lease.TryTransition(LaneState.Failed))
                    return new Result<T>(default, false, false, ex)
                    {
                        State = LaneState.Failed,
                        Lease = lease,
                    };

                lease.RecordLateFault(ex);
                return TerminalResultAfterLateCompletion<T>(lease);
            }
        }

        // Under heavy scheduler load the cancellation continuation can be observed just after
        // the deadline task, even though the caller token was already cancelled. Caller
        // cancellation is authoritative once observed; do not misclassify it as a timeout.
        var terminal = completed == cancellationTask || callerCancellation.IsCancellationRequested
            ? LaneState.Cancelled
            : LaneState.TimedOut;
        lease.TryTransition(terminal);
        laneCancellation.Cancel(throwOnFirstException: false);
        var observedDetached = ObserveDetachedAsync(work, lease);
        return new Result<T>(default, terminal == LaneState.TimedOut, terminal == LaneState.Cancelled)
        {
            State = terminal,
            Lease = lease,
            DetachedTask = observedDetached,
        };
    }

    private static async Task ObserveDetachedAsync<T>(Task<T> work, LaneLease lease)
    {
        try
        {
            _ = await work.ConfigureAwait(false);
            lease.RecordLateCompletion();
        }
        catch (Exception ex)
        {
            lease.RecordLateFault(ex);
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The task is a race signal, not the caller-facing cancellation operation.
        }
    }

    private static Result<T> TerminalResultAfterLateCompletion<T>(LaneLease lease) =>
        lease.State switch
        {
            LaneState.TimedOut => new Result<T>(default, true, false)
            {
                State = LaneState.TimedOut,
                Lease = lease,
            },
            LaneState.Cancelled => new Result<T>(default, false, true)
            {
                State = LaneState.Cancelled,
                Lease = lease,
            },
            _ => new Result<T>(default, false, false)
            {
                State = lease.State,
                Lease = lease,
            },
        };
}
