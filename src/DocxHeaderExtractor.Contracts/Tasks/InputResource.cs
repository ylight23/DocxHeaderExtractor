namespace DocxHeaderExtractor.Contracts.Tasks;

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
