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
