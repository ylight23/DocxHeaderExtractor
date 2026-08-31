using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

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
        IReadOnlyList<PdfOutputDecision> decisions)
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
            var sourceId = heading.SourceAnchor.StableId ?? heading.Id;
            var sourceText = heading.SourceText;
            var sourceFacts = new SourceFacts
            {
                SourceId = sourceId,
                RawText = sourceText,
                Source = new SourceAnchor
                {
                    SourceType = "docx",
                    ParagraphId = sourceId,
                    ParagraphIndex = heading.SourceAnchor.ParagraphIndex,
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
                        new StructuralSpan(heading.SourceAnchor.Span.Start, heading.SourceAnchor.Span.End)),
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

        var structure = ValidatedStructure.FromElements(elements);
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
