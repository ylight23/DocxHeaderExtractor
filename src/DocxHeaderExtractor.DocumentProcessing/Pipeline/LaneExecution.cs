namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>The only non-terminal state of one PDF lane execution.</summary>
internal enum LaneExecutionState
{
    Active = 0,
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    Cancelled = 4,
}

internal sealed class LaneLeaseLostException : InvalidOperationException
{
    public LaneLeaseLostException()
        : base("The PDF execution lease is no longer active; late provider work was quarantined.")
    {
    }
}

/// <summary>
/// Monotonic lease for one lane attempt. A terminal transition wins exactly once. Checkpoint,
/// publication, and downstream work can admit an operation only while the lease is active.
/// </summary>
internal sealed class LaneExecutionLease
{
    private readonly object _sync = new();
    private LaneExecutionState _state = LaneExecutionState.Active;
    private Exception? _lateFault;
    private int _lateCompletionObserved;
    private int _terminalTransitions;

    public LaneExecutionState State
    {
        get { lock (_sync) return _state; }
    }

    public bool IsActive
    {
        get { lock (_sync) return _state == LaneExecutionState.Active; }
    }

    public int TerminalTransitionCount => Volatile.Read(ref _terminalTransitions);
    public bool CanPublishCompletedResult => State == LaneExecutionState.Succeeded &&
        TerminalTransitionCount == 1;

    public bool TryTransition(LaneExecutionState terminal)
    {
        if (terminal is LaneExecutionState.Active)
            throw new ArgumentException("A lane transition must be terminal.", nameof(terminal));

        lock (_sync)
        {
            if (_state != LaneExecutionState.Active) return false;
            _state = terminal;
            Interlocked.Increment(ref _terminalTransitions);
            return true;
        }
    }

    /// <summary>
    /// Admits one operation that began while the lane was active. If the terminal transition wins
    /// first, the caller must quarantine the operation instead of mutating canonical state.
    /// </summary>
    public IDisposable? TryAdmitOperation()
    {
        lock (_sync)
            return _state == LaneExecutionState.Active ? NoopLease.Instance : null;
    }

    public bool TryPublish(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_sync)
        {
            if (_state != LaneExecutionState.Active) return false;
            publish();
            return true;
        }
    }

    public bool TryStartDownstream(Action start) => TryPublish(start);

    internal void RecordLateCompletion()
    {
        Interlocked.Exchange(ref _lateCompletionObserved, 1);
    }

    internal void RecordLateFault(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Interlocked.CompareExchange(ref _lateFault, exception, null);
        RecordLateCompletion();
    }

    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Runs one lane under a hard wall-clock budget. A timeout/cancellation returns without awaiting
/// work that ignored cancellation, but the detached work is always observed and its late output is
/// quarantined behind the execution lease.
/// </summary>
internal static class LaneExecution
{
    internal sealed record Result<T>(T? Value, bool TimedOut, bool Cancelled, Exception? Fault = null)
    {
        public LaneExecutionState State { get; init; }
        public LaneExecutionLease Lease { get; init; } = null!;

        /// <summary>Observed wrapper for work that outlived the deadline.</summary>
        public Task? DetachedTask { get; init; }
    }

    public static async Task<Result<T>> RunAsync<T>(
        Func<LaneExecutionLease, CancellationToken, Task<T>> action,
        TimeSpan deadline,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));

        var lease = new LaneExecutionLease();
        if (callerCancellation.IsCancellationRequested)
        {
            lease.TryTransition(LaneExecutionState.Cancelled);
            return new Result<T>(default, false, true)
            {
                State = LaneExecutionState.Cancelled,
                Lease = lease,
            };
        }

        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        Task<T> work;
        try
        {
            work = action(lease, laneCancellation.Token) ??
                throw new InvalidOperationException("The PDF lane action returned a null task.");
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            lease.TryTransition(LaneExecutionState.Cancelled);
            return new Result<T>(default, false, true)
            {
                State = LaneExecutionState.Cancelled,
                Lease = lease,
            };
        }
        catch (Exception ex)
        {
            lease.TryTransition(LaneExecutionState.Failed);
            return new Result<T>(default, false, false, ex)
            {
                State = LaneExecutionState.Failed,
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
                if (lease.TryTransition(LaneExecutionState.Succeeded))
                    return new Result<T>(value, false, false)
                    {
                        State = LaneExecutionState.Succeeded,
                        Lease = lease,
                    };

                lease.RecordLateCompletion();
                return TerminalResultAfterLateCompletion<T>(lease);
            }
            catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
            {
                lease.TryTransition(LaneExecutionState.Cancelled);
                return new Result<T>(default, false, true)
                {
                    State = LaneExecutionState.Cancelled,
                    Lease = lease,
                };
            }
            catch (Exception ex)
            {
                if (lease.TryTransition(LaneExecutionState.Failed))
                    return new Result<T>(default, false, false, ex)
                    {
                        State = LaneExecutionState.Failed,
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
            ? LaneExecutionState.Cancelled
            : LaneExecutionState.TimedOut;
        lease.TryTransition(terminal);
        laneCancellation.Cancel(throwOnFirstException: false);
        var observedDetached = ObserveDetachedAsync(work, lease);
        return new Result<T>(default, terminal == LaneExecutionState.TimedOut, terminal == LaneExecutionState.Cancelled)
        {
            State = terminal,
            Lease = lease,
            DetachedTask = observedDetached,
        };
    }

    private static async Task ObserveDetachedAsync<T>(Task<T> work, LaneExecutionLease lease)
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

    private static Result<T> TerminalResultAfterLateCompletion<T>(LaneExecutionLease lease) =>
        lease.State switch
        {
            LaneExecutionState.TimedOut => new Result<T>(default, true, false)
            {
                State = LaneExecutionState.TimedOut,
                Lease = lease,
            },
            LaneExecutionState.Cancelled => new Result<T>(default, false, true)
            {
                State = LaneExecutionState.Cancelled,
                Lease = lease,
            },
            _ => new Result<T>(default, false, false)
            {
                State = lease.State,
                Lease = lease,
            },
        };
}
