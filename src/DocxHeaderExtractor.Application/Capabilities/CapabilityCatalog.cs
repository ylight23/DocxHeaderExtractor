namespace DocxHeaderExtractor.Application.Capabilities;

/// <summary>
/// Generic capability catalog. Registration is deterministic and resolution is exact; duplicate
/// names remain visible as an explicit ambiguity instead of being silently overwritten.
/// </summary>
public sealed class CapabilityCatalog : ICapabilityCatalog
{
    private readonly IReadOnlyList<CapabilityDescriptor> _descriptors;

    public CapabilityCatalog(IEnumerable<CapabilityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        _descriptors = descriptors.Select(Validate).ToArray();
    }

    public IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;

    public CapabilityResolutionResult Resolve(string capabilityId)
    {
        if (string.IsNullOrWhiteSpace(capabilityId))
            return CapabilityResolutionResult.Failed("capability-id-missing");

        var matches = _descriptors
            .Where(descriptor => string.Equals(descriptor.Name, capabilityId, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => CapabilityResolutionResult.Resolved(matches[0]),
            0 => CapabilityResolutionResult.Failed("capability-not-found"),
            _ => CapabilityResolutionResult.Failed("capability-ambiguous"),
        };
    }

    private static CapabilityDescriptor Validate(CapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Name))
            throw new ArgumentException("Capability name không được rỗng.", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.Description))
            throw new ArgumentException("Capability description không được rỗng.", nameof(descriptor));
        return descriptor;
    }
}

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
