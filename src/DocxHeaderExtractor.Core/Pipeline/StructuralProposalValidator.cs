using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Generic source/span gate for structural proposals. It validates the proposal against parser
/// facts, but never lets the proposal replace those facts.
/// </summary>
public static class StructuralProposalValidator
{
    public static StructuralValidation Validate(
        SourceFacts? source,
        StructuralProposal proposal,
        StructuralSpan authoritativeSpan,
        IReadOnlySet<string>? knownSourceIds = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(authoritativeSpan);

        var sourceGrounded = source is not null &&
            string.Equals(source.SourceId, proposal.SourceId, StringComparison.Ordinal);
        var spanValid = source is not null && authoritativeSpan.IsValidFor(source.RawText);
        var typeValid = Enum.IsDefined(proposal.Type);
        var levelValid = proposal.Level is null or >= 1 and <= 9;
        var parentValid = proposal.ParentId is null || knownSourceIds is null ||
            knownSourceIds.Contains(proposal.ParentId);
        var reason = !sourceGrounded ? "source-not-grounded"
            : !spanValid ? "invalid-source-span"
            : !typeValid ? "unsupported-structural-type"
            : !levelValid ? "invalid-structural-level"
            : !parentValid ? "parent-not-grounded"
            : null;
        return new StructuralValidation(sourceGrounded, spanValid, typeValid, levelValid, parentValid, reason);
    }

    public static ValidatedStructuralElement? Materialize(
        SourceFacts source,
        StructuralProposal proposal,
        StructuralSpan authoritativeSpan,
        StructuralDecision decision,
        int sourceOrdinal,
        IReadOnlySet<string>? knownSourceIds = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(decision);
        var validation = Validate(source, proposal, authoritativeSpan, knownSourceIds);
        if (!validation.Accepted) return null;

        return new ValidatedStructuralElement
        {
            Id = proposal.SourceId,
            Type = proposal.Type,
            Role = proposal.Role,
            Source = new SourceReference(proposal.SourceId, sourceOrdinal, authoritativeSpan),
            Text = source.RawText[authoritativeSpan.Start..authoritativeSpan.End],
            Level = proposal.Level,
            ParentId = proposal.ParentId,
            Validation = validation,
            Decision = decision,
        };
    }
}
