using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Activates only semantic roles that already have parser-owned PDF source blocks. The semantic
/// analyst proposes a role; generic source/span validation remains the only materialization gate.
/// </summary>
internal static class PdfNonHeadingStructuralProducer
{
    public static IReadOnlyList<ValidatedStructuralElement> Materialize(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfBlockDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(decisions);

        var blocksById = blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var elements = new List<ValidatedStructuralElement>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (decision, ordinal) in decisions.Select((item, index) => (item, index)))
        {
            if (!TryMap(decision.SemanticRole, out var type) ||
                decision.Confidence < 0.65 ||
                !seenIds.Add(decision.Id) ||
                !blocksById.TryGetValue(decision.Id, out var block) ||
                string.IsNullOrWhiteSpace(block.Text))
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
                Role = ProposedRole.Caption,
                ProposedSources =
                [
                    new ProposedSourceReference(
                        sourceFacts.SourceId,
                        new StructuralSpan(sourceFacts.RawSpan.Start, sourceFacts.RawSpan.End)),
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
                elements.Add(element);
        }

        return elements;
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

    private static bool TryMap(PdfSemanticRole role, out StructuralElementType type)
    {
        type = role switch
        {
            PdfSemanticRole.TableTitle => StructuralElementType.TableTitle,
            PdfSemanticRole.FigureCaption => StructuralElementType.Caption,
            _ => default,
        };
        return role is PdfSemanticRole.TableTitle or PdfSemanticRole.FigureCaption;
    }
}
