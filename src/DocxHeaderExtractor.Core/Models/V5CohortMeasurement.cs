namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Hard-pins the first V5 measurement cohort to exactly 31 requests (SRC-089: 7 packs, SRC-095: 24
/// packs) and requires an explicit authorization flag. Separate from <see cref="V5CanaryGate"/>: a
/// canary authorization never authorizes the cohort, and vice versa. Never performs a network call.
/// </summary>
public static class V5CohortGate
{
    public const int CohortRequestCount = 31;

    public static void Authorize(int requestCount, bool providerExecutionAuthorized)
    {
        if (requestCount != CohortRequestCount)
            throw new InvalidOperationException($"cohort-request-count-must-be-exactly-{CohortRequestCount}:{requestCount}");
        if (!providerExecutionAuthorized)
            throw new InvalidOperationException("cohort-provider-execution-not-authorized");
    }
}

/// <summary>One logical provider call's transport facts, as persisted by the cohort runner.</summary>
public sealed record V5CohortCallTransport(
    int Ordinal,
    string DocumentId,
    string PackId,
    bool TransportSucceeded,
    int Attempts,
    string? FinishReason,
    int PromptTokens,
    int CompletionTokens,
    int ReasoningTokens,
    double LatencyMs);

/// <summary>
/// Frozen transport, cost and latency metrics for a cohort. Descriptive only: no threshold, no pass.
/// Percentiles are nearest-rank over logical calls (retries included in a call's latency).
/// </summary>
public sealed record V5CohortTransportAggregate(
    int ProviderCalls,
    int HttpAttempts,
    int TransportFailures,
    int TransportRetries,
    IReadOnlyDictionary<string, int> FinishReasonDistribution,
    int FinishReasonLength,
    long PromptTokens,
    long CompletionTokens,
    long ReasoningTokens,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double TotalWallTimeMs)
{
    public static V5CohortTransportAggregate From(IReadOnlyList<V5CohortCallTransport> calls, double totalWallTimeMs)
    {
        ArgumentNullException.ThrowIfNull(calls);
        var latencies = calls.Select(call => call.LatencyMs).Order().ToArray();
        return new V5CohortTransportAggregate(
            ProviderCalls: calls.Count,
            HttpAttempts: calls.Sum(call => call.Attempts),
            TransportFailures: calls.Count(call => !call.TransportSucceeded),
            TransportRetries: calls.Sum(call => Math.Max(0, call.Attempts - 1)),
            FinishReasonDistribution: calls
                .GroupBy(call => call.FinishReason ?? "(none)", StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            FinishReasonLength: calls.Count(call => call.FinishReason == V5BindingQualifier.FinishReasonLength),
            PromptTokens: calls.Sum(call => (long)call.PromptTokens),
            CompletionTokens: calls.Sum(call => (long)call.CompletionTokens),
            ReasoningTokens: calls.Sum(call => (long)call.ReasoningTokens),
            LatencyP50Ms: NearestRank(latencies, 0.50),
            LatencyP95Ms: NearestRank(latencies, 0.95),
            TotalWallTimeMs: totalWallTimeMs);
    }

    /// <summary>Nearest-rank percentile over an ascending array; 0 for no data.</summary>
    public static double NearestRank(IReadOnlyList<double> ascending, double percentile)
    {
        if (ascending.Count == 0) return 0;
        var rank = (int)Math.Ceiling(percentile * ascending.Count);
        return ascending[Math.Clamp(rank, 1, ascending.Count) - 1];
    }
}
