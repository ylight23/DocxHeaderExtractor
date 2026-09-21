namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>The only non-terminal state of one PDF lane execution.</summary>
internal enum PdfLaneExecutionState
{
    Active = 0,
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    Cancelled = 4,
}

internal sealed class PdfExecutionLeaseLostException : InvalidOperationException
{
    public PdfExecutionLeaseLostException()
        : base("The PDF execution lease is no longer active; late provider work was quarantined.")
    {
    }
}

/// <summary>
/// Monotonic lease for one lane attempt. A terminal transition wins exactly once. Checkpoint,
/// publication, and downstream work can admit an operation only while the lease is active.
/// </summary>
internal sealed class PdfLaneExecutionLease
{
    private readonly object _sync = new();
    private PdfLaneExecutionState _state = PdfLaneExecutionState.Active;
    private Exception? _lateFault;
    private int _lateCompletionObserved;
    private int _terminalTransitions;

    public PdfLaneExecutionState State
    {
        get { lock (_sync) return _state; }
    }

    public bool IsActive
    {
        get { lock (_sync) return _state == PdfLaneExecutionState.Active; }
    }

    public int TerminalTransitionCount => Volatile.Read(ref _terminalTransitions);
    public bool LateCompletionObserved => Volatile.Read(ref _lateCompletionObserved) != 0;
    public Exception? LateFault => Volatile.Read(ref _lateFault);
    public bool CanPublishCompletedResult => State == PdfLaneExecutionState.Succeeded &&
        TerminalTransitionCount == 1;

    public bool TryTransition(PdfLaneExecutionState terminal)
    {
        if (terminal is PdfLaneExecutionState.Active)
            throw new ArgumentException("A lane transition must be terminal.", nameof(terminal));

        lock (_sync)
        {
            if (_state != PdfLaneExecutionState.Active) return false;
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
            return _state == PdfLaneExecutionState.Active ? NoopLease.Instance : null;
    }

    public bool TryPublish(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_sync)
        {
            if (_state != PdfLaneExecutionState.Active) return false;
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
internal static class PdfLaneExecution
{
    internal sealed record Result<T>(T? Value, bool TimedOut, bool Cancelled, Exception? Fault = null)
    {
        public PdfLaneExecutionState State { get; init; }
        public PdfLaneExecutionLease Lease { get; init; } = null!;

        /// <summary>Observed wrapper for work that outlived the deadline.</summary>
        public Task? DetachedTask { get; init; }
    }

    public static async Task<Result<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan deadline,
        CancellationToken callerCancellation)
        => await RunAsync((_, ct) => action(ct), deadline, callerCancellation).ConfigureAwait(false);

    public static async Task<Result<T>> RunAsync<T>(
        Func<PdfLaneExecutionLease, CancellationToken, Task<T>> action,
        TimeSpan deadline,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));

        var lease = new PdfLaneExecutionLease();
        if (callerCancellation.IsCancellationRequested)
        {
            lease.TryTransition(PdfLaneExecutionState.Cancelled);
            return new Result<T>(default, false, true)
            {
                State = PdfLaneExecutionState.Cancelled,
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
            lease.TryTransition(PdfLaneExecutionState.Cancelled);
            return new Result<T>(default, false, true)
            {
                State = PdfLaneExecutionState.Cancelled,
                Lease = lease,
            };
        }
        catch (Exception ex)
        {
            lease.TryTransition(PdfLaneExecutionState.Failed);
            return new Result<T>(default, false, false, ex)
            {
                State = PdfLaneExecutionState.Failed,
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
                if (lease.TryTransition(PdfLaneExecutionState.Succeeded))
                    return new Result<T>(value, false, false)
                    {
                        State = PdfLaneExecutionState.Succeeded,
                        Lease = lease,
                    };

                lease.RecordLateCompletion();
                return TerminalResultAfterLateCompletion<T>(lease);
            }
            catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
            {
                lease.TryTransition(PdfLaneExecutionState.Cancelled);
                return new Result<T>(default, false, true)
                {
                    State = PdfLaneExecutionState.Cancelled,
                    Lease = lease,
                };
            }
            catch (Exception ex)
            {
                if (lease.TryTransition(PdfLaneExecutionState.Failed))
                    return new Result<T>(default, false, false, ex)
                    {
                        State = PdfLaneExecutionState.Failed,
                        Lease = lease,
                    };

                lease.RecordLateFault(ex);
                return TerminalResultAfterLateCompletion<T>(lease);
            }
        }

        var terminal = completed == cancellationTask
            ? PdfLaneExecutionState.Cancelled
            : PdfLaneExecutionState.TimedOut;
        lease.TryTransition(terminal);
        laneCancellation.Cancel(throwOnFirstException: false);
        var observedDetached = ObserveDetachedAsync(work, lease);
        return new Result<T>(default, terminal == PdfLaneExecutionState.TimedOut, terminal == PdfLaneExecutionState.Cancelled)
        {
            State = terminal,
            Lease = lease,
            DetachedTask = observedDetached,
        };
    }

    private static async Task ObserveDetachedAsync<T>(Task<T> work, PdfLaneExecutionLease lease)
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

    private static Result<T> TerminalResultAfterLateCompletion<T>(PdfLaneExecutionLease lease) =>
        lease.State switch
        {
            PdfLaneExecutionState.TimedOut => new Result<T>(default, true, false)
            {
                State = PdfLaneExecutionState.TimedOut,
                Lease = lease,
            },
            PdfLaneExecutionState.Cancelled => new Result<T>(default, false, true)
            {
                State = PdfLaneExecutionState.Cancelled,
                Lease = lease,
            },
            _ => new Result<T>(default, false, false)
            {
                State = lease.State,
                Lease = lease,
            },
        };
}
