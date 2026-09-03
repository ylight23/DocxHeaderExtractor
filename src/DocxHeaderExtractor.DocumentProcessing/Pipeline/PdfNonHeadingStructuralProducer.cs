using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Activates only semantic roles that already have parser-owned PDF source blocks. The semantic
/// analyst proposes a role; generic source/span validation remains the only materialization gate.
/// </summary>
internal sealed record PdfStructuralLaneResult(
    IReadOnlyList<ValidatedStructuralElement> Elements,
    IReadOnlyList<StructuralRelationProposal> RelationProposals);

internal static class PdfNonHeadingStructuralProducer
{
    public static IReadOnlyList<ValidatedStructuralElement> Materialize(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfBlockDecision> decisions,
        IReadOnlyDictionary<string, PdfCandidateContext>? contexts = null)
        => MaterializeLane(blocks, decisions, contexts).Elements;

    public static PdfStructuralLaneResult MaterializeLane(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfBlockDecision> decisions,
        IReadOnlyDictionary<string, PdfCandidateContext>? contexts = null,
        IReadOnlyList<PdfStructuralContainerObservation>? containerObservations = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(decisions);

        var blocksById = blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var ordinalByBlockId = blocks.Select((block, index) => (block.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var elements = new List<ValidatedStructuralElement>();
        var elementBySourceId = new Dictionary<string, ValidatedStructuralElement>(StringComparer.Ordinal);
        var seenSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (decision, ordinal) in decisions.Select((item, index) => (item, index)))
        {
            if (!TryMap(decision.SemanticRole, out var type, out var role) ||
                decision.Confidence < 0.65 ||
                !seenSourceIds.Add(decision.Id) ||
                !blocksById.TryGetValue(decision.Id, out var block) ||
                string.IsNullOrWhiteSpace(block.Text))
                continue;

            // A semantic list proposal is necessary but not sufficient. The parser must also
            // have observed a marker and a standalone/list-shaped source block. This keeps a
            // numbered heading from becoming a ListItem merely because it contains numbering.
            if (decision.SemanticRole == PdfSemanticRole.ListItemTopic &&
                (contexts is null || !contexts.TryGetValue(decision.Id, out var context) ||
                 !HasListEvidence(context.Source)))
                continue;

            var sourceFacts = ToSourceFacts(block, ordinal);
            var candidate = new StructuralCandidate
            {
                CandidateId = block.Id,
                ObservedSourceFacts = [sourceFacts],
            };
            var proposal = new StructuralProposal
            {
                CandidateId = block.Id,
                Type = type,
                Role = role,
                ProposedSources =
                [
                    new ProposedSourceReference(
                        sourceFacts.SourceId,
                        decision.ProposedSourceSpan is { } proposedSpan
                            ? new StructuralSpan(proposedSpan.Start, proposedSpan.End)
                            : new StructuralSpan(sourceFacts.RawSpan.Start, sourceFacts.RawSpan.End)),
                ],
            };
            var element = StructuralProposalValidator.Materialize(
                candidate,
                proposal,
                $"structural:pdf:semantic:{block.Id}",
                new StructuralDecision(
                    "pdf-semantic",
                    nameof(HeadingDecisionStatus.RequiresReview),
                    decision.Confidence,
                    "pdf-semantic-role"));
            if (element is not null)
            {
                elements.Add(element);
                elementBySourceId[decision.Id] = element;
            }
        }

        var observations = containerObservations ?? contexts?.Values
            .SelectMany(context => context.Source.LayoutContainers)
            .GroupBy(observation => observation.ContainerId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray() ?? [];
        var containerElements = new Dictionary<string, ValidatedStructuralElement>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (observation.Type is not (StructuralElementType.Figure or StructuralElementType.Table) ||
                !blocksById.TryGetValue(observation.SourceId, out var block) ||
                string.IsNullOrWhiteSpace(block.Text))
                continue;

            var sourceFacts = ToSourceFacts(block, ordinalByBlockId[block.Id]);
            var candidate = new StructuralCandidate
            {
                CandidateId = block.Id,
                ObservedSourceFacts = [sourceFacts],
            };
            var proposal = new StructuralProposal
            {
                CandidateId = block.Id,
                Type = observation.Type,
                Role = ProposedRole.StructuralContainer,
                ProposedSources =
                [new ProposedSourceReference(observation.SourceId, observation.Span)],
            };
            var element = StructuralProposalValidator.Materialize(
                candidate,
                proposal,
                $"structural:pdf:container:{observation.ContainerId}",
                new StructuralDecision(
                    "pdf-layout",
                    nameof(HeadingDecisionStatus.RequiresReview),
                    1,
                    observation.Evidence));
            if (element is not null)
            {
                elements.Add(element);
                containerElements[observation.ContainerId] = element;
            }
        }

        var relations = new List<StructuralRelationProposal>();
        foreach (var observation in observations)
        {
            if (!containerElements.TryGetValue(observation.ContainerId, out var container)) continue;
            foreach (var sourceId in observation.MemberSourceIds)
            {
                if (!elementBySourceId.TryGetValue(sourceId, out var element)) continue;
                if (observation.Type == StructuralElementType.Figure && element.Type == StructuralElementType.FigureTitle)
                    relations.Add(new StructuralRelationProposal(element.Id, container.Id, StructuralRelationType.Labels));
                else if (observation.Type == StructuralElementType.Figure && element.Type == StructuralElementType.Caption)
                    relations.Add(new StructuralRelationProposal(element.Id, container.Id, StructuralRelationType.CaptionOf));
                else if (observation.Type == StructuralElementType.Table && element.Type == StructuralElementType.TableTitle)
                    relations.Add(new StructuralRelationProposal(element.Id, container.Id, StructuralRelationType.Labels));
            }
        }

        var structuralElementTypes = elements.ToDictionary(element => element.Id, element => element.Type, StringComparer.Ordinal);
        var validatedRelations = StructuralRelationProposalValidator.Materialize(
            structuralElementTypes.Keys.ToHashSet(StringComparer.Ordinal), relations, structuralElementTypes);
        return new PdfStructuralLaneResult(
            elements,
            validatedRelations.Select(relation => new StructuralRelationProposal(
                relation.FromId, relation.ToId, relation.Type)).ToArray());
    }

    private static SourceFacts ToSourceFacts(PdfSemanticBlock block, int ordinal) => new()
    {
        SourceId = block.Id,
        RawText = block.Text,
        Source = new SourceAnchor
        {
            SourceType = "pdf",
            ParagraphIndex = ordinal,
            Page = block.Page,
            RenderBlockId = block.Id,
            RenderLineIds = block.Lines.Select(PdfCandidateProvenance.LineId).ToArray(),
        },
        RawSpan = new SourceTextSpan(0, block.Text.Length),
    };

    private static bool TryMap(PdfSemanticRole role, out StructuralElementType type, out ProposedRole proposedRole)
    {
        type = role switch
        {
            PdfSemanticRole.TableTitle => StructuralElementType.TableTitle,
            PdfSemanticRole.FigureCaption => StructuralElementType.Caption,
            PdfSemanticRole.FigureTitle => StructuralElementType.FigureTitle,
            PdfSemanticRole.ListItemTopic => StructuralElementType.ListItem,
            _ => default,
        };
        proposedRole = role switch
        {
            PdfSemanticRole.FigureTitle => ProposedRole.FigureTitle,
            PdfSemanticRole.ListItemTopic => ProposedRole.ListItemTopic,
            _ => ProposedRole.Caption,
        };
        return role is PdfSemanticRole.TableTitle or PdfSemanticRole.FigureCaption or
            PdfSemanticRole.FigureTitle or PdfSemanticRole.ListItemTopic;
    }

    private static bool HasListEvidence(PdfSourceFacts source) =>
        source.StructuralScope == "document_body" &&
        source.Marker is not null &&
        source.ObservedEvidence.Any(evidence => evidence.StartsWith("marker:", StringComparison.Ordinal)) &&
        source.ObservedEvidence.Any(evidence => evidence is "standalone_line" or "multi_line_cluster");
}
