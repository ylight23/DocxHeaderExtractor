using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Eval;

/// <summary>
/// M9.5c historical projection retained exclusively for M9.4 diagnostic/evaluation artifacts.
/// It is not an output authority and must not be referenced by the production PDF-first route.
/// The new production authority is <c>PdfFinalStructure -&gt; PdfOutputDecision -&gt;
/// PdfProductOutput</c>; this helper exists only to reproduce the previous lane from the same
/// frozen validated input for comparisons.
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

    /// <summary>Legal/contract tree projection keeps clause and point nodes after the same validation.</summary>
    public static IReadOnlyList<HeadingRecord> ProjectFullStructuralTree(IReadOnlyList<HeadingRecord> headings) =>
        ProjectDocumentOutline(headings, []);
}
