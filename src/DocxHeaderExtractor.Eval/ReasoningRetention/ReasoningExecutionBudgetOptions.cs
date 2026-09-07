namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Evaluation-only parent budgets. Child operations must consume the deadline supplied by
/// their parent; production provider configuration is intentionally not changed here.
/// </summary>
public sealed record ReasoningExecutionBudgetOptions
{
    public TimeSpan AttemptTotalTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan SemanticPassTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan DocumentTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan RouteTimeout { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan RepeatTimeout { get; init; } = TimeSpan.FromHours(3);
    public TimeSpan CampaignTimeout { get; init; } = TimeSpan.FromHours(12);
    public int MaxTransientRetries { get; init; } = 2;

    public static ReasoningExecutionBudgetOptions FromEnvironment(ReasoningProviderTimeoutOptions providerTimeouts) => new()
    {
        AttemptTotalTimeout = providerTimeouts.TotalRequestTimeout,
        SemanticPassTimeout = Read("A99_REASONING_SEMANTIC_PASS_TIMEOUT_SECONDS", 600),
        DocumentTimeout = Read("A99_REASONING_DOCUMENT_TIMEOUT_SECONDS", 1_800),
        RouteTimeout = Read("A99_REASONING_ROUTE_TIMEOUT_SECONDS", 3_600),
        RepeatTimeout = Read("A99_REASONING_REPEAT_TIMEOUT_SECONDS", 10_800),
        CampaignTimeout = Read("A99_REASONING_CAMPAIGN_TIMEOUT_SECONDS", 43_200),
        MaxTransientRetries = ReadInt("A99_REASONING_MAX_TRANSIENT_RETRIES", 2, 0, 8),
    };

    public void Validate()
    {
        if (AttemptTotalTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(AttemptTotalTimeout));
        if (SemanticPassTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(SemanticPassTimeout));
        if (DocumentTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(DocumentTimeout));
        if (RouteTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RouteTimeout));
        if (RepeatTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RepeatTimeout));
        if (CampaignTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CampaignTimeout));
        if (MaxTransientRetries < 0) throw new ArgumentOutOfRangeException(nameof(MaxTransientRetries));
    }

    private static TimeSpan Read(string name, int defaultSeconds) =>
        TimeSpan.FromSeconds(ReadInt(name, defaultSeconds, 1, 86_400 * 7));

    private static int ReadInt(string name, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : defaultValue;
    }
}
