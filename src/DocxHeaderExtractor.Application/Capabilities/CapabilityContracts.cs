namespace DocxHeaderExtractor.Application.Capabilities;

/// <summary>
/// Risk declared by code for a capability. The declaration is metadata for policy evaluation;
/// it is never supplied by a model at runtime.
/// </summary>
public enum CapabilityRisk
{
    Low,
    Medium,
    High,
}

/// <summary>Provider-independent description of a callable capability and its side effects.</summary>
public sealed record CapabilityDescriptor(
    string Name,
    string Description,
    CapabilityRisk Risk,
    bool SendsDataExternally,
    bool MutatesExternalState)
{
    public bool SupportsRepair { get; init; }

    public IReadOnlyList<string> SideEffectPaths { get; init; } = [];
}
