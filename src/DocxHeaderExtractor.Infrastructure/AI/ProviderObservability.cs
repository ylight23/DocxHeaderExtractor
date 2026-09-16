using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// Execution-only provider telemetry. It deliberately stores request shape and hashes, never
/// prompt/content payloads. Every critical record is flushed synchronously so a parent watchdog
/// can reconstruct progress after killing the worker process tree.
/// </summary>
public sealed record ProviderObservabilityOptions
{
    public required string RootDirectory { get; init; }
    public required string CampaignId { get; init; }
    public required string DocumentId { get; init; }
    public string? StagePrefix { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public int HeartbeatSeconds { get; init; } = 5;
}

public sealed record ProviderLogicalCallMetadata
{
    public required string Stage { get; init; }
    public required string LogicalCallId { get; init; }
    public required string RequestHash { get; init; }
    public required int RequestBytes { get; init; }
    public required int EstimatedInputTokens { get; init; }
    public required int MaxOutputTokens { get; init; }
    public int CandidateCount { get; init; }
    public int CurrentNodeCount { get; init; }
    public int ContextItemCount { get; init; }
    public int ContextCharacterCount { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
}

public sealed class ProviderCallTelemetry : IDisposable
{
    private static readonly ConcurrentDictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProviderObservabilityOptions _options;
    private readonly ProviderLogicalCallMetadata _metadata;
    private readonly string _directory;
    private readonly object _gate = new();
    private Timer? _heartbeat;
    private int _sequence;
    private string _stage;
    private string? _attemptId;
    private bool _terminal;
    private bool _disposed;
    private readonly int _callOrdinal;
    private string _lastMilestone = "NONE";

    private ProviderCallTelemetry(ProviderObservabilityOptions options, ProviderLogicalCallMetadata metadata)
    {
        _options = options;
        _metadata = metadata;
        _stage = metadata.Stage;
        _callOrdinal = Ordinals.AddOrUpdate(options.RootDirectory, 1, (_, value) => value + 1);
        _directory = Path.Combine(options.RootDirectory, "telemetry");
        Directory.CreateDirectory(_directory);
        WriteNew($"logical-call.started.{Safe(metadata.LogicalCallId)}.json", new
        {
            schemaVersion = "provider-observability-v1",
            eventType = "LOGICAL_CALL_STARTED",
            timestamp = UtcNow(),
            campaignId = options.CampaignId,
            documentId = options.DocumentId,
            stage = metadata.Stage,
            logicalCallId = metadata.LogicalCallId,
            callOrdinal = _callOrdinal,
            requestHash = metadata.RequestHash,
            requestBytes = metadata.RequestBytes,
            estimatedInputTokens = metadata.EstimatedInputTokens,
            maxOutputTokens = metadata.MaxOutputTokens,
            candidateCount = metadata.CandidateCount,
            currentNodeCount = metadata.CurrentNodeCount,
            contextItemCount = metadata.ContextItemCount,
            contextCharacterCount = metadata.ContextCharacterCount,
            provider = metadata.Provider ?? options.Provider,
            model = metadata.Model ?? options.Model,
        });
        Event("LOGICAL_CALL_STARTED", new { requestHash = metadata.RequestHash, requestBytes = metadata.RequestBytes });
        Heartbeat();
        _heartbeat = new Timer(_ => Heartbeat(), null,
            TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatSeconds)),
            TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatSeconds)));
    }

    public static ProviderCallTelemetry? Start(
        ProviderObservabilityOptions? options,
        ProviderLogicalCallMetadata metadata)
    {
        if (options is null) return null;
        return new ProviderCallTelemetry(options, metadata);
    }

    public ProviderAttemptTelemetry StartAttempt(
        string attemptId,
        string requestHash,
        int requestBytes,
        int estimatedInputTokens,
        int maxOutputTokens)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _attemptId = attemptId;
            _stage = _metadata.Stage;
            var attempt = new ProviderAttemptTelemetry(this, attemptId, requestHash, requestBytes,
                estimatedInputTokens, maxOutputTokens);
            attempt.Event("TRANSPORT_START");
            attempt.Event("NOT_OBSERVABLE_WITH_CURRENT_TRANSPORT", new { milestone = "CONNECTION_ESTABLISHED" });
            attempt.Event("NOT_OBSERVABLE_WITH_CURRENT_TRANSPORT", new { milestone = "REQUEST_HEADERS_SENT" });
            attempt.Event("NOT_OBSERVABLE_WITH_CURRENT_TRANSPORT", new { milestone = "REQUEST_BODY_SENT" });
            return attempt;
        }
    }

    internal void Event(string eventType, object? data = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var payload = new Dictionary<string, object?>
            {
                ["schemaVersion"] = "provider-observability-v1",
                ["eventType"] = eventType,
                ["sequence"] = ++_sequence,
                ["timestamp"] = UtcNow(),
                ["campaignId"] = _options.CampaignId,
                ["documentId"] = _options.DocumentId,
                ["stage"] = _stage,
                ["logicalCallId"] = _metadata.LogicalCallId,
                ["callOrdinal"] = _callOrdinal,
                ["attemptId"] = _attemptId,
            };
            if (!string.Equals(eventType, "HEARTBEAT", StringComparison.Ordinal))
                _lastMilestone = eventType;
            if (data is not null)
                foreach (var property in JsonSerializer.SerializeToElement(data).EnumerateObject())
                    payload[property.Name] = property.Value.Clone();
            Append("events.jsonl", payload);
        }
    }

    public void Complete(object? data = null)
    {
        lock (_gate)
        {
            if (_terminal || _disposed) return;
            _terminal = true;
            Event("LOGICAL_CALL_COMPLETED", data);
        }
    }

    public void Fail(string reason, object? data = null)
    {
        lock (_gate)
        {
            if (_terminal || _disposed) return;
            _terminal = true;
            Event("LOGICAL_CALL_FAILED", new { reason, data });
        }
    }

    private void Heartbeat()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                Append("heartbeats.jsonl", new
                {
                    schemaVersion = "provider-observability-v1",
                    eventType = "HEARTBEAT",
                    sequence = ++_sequence,
                    timestamp = UtcNow(),
                    campaignId = _options.CampaignId,
                    documentId = _options.DocumentId,
                    stage = _stage,
                    logicalCallId = _metadata.LogicalCallId,
                    callOrdinal = _callOrdinal,
                    attemptId = _attemptId,
                    lastTransportMilestone = _lastMilestone,
                    terminal = _terminal,
                });
            }
        }
        catch
        {
            // Telemetry must never change provider semantics or turn a provider call into a retry.
        }
    }

    private void WriteNew(string name, object value)
    {
        var path = Path.Combine(_directory, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(true);
    }

    private void Append(string name, object value)
    {
        var path = Path.Combine(_directory, name);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
            4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, value);
        stream.WriteByte((byte)'\n');
        stream.Flush(true);
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");
    private static string Safe(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(ProviderCallTelemetry)); }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_terminal) Event("LOGICAL_CALL_FAILED", new { reason = "DISPOSED_WITHOUT_TERMINAL" });
            _heartbeat?.Dispose();
            _disposed = true;
        }
    }

    public sealed class ProviderAttemptTelemetry : IDisposable
    {
        private readonly ProviderCallTelemetry _parent;
        private bool _completed;

        internal ProviderAttemptTelemetry(ProviderCallTelemetry parent, string attemptId, string requestHash,
            int requestBytes, int estimatedInputTokens, int maxOutputTokens)
        {
            _parent = parent;
            AttemptId = attemptId;
            _parent.Event("ATTEMPT_STARTED", new
            {
                attemptId,
                requestHash,
                requestBytes,
                estimatedInputTokens,
                maxOutputTokens,
            });
            _parent.WriteAttemptStarted(AttemptId, requestHash, requestBytes, estimatedInputTokens, maxOutputTokens);
        }

        public string AttemptId { get; }
        public void Event(string eventType, object? data = null) => _parent.Event(eventType, data);
        public void PersistRawResponse(string responseText) => _parent.WriteText($"response.raw.{Safe(AttemptId)}.txt", responseText);
        public void PersistParsed(object parsed) => _parent.WriteNew($"response.parsed.{Safe(AttemptId)}.json", parsed);
        public void PersistBinding(object binding) => _parent.WriteNew($"response.binding.{Safe(AttemptId)}.json", binding);
        public void Complete(object? data = null)
        {
            if (_completed) return;
            _completed = true;
            Event("ATTEMPT_COMPLETED", data);
        }
        public void Fail(string reason, object? data = null)
        {
            if (_completed) return;
            _completed = true;
            Event("ATTEMPT_FAILED", new { reason, data });
        }
        public void Dispose() { if (!_completed) Fail("DISPOSED_WITHOUT_TERMINAL"); }
    }

    private void WriteAttemptStarted(string attemptId, string requestHash, int requestBytes,
        int estimatedInputTokens, int maxOutputTokens)
    {
        WriteNew($"attempt.started.{Safe(attemptId)}.json", new
        {
            schemaVersion = "provider-observability-v1",
            eventType = "ATTEMPT_STARTED",
            timestamp = UtcNow(),
            campaignId = _options.CampaignId,
            documentId = _options.DocumentId,
            stage = _metadata.Stage,
            logicalCallId = _metadata.LogicalCallId,
            callOrdinal = _callOrdinal,
            attemptId,
            requestHash,
            requestBytes,
            estimatedInputTokens,
            maxOutputTokens,
        });
    }

    private void WriteText(string name, string value)
    {
        var path = Path.Combine(_directory, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.Write(value);
        writer.Flush();
        stream.Flush(true);
    }
}

public static class ProviderObservabilityHashing
{
    public static string Sha256Utf8(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Sha256Bytes(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    public static int EstimateTokens(string value) => Math.Max(1, (int)Math.Ceiling(value.Length / 4d));
}
