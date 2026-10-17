namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>
/// A producer observation of the placement attempt, not heading membership or hierarchy authority.
/// Contains identities and validated outcomes only; no prompt, source text, completion or exception message.
/// </summary>
public sealed record HeadingPlacementExecutionObservation(
    string Status,
    int RequestedCount,
    int ResponseEntryCount,
    int PlacedCount,
    int OutOfHierarchyCount,
    int UnresolvedCount,
    string? FailureClass,
    IReadOnlyList<HeadingPlacementDecisionObservation> Decisions)
{
    public string? Contract { get; init; }
    public int LogicalCalls { get; init; }
    public double Seconds { get; init; }
    public int VerifiedCount { get; init; }
    public int RepairCount { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = [];
    public IReadOnlyList<HeadingPlacementSourceObservation> SourceTrace { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ModelEvidence { get; init; } = [];
}

public sealed record HeadingPlacementDecisionObservation(
    string SourceId,
    string Alias,
    string Resolution)
{
    public string? ProposedParentAlias { get; init; }
    public string? VerificationStatus { get; init; }
    public string? ResolutionBasis { get; init; }
    public IReadOnlyList<string> BlockingAliases { get; init; } = [];
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];
}

public sealed record HeadingPlacementSourceObservation(string Alias, string SourceId, string? InitialSelection,
    string? FinalSelection, string? VerificationMembership, string BindingStatus, string PlacementResolution);
