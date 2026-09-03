namespace DocxHeaderExtractor.Application.Capabilities;

public sealed record CapabilityResolutionResult(
    CapabilityDescriptor? Capability,
    string? FailureReason)
{
    public bool IsResolved => Capability is not null && FailureReason is null;

    public static CapabilityResolutionResult Resolved(CapabilityDescriptor capability) =>
        new(capability, null);

    public static CapabilityResolutionResult Failed(string reason) =>
        new(null, reason);
}

/// <summary>Read-only, provider-independent capability catalog with exact fail-closed resolution.</summary>
public interface ICapabilityCatalog
{
    IReadOnlyList<CapabilityDescriptor> Descriptors { get; }
    CapabilityResolutionResult Resolve(string capabilityId);
}
