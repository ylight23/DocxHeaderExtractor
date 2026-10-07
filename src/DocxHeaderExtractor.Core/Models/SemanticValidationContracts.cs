namespace DocxHeaderExtractor.Core.Models;

public sealed record SemanticContractIssue(string Code, string? SourceAlias, string Message);

public sealed record SemanticProposalValidationSummary(
    IReadOnlyList<CanonicalSemanticProposal> ValidProposals,
    IReadOnlyList<SemanticContractIssue> Issues);
