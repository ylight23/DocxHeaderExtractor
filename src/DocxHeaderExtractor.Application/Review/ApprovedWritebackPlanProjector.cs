using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Application.Review;

public static class ApprovedWritebackPlanProjector
{
    public static ApprovedWritebackPlan Build(
        DocumentReviewResult review,
        IReadOnlyList<HumanReviewRecord> records,
        SourceDocument? source = null,
        bool allowSourceDocumentIdAlias = false)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(records);
        if (source is not null && !allowSourceDocumentIdAlias &&
            !string.Equals(review.DocumentId, source.DocumentId, StringComparison.Ordinal))
            return Deferred(review.DocumentId, "review-source-document-mismatch", []);

        var latest = new Dictionary<string, HumanReviewRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!string.Equals(record.DocumentId, review.DocumentId, StringComparison.Ordinal))
                return Deferred(review.DocumentId, "review-record-document-mismatch", []);
            latest[record.Decision.HeadingId] = record;
        }

        var knownHeadingIds = review.Headings.Select(heading => heading.HeadingId)
            .ToHashSet(StringComparer.Ordinal);
        if (latest.Keys.Any(id => !knownHeadingIds.Contains(id)))
            return Deferred(review.DocumentId, "review-record-unknown-heading", []);

        var decisions = new List<ReviewedHeadingDecision>();
        foreach (var heading in review.Headings)
        {
            var paragraph = ResolveParagraph(heading, source);
            if (paragraph is null)
                return Deferred(review.DocumentId, $"writeback-source-unresolved:{heading.HeadingId}", decisions);

            var sourceText = paragraph.Text;
            var span = new TextOffsetSpan(heading.Span.Start, heading.Span.End);
            if (span.Start < 0 || span.End <= span.Start || span.End > sourceText.Length)
                return Deferred(review.DocumentId, $"writeback-span-invalid:{heading.HeadingId}", decisions);

            var originalText = sourceText[span.Start..span.End];
            if (heading.Status.Equals("RequiresReview", StringComparison.OrdinalIgnoreCase) &&
                !latest.ContainsKey(heading.HeadingId))
                return Deferred(review.DocumentId, $"writeback-review-pending:{heading.HeadingId}", decisions);

            latest.TryGetValue(heading.HeadingId, out var record);
            var action = record?.Decision.Action;
            var state = record?.State;
            var text = originalText;
            var level = heading.Level;
            var comment = record?.Decision.Comment;
            var include = true;

            if (record is not null)
            {
                switch (record.Decision.Action)
                {
                    case HumanReviewAction.Accept:
                        break;
                    case HumanReviewAction.Reject:
                        include = false;
                        break;
                    case HumanReviewAction.Correct:
                        if (record.Decision.CorrectedText is { } correctedText)
                        {
                            // This writeback boundary only accepts text already proven to occupy
                            // the reviewed source span. It never invents or relocates content.
                            if (!string.Equals(correctedText, originalText, StringComparison.Ordinal))
                                return Deferred(review.DocumentId,
                                    $"writeback-corrected-text-not-source-backed:{heading.HeadingId}",
                                    decisions);
                            text = correctedText;
                        }
                        if (record.Decision.CorrectedLevel is { } correctedLevel)
                            level = correctedLevel;
                        break;
                    default:
                        return Deferred(review.DocumentId, $"writeback-action-invalid:{heading.HeadingId}", decisions);
                }
            }

            decisions.Add(new ReviewedHeadingDecision(
                heading.HeadingId,
                paragraph.SourceId,
                paragraph.SourceOrdinal,
                sourceText,
                text,
                level,
                span,
                action,
                state,
                include,
                comment));
        }

        return new ApprovedWritebackPlan(review.DocumentId, ApprovedWritebackPlanStatus.Ready,
            "all-review-gates-satisfied", decisions);
    }

    private static SourceParagraph? ResolveParagraph(
        ReviewHeadingDto heading,
        SourceDocument? source)
    {
        if (source is null)
        {
            return new SourceParagraph
            {
                SourceId = heading.Provenance.SourceId,
                SourceOrdinal = heading.Provenance.ParagraphIndex ?? 0,
                Text = heading.Text,
                Style = new SourceStyleFacts(),
                Numbering = new SourceNumberingFacts(),
                Layout = new SourceLayoutFacts(),
            };
        }

        var byId = source.Paragraphs.FirstOrDefault(paragraph =>
            string.Equals(paragraph.SourceId, heading.Provenance.SourceId, StringComparison.Ordinal));
        return byId ?? (heading.Provenance.ParagraphIndex is { } ordinal
            ? source.Paragraphs.FirstOrDefault(paragraph => paragraph.SourceOrdinal == ordinal)
            : null);
    }

    private static ApprovedWritebackPlan Deferred(
        string documentId,
        string reason,
        IReadOnlyList<ReviewedHeadingDecision> decisions) =>
        new(documentId, ApprovedWritebackPlanStatus.DeferredToHuman, reason, decisions);
}
