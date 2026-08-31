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
        var sourceFactsPresent = candidate?.ObservedSourceFacts is { Count: > 0 };
        var validatedSources = candidateGrounded && sourceFactsPresent
            ? SelectValidatedSources(candidate!, proposal)
            : [];
        var proposedSpanValid = proposal.ProposedSources is null ||
            candidateGrounded && sourceFactsPresent && validatedSources.Count == proposal.ProposedSources.Count;
        var sourceSelectionValid = candidateGrounded && sourceFactsPresent && validatedSources.Count > 0 &&
            validatedSources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() ==
            validatedSources.Count;
        var typeValid = Enum.IsDefined(proposal.Type);
        var levelValid = proposal.ProposedLevel is null or >= 1 and <= 9;
        var parentValid = proposal.ProposedParentId is null || knownStructuralElementIds is null ||
            knownStructuralElementIds.Contains(proposal.ProposedParentId);
        var reason = !candidateGrounded ? "candidate-not-grounded"
            : !sourceFactsPresent ? "source-facts-missing"
            : !proposedSpanValid ? "invalid-proposed-sources"
            : !sourceSelectionValid ? "invalid-proposed-sources"
            : !typeValid ? "unsupported-structural-type"
            : !levelValid ? "invalid-structural-level"
            : !parentValid ? "structural-parent-not-grounded"
            : null;
        return new StructuralValidation(
            candidateGrounded, sourceFactsPresent, proposedSpanValid, sourceSelectionValid,
            validatedSources.Count, typeValid, levelValid, parentValid, reason);
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

        var sources = SelectValidatedSources(candidate, proposal);
        var text = string.Join(" ", sources.Select(source =>
        {
            var facts = candidate.ObservedSourceFacts.First(item => item.SourceId == source.SourceId);
            return facts.RawText[source.Span.Start..source.Span.End];
        }));
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

    private static IReadOnlyList<SourceReference> SelectValidatedSources(
        StructuralCandidate candidate,
        StructuralProposal proposal)
    {
        if (proposal.ProposedSources is null)
        {
            var observed = candidate.ObservedSources;
            return observed.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() ==
                observed.Count ? observed : [];
        }

        var observedById = candidate.ObservedSourceFacts
            .ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        if (proposal.ProposedSources.Count == 0 ||
            proposal.ProposedSources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() !=
            proposal.ProposedSources.Count)
            return [];

        var selected = new List<SourceReference>(proposal.ProposedSources.Count);
        foreach (var proposed in proposal.ProposedSources)
        {
            if (!observedById.TryGetValue(proposed.SourceId, out var facts) ||
                !proposed.Span.IsValidFor(facts.RawText) ||
                proposed.Span.Start < facts.RawSpan.Start ||
                proposed.Span.End > facts.RawSpan.End)
                return [];

            selected.Add(new SourceReference(
                facts.SourceId,
                facts.Source.ParagraphIndex ?? selected.Count,
                proposed.Span));
        }
        return selected;
    }
}
