namespace DocxHeaderExtractor.Agents.Transport;

/// <summary>Opt-in, in-process protocol verdicts. No document content or credentials are sent to the sink.</summary>
public static class ProtocolDiagnosticContext
{
    public sealed record Verdict(string Unit, int Turn, string? RequestSha256, string Status, string? Failure);
    static readonly AsyncLocal<Action<Verdict>?> Sink = new();

    public static IDisposable Begin(Action<Verdict> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var previous = Sink.Value;
        Sink.Value = sink;
        return new Restore(previous);
    }

    public static void Report(string unit, int turn, string? requestSha256, string status, string? failure = null) =>
        Sink.Value?.Invoke(new(unit, turn, requestSha256, status, failure));

    sealed class Restore(Action<Verdict>? previous) : IDisposable
    {
        public void Dispose() => Sink.Value = previous;
    }
}
