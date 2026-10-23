namespace DocxHeaderExtractor.DocumentProcessing.Harness;

/// <summary>
/// Monotonic lease for one lane attempt. A terminal transition wins exactly once. Checkpoint,
/// publication, and downstream work can admit an operation only while the lease is active.
/// </summary>
internal sealed class LaneLease
{
    private readonly object _sync = new();
    private LaneState _state = LaneState.Active;
    private Exception? _lateFault;
    private int _lateCompletionObserved;
    private int _terminalTransitions;

    public LaneState State
    {
        get { lock (_sync) return _state; }
    }

    public bool IsActive
    {
        get { lock (_sync) return _state == LaneState.Active; }
    }

    public int TerminalTransitionCount => Volatile.Read(ref _terminalTransitions);
    public bool CanPublishCompletedResult => State == LaneState.Succeeded &&
        TerminalTransitionCount == 1;

    public bool TryTransition(LaneState terminal)
    {
        if (terminal is LaneState.Active)
            throw new ArgumentException("A lane transition must be terminal.", nameof(terminal));

        lock (_sync)
        {
            if (_state != LaneState.Active) return false;
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
            return _state == LaneState.Active ? NoopLease.Instance : null;
    }

    public bool TryPublish(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_sync)
        {
            if (_state != LaneState.Active) return false;
            publish();
            return true;
        }
    }

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
