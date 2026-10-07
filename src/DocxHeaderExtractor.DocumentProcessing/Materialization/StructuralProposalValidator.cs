using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Materialization;

/// <summary>
/// Source/span gate for heading materialization proposals. It validates proposed coordinates against
/// parser facts, but never lets a proposal replace observed source identity or source spans.
/// </summary>
public static class StructuralProposalValidator
{
    public static StructuralValidation Validate(
        StructuralSourceOccurrence? sourceOccurrence,
        StructuralProposal proposal,
        IReadOnlySet<string>? knownStructuralElementIds = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var sourceGrounded = sourceOccurrence is not null &&
            string.Equals(sourceOccurrence.SourceOccurrenceId, proposal.SourceOccurrenceId, StringComparison.Ordinal);
        var sourceFactsPresent = sourceOccurrence?.ObservedSourceFacts is { Count: > 0 };
        var validatedSources = sourceGrounded && sourceFactsPresent
            ? SelectValidatedSources(sourceOccurrence!, proposal)
            : [];
        var proposedSpanValid = proposal.ProposedSources is null ||
            sourceGrounded && sourceFactsPresent && validatedSources.Count == proposal.ProposedSources.Count;
        var sourceSelectionValid = sourceGrounded && sourceFactsPresent && validatedSources.Count > 0 &&
            validatedSources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() ==
            validatedSources.Count;
        var typeValid = Enum.IsDefined(proposal.Type);
        var typeRoleValid = typeValid && IsRoleCompatible(proposal.Type, proposal.Role);
        var levelValid = proposal.ProposedLevel is null or >= 1 and <= 9;
        var parentValid = proposal.ProposedParentId is null || knownStructuralElementIds is null ||
            knownStructuralElementIds.Contains(proposal.ProposedParentId);
        var reason = !sourceGrounded ? "source-occurrence-not-grounded"
            : !sourceFactsPresent ? "source-facts-missing"
            : !proposedSpanValid ? "invalid-proposed-sources"
            : !sourceSelectionValid ? "invalid-proposed-sources"
            : !typeValid ? "unsupported-structural-type"
            : !typeRoleValid ? "incompatible-structural-role"
            : !levelValid ? "invalid-structural-level"
            : !parentValid ? "structural-parent-not-grounded"
            : null;
        return new StructuralValidation(
            sourceGrounded, sourceFactsPresent, proposedSpanValid, sourceSelectionValid,
            validatedSources.Count, typeValid, levelValid, parentValid, reason, typeRoleValid);
    }

    public static ValidatedStructuralElement? Materialize(
        StructuralSourceOccurrence sourceOccurrence,
        StructuralProposal proposal,
        string structuralElementId,
        StructuralDecision decision,
        IReadOnlySet<string>? knownStructuralElementIds = null,
        StructuralProjectionMetadata? projectionMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(sourceOccurrence);
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralElementId);
        ArgumentNullException.ThrowIfNull(decision);
        var validation = Validate(sourceOccurrence, proposal, knownStructuralElementIds);
        if (!validation.Accepted) return null;

        var sources = SelectValidatedSources(sourceOccurrence, proposal);
        var text = string.Join(" ", sources.Select(source =>
        {
            var facts = sourceOccurrence.ObservedSourceFacts.First(item => item.SourceId == source.SourceId);
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
        StructuralSourceOccurrence sourceOccurrence,
        StructuralProposal proposal)
    {
        if (proposal.ProposedSources is null)
        {
            var observed = sourceOccurrence.ObservedSources;
            return observed.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() ==
                observed.Count ? observed : [];
        }

        var observedById = sourceOccurrence.ObservedSourceFacts
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

    private static bool IsRoleCompatible(StructuralElementType type, ProposedRole role) =>
        type == StructuralElementType.Heading && role == ProposedRole.HeadingTopic;
}
