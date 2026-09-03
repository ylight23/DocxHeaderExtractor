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

public sealed record RetryPolicy(
    int MaxAttempts = 1,
    TimeSpan? InitialBackoff = null,
    bool RetryProviderFaults = false)
{
    public static RetryPolicy None { get; } = new();

    public void Validate()
    {
        if (MaxAttempts is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts phải nằm trong khoảng 1..8.");
        if (InitialBackoff < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialBackoff), "InitialBackoff không được âm.");
    }
}
