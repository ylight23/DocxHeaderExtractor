using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.Agents.Transport;

/// <summary>Process-wide concurrency and 429 cooldown per account on the locked provider route.
/// Only a hash of the account key is retained. No permit is held during backoff.</summary>
internal sealed class ProviderRequestGate(int concurrency)
{
    static readonly ConcurrentDictionary<string, ProviderRequestGate> accounts = new(StringComparer.Ordinal);
    readonly SemaphoreSlim slots = new(concurrency, concurrency);
    readonly object gate = new();
    DateTimeOffset blockedUntil;

    public static ProviderRequestGate ForAccount(string apiKey) => accounts.GetOrAdd(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))),
        _ => new(int.TryParse(Environment.GetEnvironmentVariable("DHX_OPENROUTER_MAX_CONCURRENCY"), out var n) && n > 0 ? n : 8));

    public void CoolDown(TimeSpan delay)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var until = delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
            if (until > blockedUntil) blockedUntil = until;
        }
    }

    public IDisposable Enter(TimeSpan waitBudget, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = waitBudget - clock.Elapsed;
            if (remaining <= TimeSpan.Zero || !slots.Wait(remaining, ct)) throw new ReplyWaitLimit();
            TimeSpan cooldown;
            lock (gate) cooldown = blockedUntil - DateTimeOffset.UtcNow;
            if (cooldown <= TimeSpan.Zero) return new Lease(slots);
            slots.Release();
            remaining = waitBudget - clock.Elapsed;
            if (cooldown >= remaining) throw new ReplyWaitLimit();
            Wait(cooldown, ct);
        }
    }

    internal static void Wait(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (delay > TimeSpan.Zero && ct.WaitHandle.WaitOne(delay)) ct.ThrowIfCancellationRequested();
    }

    sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) semaphore.Release(); }
    }
    internal sealed class ReplyWaitLimit : Exception { }
}
