using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

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
        ArgumentNullException.ThrowIfNull(proposal);
        policy ??= new HeadingPolicy();
        var isOutlineRole = policy.Includes(proposal.Role);
        // Heading proposals now pass through the generic source/span gate. Evidence and marker
        // checks remain here because they are still specific to the heading proposal contract.
        var structuralValidation = isOutlineRole && proposal.HeadingSpan is { } headingSpan
            ? StructuralProposalValidator.Validate(
                source is null ? null : new StructuralCandidate
                {
                    CandidateId = proposal.SourceId,
                    ObservedSourceFacts = [source],
                },
                new StructuralProposal
                {
                    CandidateId = proposal.SourceId,
                    Type = proposal.Role == ProposedRole.DocumentTitle
                        ? StructuralElementType.Title
                        : StructuralElementType.Heading,
                    Role = proposal.Role,
                    ProposedSources = [new ProposedSourceReference(
                        proposal.SourceId, new StructuralSpan(headingSpan.Start, headingSpan.End))],
                    ProposedLevel = proposal.ProposedLevel,
                })
            : null;
        var grounded = structuralValidation?.CandidateGrounded ??
            (source is not null && string.Equals(source.SourceId, proposal.SourceId, StringComparison.Ordinal));
        var spanValid = structuralValidation?.ProposedSpanValid ??
            (!isOutlineRole || proposal.HeadingSpan is { } span && source is not null && span.IsValidFor(source.RawText));
        var parserBoundaryValid = !isOutlineRole ||
            proposal.HeadingSpan is { } candidateSpan && source is not null &&
            IsParserBoundaryAligned(source, candidateSpan);
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
            ParentValid: false,
            ParserBoundaryValid: parserBoundaryValid);

        var reason = !grounded ? "source-not-grounded"
            : !spanValid ? "invalid-or-missing-heading-span"
            : !parserBoundaryValid ? "span-not-parser-boundary"
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

    private static bool IsParserBoundaryAligned(SourceFacts source, SourceTextSpan span)
    {
        if (!span.IsValidFor(source.RawText)) return false;
        var boundaries = source.ParserBoundaries.Count == 0
            ? SourceTextBoundaryMap.For(source.RawText)
            : source.ParserBoundaries;
        return SourceTextBoundaryMap.Contains(boundaries, span.Start) &&
               SourceTextBoundaryMap.Contains(boundaries, span.End);
    }
}
