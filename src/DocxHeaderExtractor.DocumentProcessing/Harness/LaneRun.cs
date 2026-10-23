namespace DocxHeaderExtractor.DocumentProcessing.Harness;

/// <summary>
/// Runs any lane's work under its lane lease and deadline and returns the value only when the work completed and still holds the lease.
/// Nothing here knows a format, a heading or an agent: every lane, the heading lanes and any later problem, runs through it.
/// </summary>
internal static class LaneRun
{
    /// <summary>What a lane says when its execution does not complete. Each lane keeps its own wording.</summary>
    internal sealed record Messages(string TimedOut, string Failed, string LeaseLost);

    /// <summary>
    /// Runs <paramref name="work"/> under the lane deadline and returns its value only when it completed and still holds its lease.
    /// <paramref name="afterRun"/> runs once the work has stopped, before the outcome is judged (the PDF lane drains its checkpoint there).
    /// </summary>
    internal static async Task<T> CompleteAsync<T>(
        Func<LaneLease, CancellationToken, Task<T>> work,
        TimeSpan deadline,
        Messages messages,
        CancellationToken ct,
        Func<Task>? afterRun = null) where T : class
    {
        var execution = await LaneExecution.RunAsync(work, deadline, ct).ConfigureAwait(false);
        if (afterRun is not null) await afterRun().ConfigureAwait(false);
        if (execution.State == LaneState.TimedOut)
            throw new TimeoutException(messages.TimedOut);
        if (execution.State == LaneState.Cancelled)
            throw new OperationCanceledException(ct);
        if (execution.State == LaneState.Failed)
            throw execution.Fault ?? new InvalidOperationException(messages.Failed);
        if (!execution.Lease.CanPublishCompletedResult || execution.Value is null)
            throw new InvalidOperationException(messages.LeaseLost);
        return execution.Value;
    }
}
