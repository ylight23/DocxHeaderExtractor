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
        var grouped = proposals
            .GroupBy(p => $"{p.SourceId}:{p.HeadingSpan.Start}:{p.HeadingSpan.End}", StringComparer.Ordinal)
            .ToArray();
        var conflictingKeys = grouped
            .Where(group => group.Skip(1).Any(item => !EquivalentPayload(group.First(), item)))
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var distinct = grouped
            .Where(group => !conflictingKeys.Contains(group.Key))
            .Select(group => group.First())
            .ToArray();
        var elementIds = distinct.Select(ElementId).ToHashSet(StringComparer.Ordinal);
        var rows = new List<ReasoningValidatedProposal>();
        var elements = new List<ValidatedStructuralElement>();

        foreach (var conflict in grouped.Where(group => conflictingKeys.Contains(group.Key)))
        {
            foreach (var proposal in conflict)
                rows.Add(new(proposal, ElementId(proposal), false, "conflicting-proposal-payload"));
        }

        var normalized = NormalizeHierarchy(distinct, source, elementIds);

        foreach (var proposal in normalized)
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
                ProposedLevel = materializedProposal.ProposedLevel is >= 1 and <= 9
                    ? materializedProposal.ProposedLevel
                    : null,
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

    private static bool EquivalentPayload(ReasoningHeadingProposal left, ReasoningHeadingProposal right) =>
        string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal) &&
        left.HeadingSpan == right.HeadingSpan &&
        string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
        string.Equals(left.SemanticRole, right.SemanticRole, StringComparison.Ordinal) &&
        left.ProposedLevel == right.ProposedLevel &&
        string.Equals(left.ProposedParent, right.ProposedParent, StringComparison.Ordinal) &&
        left.Confidence.Equals(right.Confidence) &&
        left.DecisionEvidence.SequenceEqual(right.DecisionEvidence);

    private static IReadOnlyList<ReasoningHeadingProposal> NormalizeHierarchy(
        IReadOnlyList<ReasoningHeadingProposal> proposals,
        SourceDocument source,
        IReadOnlySet<string> elementIds)
    {
        var sourceById = source.Paragraphs.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var firstByOrdinal = proposals
            .Where(item => sourceById.ContainsKey(item.SourceId))
            .GroupBy(item => sourceById[item.SourceId].SourceOrdinal)
            .ToDictionary(group => group.Key, group => ElementId(group.First()));
        var parentById = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var proposal in proposals)
        {
            var id = ElementId(proposal);
            var parent = ResolveParent(proposal.ProposedParent, sourceById, firstByOrdinal, elementIds);
            // An invalid edge must not erase the node. Drop only the bad edge and keep the
            // accepted heading available for projection.
            parentById[id] = parent is not null && !string.Equals(parent, id, StringComparison.Ordinal)
                ? parent
                : null;
        }

        // Break only cyclic edges. Nodes remain in the union and receive a valid root level.
        foreach (var id in parentById.Keys.ToArray())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = id;
            while (parentById.TryGetValue(current, out var parent) && parent is not null)
            {
                if (!seen.Add(current) || string.Equals(parent, id, StringComparison.Ordinal))
                {
                    parentById[id] = null;
                    break;
                }
                current = parent;
            }
        }

        var levelById = new Dictionary<string, int>(StringComparer.Ordinal);
        int DerivedLevel(string id, HashSet<string> path)
        {
            if (levelById.TryGetValue(id, out var known)) return known;
            if (!path.Add(id) || !parentById.TryGetValue(id, out var parent) || parent is null)
                return levelById[id] = 1;
            return levelById[id] = Math.Min(9, DerivedLevel(parent, path) + 1);
        }

        return proposals.Select(proposal =>
        {
            var id = ElementId(proposal);
            var level = DerivedLevel(id, new HashSet<string>(StringComparer.Ordinal));
            return proposal with
            {
                ProposedParent = parentById[id],
                ProposedLevel = level,
            };
        }).ToArray();
    }

    private static string? ResolveParent(
        string? token,
        IReadOnlyDictionary<string, SourceParagraph> sourceById,
        IReadOnlyDictionary<int, string> firstByOrdinal,
        IReadOnlySet<string> elementIds)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (elementIds.Contains(token)) return token;
        if (token.StartsWith("sourceOrdinal:", StringComparison.Ordinal) &&
            int.TryParse(token[14..], out var ordinal) && firstByOrdinal.TryGetValue(ordinal, out var id))
            return id;
        if (sourceById.TryGetValue(token, out var paragraph) && firstByOrdinal.TryGetValue(paragraph.SourceOrdinal, out var sourceId))
            return sourceId;
        return null;
    }

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
