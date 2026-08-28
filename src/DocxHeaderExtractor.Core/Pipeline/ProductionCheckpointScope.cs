namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Owns the short-lived checkpoint directory for one normal authority invocation.
/// Diagnostic callers provide their own durable path and never use this scope.
/// </summary>
internal sealed class ProductionCheckpointScope : IAsyncDisposable
{
    private bool _disposed;

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

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
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
        return ValueTask.CompletedTask;
    }
}
