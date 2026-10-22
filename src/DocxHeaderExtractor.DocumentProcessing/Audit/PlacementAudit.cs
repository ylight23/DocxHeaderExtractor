using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.DocumentProcessing.Audit;

/// <summary>
/// A producer observation of the placement attempt, not heading membership or hierarchy authority.
/// Contains identities and validated outcomes only; no prompt, source text, completion or exception message.
/// </summary>
public sealed record PlacementAudit(
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
    public IReadOnlyList<HeadingPlacementEdgeRepairObservation> EdgeRepairs { get; init; } = [];
    public HeadingPlacementUnresolvedSummary? UnresolvedSummary { get; init; }
    public IReadOnlyList<HeadingPlacementGraphConflict> GraphConflicts { get; init; } = [];
    public IReadOnlyList<string> GraphRepairAliases { get; init; } = [];
    public IReadOnlyList<string> GraphUnresolvedAliases { get; init; } = [];
    public IReadOnlyList<string> Failures { get; init; } = [];
    public IReadOnlyList<HeadingPlacementCallObservation> Calls { get; init; } = [];
    public IReadOnlyList<HeadingPlacementSourceObservation> SourceTrace { get; init; } = [];
    public IReadOnlyList<HeadingMissingAncestorBlocker> MissingAncestorBlockers { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ModelEvidence { get; init; } = [];
}

/// <summary>Structural contradictions in model claims; never a semantic choice of a replacement parent.</summary>
public sealed record HeadingPlacementGraphConflict(string Checkpoint, string Code, string ParentAlias,
    IReadOnlyList<string> Children);

/// <summary>Unique model-reported blockers and their upstream producer provenance. Counts overlap for multi-line ancestors.</summary>
public sealed record HeadingMissingAncestorBlocker(string Alias, string SourceId, IReadOnlyList<string> BlockedHeadingAliases,
    string? InitialSelection, string? FinalSelection, string? VerificationMembership, string BindingStatus,
    string ProvenanceAvailability, IReadOnlyList<DocxHeaderExtractor.Contracts.Routing.HeadingAliasProvenance> Provenance)
{
    public int BlockedHeadingCount => BlockedHeadingAliases.Count;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SourceText { get; init; }
}

/// <summary>Safe response-envelope diagnostics; no source, prompt or provider completion text.
/// JSON error offsets are zero-based within the parsed content after removing an optional single JSON fence.</summary>
public sealed record HeadingPlacementCallObservation(string UnitId, string Phase, int Round, int TargetCount,
    int RequestedMaxOutputTokens, int? MaxOutputTokens, string? FinishReason, int ResponseBytes, bool JsonFence,
    double Seconds, IReadOnlyList<string> ProtocolErrors, IReadOnlyList<string> AcceptedAliases)
{
    public long? JsonErrorLine { get; init; }
    public long? JsonErrorBytePosition { get; init; }
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

/// <summary>Legacy audit shape retained for compatibility; current production placement does not repair or reverify edges.</summary>
public sealed record HeadingPlacementEdgeRepairObservation(string Alias, string InitialVerificationStatus,
    bool ProposalReceived, string ReverificationStatus);

/// <summary>Unresolved = DirectUnverified + AncestorChainBlocked. The other fields partition DirectUnverified.
/// MissingAncestor counts terminal model-reported missing ancestry, not independently established source truth.
/// ProtocolNoVerdict means no valid terminal verdict, including a failed repair proposal.</summary>
public sealed record HeadingPlacementUnresolvedSummary(int DirectUnverified, int AncestorChainBlocked,
    int MissingAncestor, int ProtocolNoVerdict, int ModelUnresolved, int ModelRejected, int GraphConflictBlocked);
