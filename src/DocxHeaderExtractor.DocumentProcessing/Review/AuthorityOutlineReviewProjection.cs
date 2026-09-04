using DocxHeaderExtractor.Application.Review;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using ReviewTextOffsetSpan = DocxHeaderExtractor.Application.Review.TextOffsetSpan;

namespace DocxHeaderExtractor.DocumentProcessing.Review;

/// <summary>
/// Projects the validated outline into the human-review contract without mutating authority output.
/// Text and source identity come from parser-owned SourceDocument paragraphs.
/// </summary>
public static class AuthorityOutlineReviewProjection
{
    public static DocumentReviewResult ToReviewResult(DocumentOutline outline, SourceDocument source) =>
        Project(outline, source);

    public static DocumentReviewResult Project(DocumentOutline outline, SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(source);

        var paragraphs = source.Paragraphs;
        var diagnostics = new List<ReviewDiagnosticDto>();
        var headings = new List<ReviewHeadingDto>();
        foreach (var heading in outline.Headings)
        {
            var paragraph = ResolveParagraph(heading, paragraphs);
            if (paragraph is null)
            {
                diagnostics.Add(new ReviewDiagnosticDto(
                    "review.heading-source-unresolved", "error", heading.SourceId ?? heading.StableId,
                    $"Không tìm thấy source paragraph cho heading '{heading.StableId ?? heading.Index.ToString()}'.",
                    "authority-outline-review-projection"));
                continue;
            }

            var span = ResolveSpan(heading, paragraph.Text);
            if (span is null)
            {
                diagnostics.Add(new ReviewDiagnosticDto(
                    "projection.heading-span-unresolved", "warning", paragraph.SourceId,
                    $"Không xác định được span cho heading '{paragraph.SourceId}'.",
                    "authority-outline-review-projection"));
                continue;
            }

            if (heading.Level is null || heading.Level < 1 || heading.Level > 9)
            {
                diagnostics.Add(new ReviewDiagnosticDto(
                    "projection.heading-level-unresolved", "warning", paragraph.SourceId,
                    $"Không xác định được level cho heading '{paragraph.SourceId}'.",
                    "authority-outline-review-projection"));
                continue;
            }

            var level = heading.Level.Value;
            headings.Add(new ReviewHeadingDto(
                paragraph.SourceId,
                paragraph.Text[span.Start..span.End],
                level,
                new ReviewTextOffsetSpan(span.Start, span.End),
                Math.Clamp(heading.Confidence, 0d, 1d),
                heading.DecisionStatus.ToString(),
                Evidence(heading.Evidence),
                new HeadingProvenanceDto(
                    paragraph.SourceId,
                    source.SourceKind,
                    paragraph.SourceOrdinal,
                    null,
                string.IsNullOrWhiteSpace(heading.ConfidenceBasis)
                    ? heading.BoundarySource ?? heading.Source.ToString()
                    : heading.ConfidenceBasis)));
        }

        return new DocumentReviewResult(
            source.DocumentId,
            headings,
            diagnostics,
            new ReviewSummaryDto(
                headings.Count,
                headings.Count(h => string.Equals(
                    h.Status, HeadingDecisionStatus.RequiresReview.ToString(), StringComparison.Ordinal)),
                diagnostics.Count));
    }

    private static SourceParagraph? ResolveParagraph(
        HeadingRecord heading,
        IReadOnlyList<SourceParagraph> paragraphs)
    {
        if (heading.SourceId is { Length: > 0 } sourceId)
        {
            var byId = paragraphs.FirstOrDefault(p =>
                string.Equals(p.SourceId, sourceId, StringComparison.Ordinal));
            if (byId is not null) return byId;
        }

        if (heading.StableId is { Length: > 0 } stableId)
        {
            var byStableId = paragraphs.FirstOrDefault(p =>
                string.Equals(p.StableId, stableId, StringComparison.Ordinal));
            if (byStableId is not null) return byStableId;
        }

        return paragraphs.FirstOrDefault(p => p.SourceOrdinal == heading.Index);
    }

    private static ReviewTextOffsetSpan? ResolveSpan(HeadingRecord heading, string sourceText)
    {
        if (heading.HeadingSpan is { } span && span.Start >= 0 && span.End > span.Start &&
            span.End <= sourceText.Length)
            return new ReviewTextOffsetSpan(span.Start, span.End);

        if (string.IsNullOrEmpty(heading.Text)) return null;
        var first = sourceText.IndexOf(heading.Text, StringComparison.Ordinal);
        if (first < 0 || sourceText.IndexOf(heading.Text, first + heading.Text.Length,
                StringComparison.Ordinal) >= 0)
            return null;
        return new ReviewTextOffsetSpan(first, first + heading.Text.Length);
    }

    private static IReadOnlyList<HeadingEvidenceDto> Evidence(HeadingEvidence? evidence)
    {
        if (evidence is null) return [];
        return
        [
            new("numberingValid", evidence.NumberingValid.ToString(), "authority-outline"),
            new("siblingSequenceValid", evidence.SiblingSequenceValid.ToString(), "authority-outline"),
            new("formattingConsistent", evidence.FormattingConsistent.ToString(), "authority-outline"),
            new("modelConfirmed", evidence.ModelConfirmed.ToString(), "authority-outline"),
            new("treeValid", evidence.TreeValid.ToString(), "authority-outline"),
            new("status", evidence.Status, "authority-outline"),
        ];
    }
}
