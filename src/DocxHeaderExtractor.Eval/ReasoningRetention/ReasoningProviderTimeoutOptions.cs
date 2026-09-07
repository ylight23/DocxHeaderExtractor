namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Evaluation-only liveness limits. They protect a long-running diagnostic without changing
/// the semantic task or the production extraction pipeline.
/// </summary>
public sealed class ReasoningProviderTimeoutOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan TotalRequestTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public static ReasoningProviderTimeoutOptions FromEnvironment() => new()
    {
        ConnectTimeout = Read("A99_REASONING_CONNECT_TIMEOUT_SECONDS", 30),
        FirstByteTimeout = Read("A99_REASONING_FIRST_BYTE_TIMEOUT_SECONDS", 180),
        InactivityTimeout = Read("A99_REASONING_INACTIVITY_TIMEOUT_SECONDS", 120),
        TotalRequestTimeout = Read("A99_REASONING_TOTAL_TIMEOUT_SECONDS", 600),
    };

    public void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        if (FirstByteTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FirstByteTimeout));
        if (InactivityTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(InactivityTimeout));
        if (TotalRequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(TotalRequestTimeout));
    }

    private static TimeSpan Read(string name, int defaultSeconds)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(defaultSeconds);
    }
}
