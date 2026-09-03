using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Application.Runtime;

namespace DocxHeaderExtractor.Infrastructure.Runtime;

/// <summary>
/// File-backed implementation of the application run-store port.
/// Each run is addressed by a hash of its logical storage key, so a RunId can never escape
/// the configured directory as a path component. Writes are replaced atomically after the
/// complete JSON document has been flushed.
/// </summary>
public sealed class JsonFileTaskRunStore : ITaskRunStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileTaskRunStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Thư mục lưu run không được rỗng.", nameof(directory));

        _directory = Path.GetFullPath(directory);
    }

    public async ValueTask SaveAsync(PersistedTaskRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        run.Key.Validate();
        if (string.IsNullOrWhiteSpace(run.PlanId))
            throw new ArgumentException("PlanId không được rỗng.", nameof(run));

        var path = GetPath(run.Key);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var payload = JsonSerializer.SerializeToUtf8Bytes(run, JsonOptions);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 16 * 1024, options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            _gate.Release();
        }
    }

    public async ValueTask<PersistedTaskRun?> LoadAsync(RunStorageKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Validate();
        var path = GetPath(key);
        if (!File.Exists(path)) return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 16 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<PersistedTaskRun>(stream, JsonOptions, ct)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private string GetPath(RunStorageKey key)
    {
        var logicalKey = $"{key.SchemaVersion}:{key.RunId}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logicalKey)))
            .ToLowerInvariant();
        return Path.Combine(_directory, $"run-{digest}.json");
    }
}
