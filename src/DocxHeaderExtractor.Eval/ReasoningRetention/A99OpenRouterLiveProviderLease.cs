using System.Diagnostics;
using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Cross-process guard for Accuracy99 live OpenRouter ceiling campaigns. The named
/// semaphore is the authority; the small metadata file is only an audit breadcrumb and is never
/// used to infer ownership. A semaphore is used instead of a mutex because async provider work
/// resumes on arbitrary thread-pool threads and Windows mutex release is thread-affine.</summary>
public sealed class A99OpenRouterLiveProviderLease : IDisposable
{
    private const string SemaphoreName = "Global\\DocxHeaderExtractor-A99-OpenRouter-Live";
    private readonly Semaphore _semaphore;
    private readonly string _metadataPath;
    private bool _owned;

    private A99OpenRouterLiveProviderLease(Semaphore semaphore, string metadataPath)
    {
        _semaphore = semaphore;
        _metadataPath = metadataPath;
        _owned = true;
    }

    public bool ConcurrentCampaignsDetected { get; private init; }
    public int ProviderConcurrency => 1;

    public static async Task<A99OpenRouterLiveProviderLease> AcquireAsync(
        string repoRoot, string strategy, string documentId, CancellationToken ct = default)
    {
        var semaphore = new Semaphore(1, 1, SemaphoreName);
        var metadataPath = Path.Combine(repoRoot, ".a99-openrouter-live-lease.json");
        var detected = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (semaphore.WaitOne(0)) break;

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
        return new A99OpenRouterLiveProviderLease(semaphore, metadataPath) { ConcurrentCampaignsDetected = detected };
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
        _semaphore.Release();
        _semaphore.Dispose();
    }
}
