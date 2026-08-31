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
            var source = new SourceReference(
                heading.Id,
                heading.SourceAnchor.ParagraphIndex,
                new StructuralSpan(heading.SourceAnchor.Span.Start, heading.SourceAnchor.Span.End))
            {
                StableId = heading.SourceAnchor.StableId,
            };
            var decision = decisionById.GetValueOrDefault(heading.Id);
            var decisionStatus = decision?.RequiresReview == false
                ? nameof(HeadingDecisionStatus.AutoAcceptedEvidence)
                : nameof(HeadingDecisionStatus.RequiresReview);
            var reasons = decision?.Reasons ?? [];

            elements.Add(new ValidatedStructuralElement
            {
                Id = elementId,
                Type = ElementType(heading.Role),
                Role = ElementRole(heading.Role),
                Sources = [source],
                Text = heading.Text,
                Level = heading.Level,
                ParentId = null,
                Validation = new StructuralValidation(
                    CandidateGrounded: true,
                    SourceFactsPresent: true,
                    ProposedSpanValid: true,
                    SourceSelectionValid: true,
                    ValidatedSourceCount: 1,
                    TypeValid: true,
                    LevelValid: true,
                    ParentValid: true,
                    RejectionReason: null),
                Decision = new StructuralDecision(
                    Origin: "model",
                    Status: decisionStatus,
                    Confidence: 1.0,
                    ConfidenceBasis: ConfidenceBasis),
                ProjectionMetadata = new StructuralProjectionMetadata
                {
                    OriginalText = heading.SourceText,
                    BoundarySource = BoundarySource,
                    AcceptanceSignature = reasons.Count > 0 ? string.Join(",", reasons) : null,
                },
            });
        }

        var parentUnjoined = 0;
        var withParents = elements.Select(element =>
        {
            var heading = finalStructure.Headings.First(item => ElementId(item.Id) == element.Id);
            if (heading.ParentId is null) return element;
            if (!elementIdByHeadingId.TryGetValue(heading.ParentId, out var parentId))
            {
                parentUnjoined++;
                return element;
            }

            return element with { ParentId = parentId };
        }).ToArray();

        var structure = ValidatedStructure.FromElements(withParents);
        var emittedElementIds = finalStructure.Headings
            .Where(heading => decisionById.TryGetValue(heading.Id, out var decision) &&
                decision.Emit && elementIdByHeadingId.ContainsKey(heading.Id))
            .Select(heading => elementIdByHeadingId[heading.Id])
            .ToHashSet(StringComparer.Ordinal);

        return new StructuralMaterializationResult(
            structure,
            emittedElementIds,
            sourceUnjoined,
            parentUnjoined);
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
