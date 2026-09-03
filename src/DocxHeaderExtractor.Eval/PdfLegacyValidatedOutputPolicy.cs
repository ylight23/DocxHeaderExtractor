using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval;

/// <summary>
/// Historical projection retained exclusively for evaluation artifacts. It is isolated from the
/// Core runtime assembly so normal extraction cannot depend on the legacy PDF lane.
/// </summary>
public static class PdfLegacyValidatedOutputPolicy
{
    public static IReadOnlyList<HeadingRecord> ProjectDocumentOutline(
        IReadOnlyList<HeadingRecord> headings,
        IReadOnlyList<PdfValidatedStructure>? structures = null)
    {
        var structuresById = structures?.ToDictionary(item => item.SourceId, StringComparer.Ordinal) ?? [];
        return headings.Where(heading => heading.HeadingSpan is not null && !string.IsNullOrWhiteSpace(heading.Text) &&
                (string.IsNullOrWhiteSpace(heading.SourceId) || !structuresById.TryGetValue(heading.SourceId, out var structure) ||
                 (!structure.DomainExclusionProposed &&
                  !DocumentDomainPolicy.EvidenceForRole(structure.DomainRole, "legacy-domain-fact").ProposesOutlineExclusion &&
                  structure.StructuralScope != "embedded_amendment" &&
                  structure.StructuralScope != "quoted_replacement" &&
                  structure.StructuralScope != "appendix_table" &&
                  structure.Decision == "requires_review")))
            .Select(heading =>
            {
                heading.DecisionStatus = HeadingDecisionStatus.RequiresReview;
                heading.ConfidenceBasis = "pdf-first-validated-structure-review";
                return heading;
            })
            .OrderBy(heading => heading.Index)
            .ThenBy(heading => heading.HeadingSpan!.Start)
            .ToArray();
    }

    /// <summary>Historical legal/contract tree projection used only by frozen evaluation tooling.</summary>
    public static IReadOnlyList<HeadingRecord> ProjectFullStructuralTree(IReadOnlyList<HeadingRecord> headings) =>
        ProjectDocumentOutline(headings, []);
}
