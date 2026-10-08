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
    IReadOnlyList<HeadingPlacementDecisionObservation> Decisions);

public sealed record HeadingPlacementDecisionObservation(
    string SourceId,
    string Alias,
    string Resolution);
