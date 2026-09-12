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
        string? expectedSourceSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCatalog);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        var sourceHashVerified = expectedSourceSha256 is null ||
            string.Equals(sourceSha256, expectedSourceSha256, StringComparison.OrdinalIgnoreCase);
        if (!sourceHashVerified) throw new InvalidOperationException("source-hash-mismatch");

        var aliases = SemanticSourceAliasCatalog.FromCatalog(sourceCatalog);
        var bound = CanonicalSemanticExactBinder.Bind(proposals, aliases, out var observations);
        var bindingValidation = CanonicalSemanticHardBindingValidator.Validate(
            bound, aliases, sourceSha256, expectedSourceSha256 ?? sourceSha256);
        if (!bindingValidation.IsValid)
            throw new InvalidOperationException(string.Join(",", bindingValidation.Errors));
        var graph = CanonicalSemanticGraphResolver.Resolve(bound);
        return new CanonicalSemanticPipelineResult(
            aliases, bound, observations, graph, sourceSha256, sourceHashVerified);
    }
}
