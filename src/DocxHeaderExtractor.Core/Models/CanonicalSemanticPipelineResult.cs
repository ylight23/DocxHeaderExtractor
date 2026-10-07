namespace DocxHeaderExtractor.Core.Models;

/// <summary>One immutable execution result for the canonical semantic path.</summary>
public sealed record CanonicalSemanticPipelineResult(
    IReadOnlyList<SemanticSourceAlias> Aliases,
    IReadOnlyList<CanonicalSemanticBoundHeading> BoundHeadings,
    IReadOnlyList<CanonicalSemanticBindingObservation> BindingObservations,
    CanonicalSemanticGraph Graph,
    string SourceSha256,
    bool SourceHashVerified)
{
    public int BindingFailureCount => BindingObservations.Count(item =>
        item.Status is not CanonicalSemanticBindingStatus.Bound and
        not CanonicalSemanticBindingStatus.NonHeadingIgnored);

    /// <summary>
    /// Pre-binder semantic-contract failures. Invalid proposals are withheld from the exact
    /// binder instead of being repaired, guessed, or converted into coordinates.
    /// </summary>
    public IReadOnlyList<SemanticContractIssue> ContractIssues { get; init; } = [];
}
