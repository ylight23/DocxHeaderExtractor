using System.Text.RegularExpressions;
using DocxHeaderExtractor.Application.Tasks;

namespace DocxHeaderExtractor.Application.Runtime;

public enum PersistedRunLifecycle
{
    Draft,
    Running,
    Completed,
    NeedsHumanReview,
    Blocked,
    Failed,
    Cancelled,
    Frozen,
}

public sealed record RunStorageKey(string RunId, int SchemaVersion = 1)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RunId))
            throw new ArgumentException("RunId không được rỗng.", nameof(RunId));
        if (SchemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
    }
}

/// <summary>
/// Durable run projection. Implementations may persist JSON, database rows, or another store;
/// the Application contract never persists raw provider payloads or secrets.
/// </summary>
public sealed record PersistedTaskRun(
    RunStorageKey Key,
    string PlanId,
    PersistedRunLifecycle Lifecycle,
    TaskRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    TaskProvenance Provenance,
    TaskFailure? Failure = null);

public interface ITaskRunStore
{
    ValueTask SaveAsync(PersistedTaskRun run, CancellationToken ct = default);
    ValueTask<PersistedTaskRun?> LoadAsync(RunStorageKey key, CancellationToken ct = default);
}

public sealed record TaskTelemetryEvent(
    string RunId,
    string Stage,
    string Outcome,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Dimensions);

public interface ITaskTelemetrySink
{
    ValueTask RecordAsync(TaskTelemetryEvent telemetry, CancellationToken ct = default);
}

public interface ISecretRedactor
{
    string Redact(string value);
}

/// <summary>Conservative redaction for logs/telemetry; it never attempts to recover secret values.</summary>
public sealed class SecretRedactor : ISecretRedactor
{
    private static readonly Regex BearerToken = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignment = new(
        @"(?<name>\b(?:api[_-]?key|token|password|secret)\b\s*[:=]\s*)(?<quote>[""']?)(?<value>[^\s,;""']+)\k<quote>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = BearerToken.Replace(value, "Bearer [REDACTED]");
        return SecretAssignment.Replace(redacted, "${name}${quote}[REDACTED]${quote}");
    }
}
