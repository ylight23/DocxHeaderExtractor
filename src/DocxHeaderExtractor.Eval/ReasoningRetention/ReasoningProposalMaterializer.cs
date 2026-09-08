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
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var canonical = grouped.Select(MergeGroup).ToArray();
        var distinct = canonical.Select(item => item.Proposal).ToArray();
        var elementIds = distinct.Select(ElementId).ToHashSet(StringComparer.Ordinal);
        var rows = new List<ReasoningValidatedProposal>();
        var elements = new List<ValidatedStructuralElement>();

        var normalized = NormalizeHierarchy(distinct, source, elementIds);

        foreach (var proposal in normalized)
        {
            var group = canonical.Single(item => ElementId(item.Proposal) == ElementId(proposal));
            var hardIssues = ReasoningHardInvariantValidator.Validate(proposal, sourceTextById);
            if (hardIssues.Count > 0)
            {
                rows.Add(new ReasoningValidatedProposal(proposal, ElementId(proposal), false, string.Join(",", hardIssues))
                {
                    ConflictStatus = group.ConflictStatus,
                    MergedDuplicateCount = group.Count,
                });
                continue;
            }
            if (!policyById.TryGetValue(proposal.SourceId, out var paragraph))
            {
                rows.Add(new ReasoningValidatedProposal(proposal, ElementId(proposal), false, "source-not-present")
                {
                    ConflictStatus = group.ConflictStatus,
                    MergedDuplicateCount = group.Count,
                });
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
                rows.Add(new ReasoningValidatedProposal(proposal, ElementId(proposal), false, validation.RejectionReason)
                {
                    ConflictStatus = group.ConflictStatus,
                    MergedDuplicateCount = group.Count,
                });
                continue;
            }

            var element = StructuralProposalValidator.Materialize(
                candidate,
                proposalContract,
                ElementId(materializedProposal),
                new StructuralDecision("reasoning-preserving-eval", "accepted", materializedProposal.Confidence, "model-proposal", group.ConflictStatus is not null),
                elementIds,
                new StructuralProjectionMetadata { OriginalText = materializedProposal.Text });
            if (element is null)
            {
                rows.Add(new ReasoningValidatedProposal(proposal, ElementId(proposal), false, "materialization-failed")
                {
                    ConflictStatus = group.ConflictStatus,
                    MergedDuplicateCount = group.Count,
                });
                continue;
            }
            elements.Add(element);
            rows.Add(new ReasoningValidatedProposal(proposal, element.Id, true, null)
            {
                ConflictStatus = group.ConflictStatus,
                MergedDuplicateCount = group.Count,
            });
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

    private sealed record CanonicalGroup(ReasoningHeadingProposal Proposal, string? ConflictStatus, int Count);

    private static CanonicalGroup MergeGroup(IGrouping<string, ReasoningHeadingProposal> group)
    {
        var items = group.ToArray();
        var roles = items.Select(item => Normalize(item.SemanticRole)).Distinct(StringComparer.Ordinal).Order().ToArray();
        var parents = items.Select(item => NormalizeParent(item.ProposedParent)).Distinct(StringComparer.Ordinal).Order().ToArray();
        var roleConflict = roles.Length > 1;
        var parentConflict = parents.Length > 1;
        var canonical = items
            .OrderBy(item => roleConflict ? Normalize(item.SemanticRole) : "")
            .ThenBy(item => parentConflict ? NormalizeParent(item.ProposedParent) : "", StringComparer.Ordinal)
            .ThenByDescending(item => item.Confidence)
            .ThenBy(item => item.ProposedLevel ?? int.MaxValue)
            .ThenBy(item => string.Join("|", item.DecisionEvidence.Select(e => $"{e.EvidenceType}:{e.SourceReference}:{e.ShortEvidenceCode}")), StringComparer.Ordinal)
            .Take(1)
            .Single();
        var evidence = items.SelectMany(item => item.DecisionEvidence)
            .Distinct()
            .OrderBy(item => item.EvidenceType, StringComparer.Ordinal)
            .ThenBy(item => item.SourceReference, StringComparer.Ordinal)
            .ThenBy(item => item.ShortEvidenceCode, StringComparer.Ordinal)
            .ToArray();
        var merged = canonical with
        {
            SemanticRole = roleConflict ? "OTHER_STRUCTURAL_LABEL" : canonical.SemanticRole,
            ProposedParent = parentConflict ? null : canonical.ProposedParent,
            Confidence = items.Max(item => item.Confidence),
            DecisionEvidence = evidence,
        };
        var conflicts = new List<string>();
        if (roleConflict) conflicts.Add("ROLE_CONFLICT");
        if (parentConflict) conflicts.Add("PARENT_CONFLICT");
        return new CanonicalGroup(merged, conflicts.Count == 0 ? null : string.Join(",", conflicts), items.Length);
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string NormalizeParent(string? value) => string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();

    private static IReadOnlyList<ReasoningHeadingProposal> NormalizeHierarchy(
        IReadOnlyList<ReasoningHeadingProposal> proposals,
        SourceDocument source,
        IReadOnlySet<string> elementIds)
    {
        var parentById = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var proposal in proposals)
        {
            var id = ElementId(proposal);
            var parent = ResolveParent(proposal.ProposedParent, elementIds);
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
            return levelById[id] = DerivedLevel(parent, path) + 1;
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

    private static string? ResolveParent(string? token, IReadOnlySet<string> elementIds)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (elementIds.Contains(token)) return token;
        return null;
    }

    private static StructuralElementType MapType(string role) =>
        role.Trim().ToUpperInvariant() switch
        {
            "DOCUMENT_TITLE" or "COVER_TITLE" => StructuralElementType.Title,
            "LOCAL_INDEX_TITLE" or "AGENDA_NAVIGATION_HEADING" or "TOC_ENTRY" or "FRONT_MATTER" => StructuralElementType.Subtitle,
            _ => StructuralElementType.Heading,
        };

    private static ProposedRole MapRole(string role) =>
        role.Trim().ToUpperInvariant() switch
        {
            "DOCUMENT_TITLE" => ProposedRole.DocumentTitle,
            "COVER_TITLE" => ProposedRole.CoverTitle,
            "LOCAL_INDEX_TITLE" or "AGENDA_NAVIGATION_HEADING" or "TOC_ENTRY" => ProposedRole.LocalSubheading,
            "FRONT_MATTER" => ProposedRole.Metadata,
            "PART" or "CHAPTER" or "SECTION" or "SUBSECTION" or "ARTICLE" or "CLAUSE_HEADING" or
                "ANNEX_HEADING" or "CONTENT_HEADING" or "OTHER_STRUCTURAL_LABEL" => ProposedRole.HeadingTopic,
            _ => ProposedRole.Unknown,
        };
}
