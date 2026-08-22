using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

public sealed record ProposalValidationResult(
    ModelProposal Proposal,
    SourceFacts? Source,
    HeadingValidation Validation,
    string? RejectionReason)
{
    public bool Accepted => RejectionReason is null;
}

/// <summary>
/// First deterministic gate after an LLM/VLM proposal. It deliberately does not resolve hierarchy;
/// that later pass receives only source-grounded, policy-eligible and span-safe proposals.
/// </summary>
public static class ModelProposalValidator
{
    public static ProposalValidationResult Validate(
        SourceFacts? source,
        ModelProposal proposal,
        HeadingPolicy? policy = null)
    {
        policy ??= new HeadingPolicy();
        var grounded = source is not null && string.Equals(source.SourceId, proposal.SourceId, StringComparison.Ordinal);
        var isOutlineRole = policy.Includes(proposal.Role);
        var spanRequired = isOutlineRole;
        var spanValid = !spanRequired || proposal.HeadingSpan is { } span && source is not null && span.IsValidFor(source.RawText);
        var evidenceValid = proposal.SemanticEvidence.Distinct().Count() == proposal.SemanticEvidence.Count &&
                            proposal.VisualEvidence.Distinct().Count() == proposal.VisualEvidence.Count;
        var markerValid = source?.Marker is null || source.Marker.Raw.Length > 0;
        var validation = new HeadingValidation(
            grounded,
            spanValid,
            evidenceValid,
            markerValid,
            MarkerSequenceValid: false,
            HierarchyValid: false,
            ParentValid: false);

        var reason = !grounded ? "source-not-grounded"
            : !spanValid ? "invalid-or-missing-heading-span"
            : !evidenceValid ? "duplicate-evidence-tag"
            : !markerValid ? "invalid-marker-facts"
            : null;
        return new ProposalValidationResult(proposal, source, validation, reason);
    }

    public static string HeadingText(SourceFacts source, SourceTextSpan span)
    {
        if (!span.IsValidFor(source.RawText))
            throw new ArgumentOutOfRangeException(nameof(span), "Heading span is outside immutable source text.");
        return source.RawText[span.Start..span.End];
    }
}
