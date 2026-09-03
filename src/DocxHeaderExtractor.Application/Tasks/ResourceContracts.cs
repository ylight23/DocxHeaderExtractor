namespace DocxHeaderExtractor.Application.Tasks;

public enum InputResourceKind
{
    Document,
    Image,
    Text,
    Data,
    Other,
}

/// <summary>
/// An opaque resource reference. Application code does not assume a local filesystem path; a host
/// or infrastructure resolver owns the locator and its lifetime.
/// </summary>
public sealed record InputResource(
    string ResourceId,
    InputResourceKind Kind,
    string Name,
    string MediaType,
    string Locator);

public sealed record AgentTaskPermissions(
    bool AllowExternalDataTransfer = false,
    bool AllowExternalMutation = false);

public sealed record TaskBudget(
    int? MaxSteps = null,
    int? MaxProviderCalls = null,
    long? MaxInputBytes = null,
    TimeSpan? MaxWallTime = null);

/// <summary>
/// Generic task request. Multiple resources are first-class; document-specific request types are
/// compatibility adapters at the outer boundary.
/// </summary>
public sealed record AgentTaskRequest(
    string UserPrompt,
    IReadOnlyList<InputResource> Resources,
    AgentTaskPermissions Permissions,
    string? RequestedAction = null,
    string OutputPreference = "default",
    TaskBudget? Budget = null,
    string? IdempotencyKey = null)
{
    public AgentTaskRequest(string userPrompt, IEnumerable<InputResource> resources)
        : this(userPrompt, resources.ToArray(), new AgentTaskPermissions())
    {
    }
}

public sealed record ResolvedInputResource(
    InputResource Resource,
    Stream Content,
    bool LeaveOpen);

public interface IInputResourceResolver
{
    ValueTask<ResolvedInputResource> ResolveAsync(
        InputResource resource,
        CancellationToken cancellationToken = default);
}
