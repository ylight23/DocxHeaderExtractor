using System.Diagnostics;
using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Cross-process guard for Accuracy99 live OpenRouter ceiling campaigns. The named
/// mutex is the authority; the small metadata file is only an audit breadcrumb and is never used
/// to infer ownership. An abandoned mutex is safe to take over because Windows has already proved
/// the previous owner died.</summary>
public sealed class A99OpenRouterLiveProviderLease : IDisposable
{
    private const string MutexName = "Global\\DocxHeaderExtractor-A99-OpenRouter-Live";
    private readonly Mutex _mutex;
    private readonly string _metadataPath;
    private bool _owned;

    private A99OpenRouterLiveProviderLease(Mutex mutex, string metadataPath)
    {
        _mutex = mutex;
        _metadataPath = metadataPath;
        _owned = true;
    }

    public bool ConcurrentCampaignsDetected { get; private init; }
    public int ProviderConcurrency => 1;

    public static async Task<A99OpenRouterLiveProviderLease> AcquireAsync(
        string repoRoot, string strategy, string documentId, CancellationToken ct = default)
    {
        var mutex = new Mutex(false, MutexName);
        var metadataPath = Path.Combine(repoRoot, ".a99-openrouter-live-lease.json");
        var detected = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (mutex.WaitOne(0)) break;
            }
            catch (AbandonedMutexException)
            {
                // The OS proved the previous owner died; this process now owns the mutex.
                break;
            }

            detected = true;
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        var owner = new
        {
            pid = Environment.ProcessId,
            processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            worktree = repoRoot,
            strategy,
            documentId,
            acquiredUtc = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(owner), ct).ConfigureAwait(false);
        return new A99OpenRouterLiveProviderLease(mutex, metadataPath) { ConcurrentCampaignsDetected = detected };
    }

    public void Dispose()
    {
        if (!_owned) return;
        _owned = false;
        try
        {
            if (File.Exists(_metadataPath)) File.Delete(_metadataPath);
        }
        catch (IOException) { }
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
