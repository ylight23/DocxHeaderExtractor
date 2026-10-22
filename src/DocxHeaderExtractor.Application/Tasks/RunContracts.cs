namespace DocxHeaderExtractor.Application.Tasks;

public enum TaskRunStatus
{
    Started,
    Completed,
    NeedsHumanReview,
    Blocked,
    Failed,
    Cancelled,
}

public enum TaskFailureKind
{
    None,
    Validation,
    PolicyDenied,
    Capability,
    Provider,
    Timeout,
    Cancelled,
    Unknown,
}

public sealed record TaskFailure(
    TaskFailureKind Kind,
    string Code,
    string Message,
    string? Stage = null);

/// <summary>
/// Sanitized evidence about a run. Secrets and raw provider payloads do not belong here.
/// </summary>
public sealed record TaskProvenance(
    string? SourceIdentity,
    string? CapabilityId,
    string? ProviderId,
    string? ModelId,
    bool ExternalDataTransferred,
    string Authority);
