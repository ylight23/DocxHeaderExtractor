using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Projection boundary for the authority pipeline. Extraction returns source-grounded validated
/// structures; this policy decides what a document-outline product is allowed to emit. It never
/// creates, edits, or accepts a heading.
/// </summary>
public static class PdfValidatedOutputPolicy
{
    public static IReadOnlyList<HeadingRecord> ProjectDocumentOutline(
        IReadOnlyList<HeadingRecord> headings,
        IReadOnlyList<PdfValidatedStructure>? structures = null)
    {
        var structuresById = structures?.ToDictionary(item => item.SourceId, StringComparer.Ordinal) ?? [];
        return headings.Where(heading => heading.HeadingSpan is not null && !string.IsNullOrWhiteSpace(heading.Text) &&
                (string.IsNullOrWhiteSpace(heading.SourceId) || !structuresById.TryGetValue(heading.SourceId, out var structure) ||
                 (!DocumentDomainPolicy.IsExcludedFromOutline(structure.DomainRole) &&
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
