namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Owns the short-lived checkpoint directory for one normal authority invocation.
/// Diagnostic callers provide their own durable path and never use this scope.
/// </summary>
internal sealed class ProductionCheckpointScope : IAsyncDisposable
{
    private bool _disposed;
    private IReadOnlyList<Task> _detachedTasks = [];

    private ProductionCheckpointScope(string directory)
    {
        DirectoryPath = directory;
        CheckpointPath = Path.Combine(directory, "span-checkpoint.jsonl");
    }

    public string DirectoryPath { get; }
    public string CheckpointPath { get; }

    public static ProductionCheckpointScope Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "DocxHeaderExtractor", "authority-runs");
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return new ProductionCheckpointScope(directory);
    }

    public void DeferCleanup(IEnumerable<Task> detachedTasks)
    {
        ArgumentNullException.ThrowIfNull(detachedTasks);
        _detachedTasks = detachedTasks.Where(task => task is not null).Distinct().ToArray();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        if (_detachedTasks.Count > 0)
        {
            _ = CleanupAfterDetachedWorkAsync(_detachedTasks);
            return ValueTask.CompletedTask;
        }

        CleanupNow();
        return ValueTask.CompletedTask;
    }

    internal async Task WaitForCleanupAsync()
    {
        if (_detachedTasks.Count > 0)
            await Task.WhenAll(_detachedTasks).ConfigureAwait(false);
        CleanupNow();
    }

    private async Task CleanupAfterDetachedWorkAsync(IReadOnlyList<Task> detachedTasks)
    {
        try
        {
            await Task.WhenAll(detachedTasks).ConfigureAwait(false);
        }
        catch
        {
            // Lane faults are already observed by PdfLaneExecution. Cleanup remains best effort.
        }
        CleanupNow();
    }

    private void CleanupNow()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
            // A failed cleanup must not turn a completed extraction into a failed extraction.
            // The directory is unique and contains no user data or secrets.
        }
        catch (UnauthorizedAccessException)
        {
            // Same policy for transient antivirus/file-lock interference.
        }
    }
}
