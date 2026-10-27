namespace DocxHeaderExtractor.Agents.Budget;

/// <summary>Shared ceiling of one extraction, including preflights, selectors, grouping, placement and retries.
/// The ambient scope flows through Tasks, is restored on disposal and is never shared between extractions.</summary>
public sealed class RunResourceBudget
{
    static readonly AsyncLocal<RunResourceBudget?> current = new();
    internal static RunResourceBudget? Current => current.Value;
    readonly object gate = new();
    readonly Dictionary<Guid, (double Reserve, double? Cost, bool Unknown)> attempts = [];
    readonly double ceilingUsd;
    readonly int maxAttempts;
    readonly DateTimeOffset? deadline;

    public RunResourceBudget(double ceilingUsd, int maxAttempts, TimeSpan? duration = null, RequestPolicy? policy = null)
    {
        if (!double.IsFinite(ceilingUsd) || ceilingUsd < 0 || maxAttempts < 0 || duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ceilingUsd));
        Policy = policy ?? new RequestPolicy(); Policy.Validate();
        this.ceilingUsd = ceilingUsd; this.maxAttempts = maxAttempts;
        deadline = duration is { } span ? DateTimeOffset.UtcNow + span : null;
    }

    public RequestPolicy Policy { get; }

    public IDisposable Enter()
    {
        var previous = current.Value;
        current.Value = this;
        return new Scope(() => current.Value = previous);
    }

    internal string? Reserve(Guid id, double usd)
    {
        lock (gate)
        {
            if (deadline is { } end && DateTimeOffset.UtcNow >= end) return "RUN_DEADLINE_EXHAUSTED";
            if (attempts.Count >= maxAttempts) return "RUN_CALL_LIMIT";
            if (attempts.Values.Sum(a => a.Cost ?? a.Reserve) + usd > ceilingUsd + 1e-12) return "RUN_RESERVE_OVER_CEILING";
            attempts.Add(id, (usd, null, false));
            return null;
        }
    }

    internal void Settle(Guid id, double? cost)
    {
        lock (gate)
        {
            var entry = attempts[id];
            if (entry.Cost is not null) return;
            attempts[id] = (entry.Reserve, cost, cost is null);
        }
    }

    public Snapshot Observe()
    {
        lock (gate) return new(ceilingUsd, maxAttempts, attempts.Count,
            attempts.Values.Sum(a => a.Cost ?? 0),
            attempts.Values.Where(a => a.Cost is null && a.Unknown).Sum(a => a.Reserve),
            attempts.Values.Where(a => a.Cost is null && !a.Unknown).Sum(a => a.Reserve));
    }

    public sealed record Snapshot(double CeilingUsd, int MaxAttempts, int Attempts,
        double ConfirmedUsd, double ProvisionalUsd, double OutstandingUsd);
    sealed class Scope(Action dispose) : IDisposable
    {
        bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; dispose(); } }
    }
}
