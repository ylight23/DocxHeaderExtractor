namespace DocxHeaderExtractor.Agents.Transport;

/// <summary>In-process diagnostic metadata for one transport attempt; never sent to the provider.</summary>
public static class ModelAttemptContext
{
    public sealed record Attempt(string Unit, int Turn, int Retry);
    static readonly AsyncLocal<Attempt?> CurrentAttempt = new();
    public static Attempt? Current => CurrentAttempt.Value;

    internal static IDisposable Enter(string unit, int turn, int retry)
    {
        var previous = CurrentAttempt.Value;
        CurrentAttempt.Value = new(unit, turn, retry);
        return new Restore(previous);
    }

    sealed class Restore(Attempt? previous) : IDisposable
    {
        public void Dispose() => CurrentAttempt.Value = previous;
    }
}
