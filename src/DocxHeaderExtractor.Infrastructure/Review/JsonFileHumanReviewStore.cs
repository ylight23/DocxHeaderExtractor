using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Application.Review;

namespace DocxHeaderExtractor.Infrastructure.Review;

public sealed class JsonFileHumanReviewStore : IHumanReviewStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private readonly string _rootDirectory;
    private readonly JsonSerializerOptions _json;

    public JsonFileHumanReviewStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Review root directory is required.", nameof(rootDirectory));

        _rootDirectory = Path.GetFullPath(rootDirectory);
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        _json.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task PublishAsync(DocumentReviewResult review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        var paths = Paths(review.DocumentId);
        await WithLockAsync(paths.LockKey, async () =>
        {
            Directory.CreateDirectory(paths.Directory);
            var payload = JsonSerializer.Serialize(review, _json);
            if (File.Exists(paths.Review))
            {
                var existing = await File.ReadAllTextAsync(paths.Review, cancellationToken).ConfigureAwait(false);
                using var existingDocument = JsonDocument.Parse(existing);
                using var incomingDocument = JsonDocument.Parse(payload);
                if (JsonElement.DeepEquals(existingDocument.RootElement, incomingDocument.RootElement))
                    return;
                throw new InvalidOperationException("review-snapshot-conflict");
            }

            var temporary = Path.Combine(paths.Directory, $"review.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporary, payload, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                File.Move(temporary, paths.Review);
            }
            finally
            {
                TryDelete(temporary);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentReviewResult?> GetReviewAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        var paths = Paths(documentId);
        if (!File.Exists(paths.Review)) return null;
        await using var stream = File.OpenRead(paths.Review);
        return await JsonSerializer.DeserializeAsync<DocumentReviewResult>(stream, _json, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AppendAsync(
        HumanReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var paths = Paths(record.DocumentId);
        await WithLockAsync(paths.LockKey, async () =>
        {
            Directory.CreateDirectory(paths.Directory);
            await using var stream = new FileStream(
                paths.Decisions,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, _json)).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HumanReviewRecord>> GetRecordsAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        var paths = Paths(documentId);
        if (!File.Exists(paths.Decisions)) return [];

        var records = new List<HumanReviewRecord>();
        using var reader = new StreamReader(paths.Decisions, new UTF8Encoding(false));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = JsonSerializer.Deserialize<HumanReviewRecord>(line, _json)
                ?? throw new InvalidDataException("review-record-empty");
            records.Add(record);
        }
        return records;
    }

    private ReviewPaths Paths(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentId)))
            .ToLowerInvariant();
        var directory = Path.Combine(_rootDirectory, hash);
        return new ReviewPaths(directory, Path.Combine(directory, "review.json"),
            Path.Combine(directory, "decisions.jsonl"), $"{_rootDirectory}:{hash}");
    }

    private static async Task WithLockAsync(
        string key,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private sealed record ReviewPaths(string Directory, string Review, string Decisions, string LockKey);
}
