namespace DocxHeaderExtractor.Application.Tasks;

/// <summary>Typed provider failure that may be retried only when policy explicitly allows it.</summary>
public sealed class ProviderCallException : Exception
{
    public ProviderCallException(string code, string message, bool isTransient, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "provider-fault" : code;
        IsTransient = isTransient;
    }

    public string Code { get; }

    public bool IsTransient { get; }
}

/// <summary>
/// Application retry seam. It never retries arbitrary exceptions or cancellation and has no
/// provider-specific knowledge beyond the typed <see cref="ProviderCallException"/> contract.
/// </summary>
public static class TaskRetryExecutor
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderCallException ex)
            {
                if (!policy.RetryProviderFaults || !ex.IsTransient || attempt >= policy.MaxAttempts)
                    throw;
                await DelayAsync(policy.InitialBackoff, attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Task DelayAsync(TimeSpan? initialBackoff, int attempt, CancellationToken ct)
    {
        if (initialBackoff is not { } delay || delay == TimeSpan.Zero)
            return Task.CompletedTask;

        var ticks = Math.Min(delay.Ticks * attempt, TimeSpan.FromMinutes(1).Ticks);
        return Task.Delay(TimeSpan.FromTicks(ticks), ct);
    }
}
