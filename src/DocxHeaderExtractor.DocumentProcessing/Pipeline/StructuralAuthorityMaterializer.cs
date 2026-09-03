using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Materializes the already validated PDF final structure into the generic structural authority.
/// It does not select candidates, resolve hierarchy, or derive product fields. Output emission is
/// still controlled by <see cref="PdfOutputDecisionPolicy"/> and is represented separately so the
/// full validated structure remains available to audit consumers.
/// </summary>
public static class StructuralAuthorityMaterializer
{
    private const string BoundarySource = "pdf-final-structure-v1";
    private const string ConfidenceBasis = "pdf-final-structure-validated";

    public static StructuralMaterializationResult Materialize(
        PdfFinalStructure finalStructure,
        IReadOnlyList<PdfOutputDecision> decisions,
        DocumentSourceCatalog? sourceCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(finalStructure);
        ArgumentNullException.ThrowIfNull(decisions);

        var decisionById = decisions
            .GroupBy(decision => decision.HeadingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sourceUnjoined = 0;
        var elements = new List<ValidatedStructuralElement>(finalStructure.Headings.Count);
        var elementIdByHeadingId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var heading in finalStructure.Headings)
        {
            if (heading.SourceAnchor is null)
            {
                sourceUnjoined++;
                continue;
            }

            var elementId = ElementId(heading.Id);
            elementIdByHeadingId[heading.Id] = elementId;
            var decision = decisionById.GetValueOrDefault(heading.Id);
            var decisionStatus = decision?.RequiresReview == false
                ? nameof(HeadingDecisionStatus.AutoAcceptedEvidence)
                : nameof(HeadingDecisionStatus.RequiresReview);
            var reasons = decision?.Reasons ?? [];
            // The catalog-bearing overload is the production PDF path. The catalog-free form is
            // retained for the compatibility/shadow oracle: it must continue to project the old
            // DOCX-grounded identity until those callers migrate.
            var usesPdfParserSource = sourceCatalog is not null && heading.PdfEvidence is not null;
            var sourceId = usesPdfParserSource
                ? heading.PdfEvidence!.BlockId
                : heading.SourceAnchor.StableId ?? heading.Id;
            var sourceUnit = usesPdfParserSource
                ? sourceCatalog?.Units.FirstOrDefault(unit => unit.SourceId == sourceId)
                : null;
            var sourceText = sourceUnit?.Text ?? heading.SourceText;
            var sourceSpan = usesPdfParserSource && heading.PdfEvidence is { } pdfEvidence
                ? new StructuralSpan(pdfEvidence.Span.Start, pdfEvidence.Span.End)
                : new StructuralSpan(heading.SourceAnchor.Span.Start, heading.SourceAnchor.Span.End);
            var sourceFacts = new SourceFacts
            {
                SourceId = sourceId,
                RawText = sourceText,
                Source = new SourceAnchor
                {
                    SourceType = usesPdfParserSource ? "pdf" : "docx",
                    ParagraphId = sourceId,
                    ParagraphIndex = sourceUnit?.SourceOrdinal ??
                        (usesPdfParserSource ? PdfBlockOrdinal(sourceId) : heading.SourceAnchor.ParagraphIndex),
                    Page = usesPdfParserSource ? heading.PdfEvidence?.Page : null,
                    RenderBlockId = usesPdfParserSource ? heading.PdfEvidence?.BlockId : null,
                    RenderLineIds = usesPdfParserSource ? heading.PdfEvidence?.LineIds ?? [] : [],
                },
                RawSpan = new SourceTextSpan(0, sourceText.Length),
            };
            var candidate = new StructuralCandidate
            {
                CandidateId = heading.Id,
                ObservedSourceFacts = [sourceFacts],
            };
            var proposal = new StructuralProposal
            {
                CandidateId = heading.Id,
                Type = ElementType(heading.Role),
                Role = ElementRole(heading.Role),
                ProposedSources =
                [
                    new ProposedSourceReference(sourceId,
                        sourceSpan),
                ],
                ProposedParentId = heading.ParentId is { } parent && elementIdByHeadingId.ContainsKey(parent)
                    ? elementIdByHeadingId[parent]
                    : null,
                ProposedLevel = heading.Level,
            };
            var element = StructuralProposalValidator.Materialize(
                candidate, proposal, elementId,
                new StructuralDecision("model", decisionStatus, 1.0, ConfidenceBasis),
                elementIdByHeadingId.Values.ToHashSet(StringComparer.Ordinal),
                new StructuralProjectionMetadata
                {
                    CompatibilitySourceId = heading.Id,
                    CompatibilitySourceOrdinal = heading.SourceAnchor.ParagraphIndex,
                    CompatibilityStableId = heading.SourceAnchor.StableId,
                    CompatibilityHeadingSpan = new StructuralSpan(
                        heading.SourceAnchor.Span.Start,
                        heading.SourceAnchor.Span.End),
                    CompatibilityText = heading.Text,
                    CompatibilityLevel = heading.Level,
                    CompatibilityLevelIsSet = true,
                    OriginalText = heading.SourceText,
                    BoundarySource = BoundarySource,
                    AcceptanceSignature = reasons.Count > 0 ? string.Join(",", reasons) : null,
                });
            if (element is null)
                throw new InvalidOperationException($"Validated PDF heading '{heading.Id}' failed generic materialization.");

            elements.Add(element with
            {
                Sources = element.Sources.Select(item => item with { StableId = heading.SourceAnchor.StableId }).ToArray(),
            });
        }

        var relationProposals = elements
            .Where(element => element.ParentId is not null)
            .Select(element => new StructuralRelationProposal(
                element.ParentId!, element.Id, StructuralRelationType.ParentChild));
        var structure = ValidatedStructure.FromElements(elements, relationProposals);
        var emittedElementIds = finalStructure.Headings
            .Where(heading => decisionById.TryGetValue(heading.Id, out var decision) &&
                decision.Emit && elementIdByHeadingId.ContainsKey(heading.Id))
            .Select(heading => elementIdByHeadingId[heading.Id])
            .ToHashSet(StringComparer.Ordinal);

        return new StructuralMaterializationResult(
            structure,
            emittedElementIds,
            sourceUnjoined,
            finalStructure.Headings.Count(heading => heading.ParentId is not null &&
                (!elementIdByHeadingId.ContainsKey(heading.Id) ||
                 !elementIdByHeadingId.ContainsKey(heading.ParentId!))));
    }

    private static string ElementId(string headingId) => $"structural:pdf:{headingId}";

    private static int PdfBlockOrdinal(string sourceId) =>
        sourceId.StartsWith("b", StringComparison.Ordinal) &&
        int.TryParse(sourceId.AsSpan(1), out var ordinal)
            ? Math.Max(0, ordinal - 1)
            : 0;

    private static StructuralElementType ElementType(string role) => role switch
    {
        "Title" or "DocumentTitle" => StructuralElementType.Title,
        "Subtitle" or "DocumentSubtitle" => StructuralElementType.Subtitle,
        _ => StructuralElementType.Heading,
    };

    private static ProposedRole ElementRole(string role) => role switch
    {
        "Title" or "DocumentTitle" => ProposedRole.DocumentTitle,
        "Subtitle" or "DocumentSubtitle" => ProposedRole.CoverTitle,
        _ => ProposedRole.HeadingTopic,
    };
}

public sealed record StructuralMaterializationResult(
    ValidatedStructure Structure,
    IReadOnlySet<string> EmittedElementIds,
    int UnjoinedSourceCount,
    int UnjoinedParentCount);
