using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Generic source/span gate for structural proposals. It validates proposed coordinates against
/// parser facts, but never lets a proposal replace observed source identity or source spans.
/// </summary>
public static class StructuralProposalValidator
{
    public static StructuralValidation Validate(
        StructuralCandidate? candidate,
        StructuralProposal proposal,
        IReadOnlySet<string>? knownStructuralElementIds = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var candidateGrounded = candidate is not null &&
            string.Equals(candidate.CandidateId, proposal.CandidateId, StringComparison.Ordinal);
        var sourceFactsPresent = candidate?.SourceFacts is { Count: > 0 };
        var proposedSpanValid = proposal.ProposedSpan is null ||
            candidate?.SourceFacts.Any(source => proposal.ProposedSpan.IsValidFor(source.RawText)) == true;
        var typeValid = Enum.IsDefined(proposal.Type);
        var levelValid = proposal.ProposedLevel is null or >= 1 and <= 9;
        var parentValid = proposal.ProposedParentId is null || knownStructuralElementIds is null ||
            knownStructuralElementIds.Contains(proposal.ProposedParentId);
        var reason = !candidateGrounded ? "candidate-not-grounded"
            : !sourceFactsPresent ? "source-facts-missing"
            : !proposedSpanValid ? "invalid-proposed-span"
            : !typeValid ? "unsupported-structural-type"
            : !levelValid ? "invalid-structural-level"
            : !parentValid ? "structural-parent-not-grounded"
            : null;
        return new StructuralValidation(
            candidateGrounded, sourceFactsPresent, proposedSpanValid, typeValid, levelValid,
            parentValid, reason);
    }

    public static ValidatedStructuralElement? Materialize(
        StructuralCandidate candidate,
        StructuralProposal proposal,
        string structuralElementId,
        StructuralDecision decision,
        IReadOnlySet<string>? knownStructuralElementIds = null,
        StructuralProjectionMetadata? projectionMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralElementId);
        ArgumentNullException.ThrowIfNull(decision);
        var validation = Validate(candidate, proposal, knownStructuralElementIds);
        if (!validation.Accepted) return null;

        var sources = candidate.Sources;
        var text = string.Join(" ", candidate.SourceFacts.Select(source =>
            source.RawText[source.RawSpan.Start..source.RawSpan.End]));
        return new ValidatedStructuralElement
        {
            Id = structuralElementId,
            Type = proposal.Type,
            Role = proposal.Role,
            Sources = sources,
            Text = text,
            Level = proposal.ProposedLevel,
            ParentId = proposal.ProposedParentId,
            Validation = validation,
            Decision = decision,
            ProjectionMetadata = projectionMetadata,
        };
    }
}
