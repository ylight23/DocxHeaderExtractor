using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Application.Runtime;

namespace DocxHeaderExtractor.Infrastructure.Runtime;

/// <summary>
/// Append-only JSONL telemetry adapter. Dimension values are redacted at the infrastructure
/// boundary; raw provider payloads are not accepted by the application telemetry contract.
/// </summary>
public sealed class JsonLinesTaskTelemetrySink : ITaskTelemetrySink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly ISecretRedactor _redactor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLinesTaskTelemetrySink(string path, ISecretRedactor? redactor = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Đường dẫn telemetry không được rỗng.", nameof(path));

        _path = Path.GetFullPath(path);
        _redactor = redactor ?? new SecretRedactor();
    }

    public async ValueTask RecordAsync(TaskTelemetryEvent telemetry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        var sanitized = telemetry with
        {
            Dimensions = telemetry.Dimensions.ToDictionary(
                pair => pair.Key,
                pair => _redactor.Redact(pair.Value),
                StringComparer.Ordinal),
        };
        var line = JsonSerializer.Serialize(sanitized, JsonOptions) + Environment.NewLine;
        var payload = Encoding.UTF8.GetBytes(line);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 16 * 1024, options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
