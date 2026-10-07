namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Why one heading entry in a reply could not be turned into a proposal.
/// <para>
/// This exists because the alternative was a silent drop. A structured reply that validated
/// cleanly, named real atoms and quoted real text produced zero proposals and zero visible
/// failures, because the decoder it reached was the one belonging to a different coordinate
/// contract and simply returned null for every entry. A run whose measurement collapsed looked
/// exactly like a run whose model found nothing.
/// </para>
/// </summary>
public sealed record SemanticProposalDecodeFailure(string Code, string Detail);

/// <summary>
/// One reply's headings array, decoded under the contract that issued its schema.
/// </summary>
public sealed record SemanticProposalDecodeResult(
    IReadOnlyList<CanonicalSemanticProposal> Proposals,
    IReadOnlyList<SemanticProposalDecodeFailure> Failures);
