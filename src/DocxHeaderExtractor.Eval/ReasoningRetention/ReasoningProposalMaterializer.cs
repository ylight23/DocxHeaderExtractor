using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public static class ReasoningProposalMaterializer
{
    public static (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated)
        Materialize(
            SourceDocument source,
            DocxPolicyState policyState,
            IEnumerable<ReasoningHeadingProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policyState);
        ArgumentNullException.ThrowIfNull(proposals);

        var policyById = policyState.Paragraphs.ToDictionary(p => p.StableId, StringComparer.Ordinal);
        var sourceTextById = source.Paragraphs.ToDictionary(p => p.SourceId, p => p.Text, StringComparer.Ordinal);
        var distinct = proposals
            .GroupBy(p => $"{p.SourceId}:{p.HeadingSpan.Start}:{p.HeadingSpan.End}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var elementIds = distinct.Select(ElementId).ToHashSet(StringComparer.Ordinal);
        var rows = new List<ReasoningValidatedProposal>();
        var elements = new List<ValidatedStructuralElement>();

        foreach (var proposal in distinct)
        {
            var hardIssues = ReasoningHardInvariantValidator.Validate(proposal, sourceTextById);
            if (hardIssues.Count > 0)
            {
                rows.Add(new(proposal, ElementId(proposal), false, string.Join(",", hardIssues)));
                continue;
            }
            if (!policyById.TryGetValue(proposal.SourceId, out var paragraph))
            {
                rows.Add(new(proposal, ElementId(proposal), false, "source-not-present"));
                continue;
            }

            // Compact provider responses may identify a source span without repeating its text.
            // The parser-owned source remains the only authority for the materialized text.
            var materializedProposal = string.IsNullOrEmpty(proposal.Text) &&
                proposal.HeadingSpan.IsValidFor(paragraph.Text)
                ? proposal with { Text = paragraph.Text[proposal.HeadingSpan.Start..proposal.HeadingSpan.End] }
                : proposal;

            var facts = SourceFactsBuilder.FromParagraph(paragraph);
            var candidate = new StructuralCandidate
            {
                CandidateId = $"reasoning:{proposal.SourceId}:{proposal.HeadingSpan.Start}:{proposal.HeadingSpan.End}",
                ObservedSourceFacts = [facts],
            };
            var proposalContract = new StructuralProposal
            {
                CandidateId = candidate.CandidateId,
                Type = MapType(materializedProposal.SemanticRole),
                Role = MapRole(materializedProposal.SemanticRole),
                ProposedSources = [new ProposedSourceReference(materializedProposal.SourceId, materializedProposal.HeadingSpan)],
                ProposedParentId = materializedProposal.ProposedParent,
                ProposedLevel = materializedProposal.ProposedLevel,
            };
            var validation = StructuralProposalValidator.Validate(candidate, proposalContract, elementIds);
            if (!validation.Accepted)
            {
                rows.Add(new(proposal, ElementId(proposal), false, validation.RejectionReason));
                continue;
            }

            var element = StructuralProposalValidator.Materialize(
                candidate,
                proposalContract,
                ElementId(materializedProposal),
                new StructuralDecision("reasoning-preserving-eval", "accepted", materializedProposal.Confidence, "model-proposal"),
                elementIds,
                new StructuralProjectionMetadata { OriginalText = materializedProposal.Text });
            if (element is null)
            {
                rows.Add(new(proposal, ElementId(proposal), false, "materialization-failed"));
                continue;
            }
            elements.Add(element);
            rows.Add(new(proposal, element.Id, true, null));
        }

        var surviving = elements
            .Where(element => element.ParentId is null || elementIds.Contains(element.ParentId))
            .ToArray();
        var relations = surviving
            .Where(element => element.ParentId is not null)
            .Select(element => new StructuralRelationProposal(
                element.ParentId!, element.Id, StructuralRelationType.ParentChild));
        return (ValidatedStructure.FromElements(surviving, relations), rows);
    }

    public static string ElementId(ReasoningHeadingProposal proposal) =>
        $"reasoning:{proposal.SourceId}:{proposal.HeadingSpan.Start}:{proposal.HeadingSpan.End}";

    private static StructuralElementType MapType(string role) =>
        role.Trim().ToUpperInvariant() switch
        {
            "DOCUMENT_TITLE" or "COVER_TITLE" => StructuralElementType.Title,
            "LOCAL_INDEX_TITLE" or "AGENDA_NAVIGATION_HEADING" or "TOC_ENTRY" => StructuralElementType.Subtitle,
            _ => StructuralElementType.Heading,
        };

    private static ProposedRole MapRole(string role) =>
        role.Trim().ToUpperInvariant() switch
        {
            "DOCUMENT_TITLE" => ProposedRole.DocumentTitle,
            "COVER_TITLE" => ProposedRole.CoverTitle,
            "LOCAL_INDEX_TITLE" or "AGENDA_NAVIGATION_HEADING" or "TOC_ENTRY" => ProposedRole.LocalSubheading,
            "PART" or "CHAPTER" or "SECTION" or "SUBSECTION" or "ARTICLE" or "CLAUSE_HEADING" or
                "ANNEX_HEADING" or "CONTENT_HEADING" or "OTHER_STRUCTURAL_LABEL" => ProposedRole.HeadingTopic,
            _ => ProposedRole.Unknown,
        };
}
