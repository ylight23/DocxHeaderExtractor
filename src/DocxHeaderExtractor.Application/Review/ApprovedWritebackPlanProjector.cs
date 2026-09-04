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
            if (!latest.TryGetValue(record.Decision.HeadingId, out var previous) ||
                record.RecordedAtUtc >= previous.RecordedAtUtc)
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
                            // A correction may narrow the reviewed range only when the replacement
                            // is a unique, source-backed substring. It never invents or relocates text.
                            var correctedSpan = span;
                            if (!string.Equals(correctedText, originalText, StringComparison.Ordinal) &&
                                !TryNarrowToUniqueSourceMatch(
                                    sourceText, span, correctedText, out correctedSpan))
                                return Deferred(review.DocumentId,
                                    $"writeback-corrected-text-not-source-backed:{heading.HeadingId}",
                                    decisions);
                            if (!string.Equals(correctedText, originalText, StringComparison.Ordinal))
                            {
                                span = correctedSpan;
                                originalText = sourceText[span.Start..span.End];
                            }
                            text = correctedText;
                        }
                        if (record.Decision.CorrectedLevel is { } correctedLevel)
                        {
                            if (correctedLevel is < 1 or > 9)
                                return Deferred(review.DocumentId,
                                    $"writeback-corrected-level-invalid:{heading.HeadingId}", decisions);
                            level = correctedLevel;
                        }
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

    private static bool TryNarrowToUniqueSourceMatch(
        string sourceText,
        TextOffsetSpan reviewedSpan,
        string correctedText,
        out TextOffsetSpan correctedSpan)
    {
        correctedSpan = new TextOffsetSpan(0, 0);
        if (correctedText.Length == 0 || correctedText.Length > reviewedSpan.End - reviewedSpan.Start)
            return false;

        var match = sourceText.IndexOf(correctedText, reviewedSpan.Start, StringComparison.Ordinal);
        if (match < reviewedSpan.Start || match + correctedText.Length > reviewedSpan.End)
            return false;
        if (sourceText.IndexOf(correctedText, match + 1, StringComparison.Ordinal) is var next &&
            next >= reviewedSpan.Start && next + correctedText.Length <= reviewedSpan.End)
            return false;

        correctedSpan = new TextOffsetSpan(match, match + correctedText.Length);
        return true;
    }
}
