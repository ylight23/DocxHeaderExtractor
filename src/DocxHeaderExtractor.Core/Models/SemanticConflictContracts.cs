namespace DocxHeaderExtractor.Core.Models;

/// <summary>A semantic disagreement for one parser-owned physical source occurrence.</summary>
public sealed record SemanticProposalConflict(
    string PhysicalSourceIdentity,
    IReadOnlyList<CanonicalSemanticProposal> Alternatives,
    string Classification = "OCCURRENCE_OR_BINDING_CONFLICT");

/// <summary>
/// A disagreement that does not prevent the harness from identifying and binding the physical
/// occurrence. Contested semantic attributes remain unresolved for a later adjudication stage.
/// </summary>
public sealed record SemanticAttributeConflict(
    string PhysicalSourceIdentity,
    IReadOnlyList<CanonicalSemanticProposal> Alternatives,
    CanonicalSemanticProposal BindingConsensus,
    IReadOnlyDictionary<string, IReadOnlyList<string?>> ContestedFields,
    string Classification = "SEMANTIC_ATTRIBUTE_CONFLICT");

/// <summary>Result of deterministic pre-binder semantic proposal normalization.</summary>
public sealed record SemanticConflictNormalizationResult(
    IReadOnlyList<CanonicalSemanticProposal> NormalizedProposals,
    IReadOnlyList<SemanticProposalConflict> Conflicts,
    int SemanticProposalInputCount,
    int SemanticProposalNormalizedCount,
    int ExactSemanticDuplicatesCollapsed,
    int SemanticConflictProposalCount)
{
    /// <summary>Attribute conflicts are retained for semantic adjudication.</summary>
    public IReadOnlyList<SemanticAttributeConflict> AttributeConflicts { get; init; } = [];

    /// <summary>Proposals safe to pass to the deterministic binder.</summary>
    public IReadOnlyList<CanonicalSemanticProposal> BindingReadyProposals { get; init; } = [];
}
