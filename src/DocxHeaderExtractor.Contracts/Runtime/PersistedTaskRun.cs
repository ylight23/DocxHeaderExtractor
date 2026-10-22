using DocxHeaderExtractor.Contracts.Tasks;

namespace DocxHeaderExtractor.Contracts.Runtime;

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
