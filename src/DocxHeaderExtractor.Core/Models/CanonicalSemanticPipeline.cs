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

/// <summary>
/// Deterministic orchestration boundary for vNext. Inference is supplied by the caller; this
/// class owns no provider and does not read Gold. It exists so the runtime order cannot silently
/// move task projection ahead of exact binding and global semantic resolution.
/// </summary>
public static class CanonicalSemanticPipeline
{
    public static CanonicalSemanticPipelineResult Run(
        DocumentSourceCatalog sourceCatalog,
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        string sourceSha256,
        string? expectedSourceSha256 = null) =>
        Run(sourceCatalog, proposals, sourceSha256, expectedSourceSha256, null);

    /// <summary>
    /// Executes the source-backed deterministic half of the semantic pipeline. Segment ownership
    /// is parser/harness authority: a visible alias outside <paramref name="ownedAliases"/> may be
    /// used as context by a model but cannot be accepted as this segment's output.
    /// </summary>
    public static CanonicalSemanticPipelineResult Run(
        DocumentSourceCatalog sourceCatalog,
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        string sourceSha256,
        string? expectedSourceSha256,
        IReadOnlySet<string>? ownedAliases)
    {
        ArgumentNullException.ThrowIfNull(sourceCatalog);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        var sourceHashVerified = expectedSourceSha256 is null ||
            string.Equals(sourceSha256, expectedSourceSha256, StringComparison.OrdinalIgnoreCase);
        if (!sourceHashVerified) throw new InvalidOperationException("source-hash-mismatch");

        var aliases = SemanticSourceAliasCatalog.FromCatalog(sourceCatalog);
        var aliasesByName = aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal);

        // Contract validation is deliberately before binding. It validates model-addressable
        // semantics/source ownership only; it never decides whether something is a heading.
        var contractIssues = new List<SemanticContractIssue>();
        var contractValid = new List<CanonicalSemanticProposal>(proposals.Count);
        foreach (var proposal in proposals)
        {
            var issues = CanonicalSemanticContractValidator.Validate(proposal, aliasesByName, ownedAliases);
            if (issues.Count == 0)
                contractValid.Add(proposal);
            else
                contractIssues.AddRange(issues);
        }

        var bound = CanonicalSemanticExactBinder.Bind(
            contractValid, aliases, ownedAliases, out var observations);
        var bindingValidation = CanonicalSemanticHardBindingValidator.Validate(
            bound, aliases, sourceSha256, expectedSourceSha256 ?? sourceSha256);
        if (!bindingValidation.IsValid)
            throw new InvalidOperationException(string.Join(",", bindingValidation.Errors));
        var graph = CanonicalSemanticGraphResolver.Resolve(bound);
        return new CanonicalSemanticPipelineResult(
            aliases, bound, observations, graph, sourceSha256, sourceHashVerified)
        {
            ContractIssues = contractIssues,
        };
    }
}
