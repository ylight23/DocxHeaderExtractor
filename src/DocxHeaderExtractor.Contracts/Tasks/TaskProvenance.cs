namespace DocxHeaderExtractor.Contracts.Tasks;

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
