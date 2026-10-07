namespace DocxHeaderExtractor.Core.Models;

/// <summary>Everything a coordinate contract's binder needs to resolve one segment's proposals.</summary>
public sealed record SemanticCoordinateBindingRequest(
    IReadOnlyList<CanonicalSemanticProposal> Proposals,
    IReadOnlyList<SemanticSourceAlias> Aliases,
    IReadOnlySet<string>? OwnedAliases = null,
    IReadOnlyList<SemanticSourceAtom>? Atoms = null);

/// <summary>What a binder resolved, and what it refused and why.</summary>
public sealed record SemanticCoordinateBindingOutcome(
    IReadOnlyList<CanonicalSemanticBoundHeading> Bound,
    IReadOnlyList<CanonicalSemanticBindingObservation> Observations);
