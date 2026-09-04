using DocxHeaderExtractor.Application.Review;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using AuthorityTextOffsetSpan = DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan;

namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>Applies an explicitly approved, source-backed review plan to a DOCX copy.</summary>
public static class ApprovedWritebackExecutor
{
    public static OutlineWritebackResult Apply(
        string sourceDocxPath,
        string targetPath,
        ApprovedWritebackPlan plan,
        ExtractionOptions extraction,
        bool explicitApproval,
        bool allowSourceDocumentIdAlias = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(extraction);
        if (!explicitApproval)
            throw new InvalidOperationException("writeback-explicit-approval-required");
        if (!plan.IsReady)
            throw new InvalidOperationException($"writeback-plan-not-ready:{plan.Reason}");

        var source = new OpenXmlDocumentSource(extraction).Read(sourceDocxPath);
        if (!allowSourceDocumentIdAlias &&
            !string.Equals(source.DocumentId, plan.DocumentId, StringComparison.Ordinal))
            throw new InvalidOperationException("writeback-document-id-mismatch");

        var headings = plan.Headings
            .Where(item => item.IncludeInWriteback)
            .Select(item =>
            {
                var paragraph = source.Paragraphs.FirstOrDefault(candidate =>
                    string.Equals(candidate.SourceId, item.SourceId, StringComparison.Ordinal) &&
                    candidate.SourceOrdinal == item.SourceOrdinal);
                if (paragraph is null || !string.Equals(paragraph.Text, item.SourceText, StringComparison.Ordinal))
                    throw new InvalidOperationException($"writeback-source-mismatch:{item.HeadingId}");
                if (item.Span.Start < 0 || item.Span.End <= item.Span.Start ||
                    item.Span.End > paragraph.Text.Length)
                    throw new InvalidOperationException($"writeback-span-invalid:{item.HeadingId}");

                return new HeadingRecord
                {
                    Index = item.SourceOrdinal,
                    StableId = item.SourceId,
                    SourceId = item.SourceId,
                    Level = item.Level,
                    Text = item.Text,
                    OriginalText = item.SourceText,
                    HeadingSpan = new AuthorityTextOffsetSpan(item.Span.Start, item.Span.End),
                    Source = HeadingSource.HumanCorrection,
                    Confidence = 1d,
                    DecisionStatus = HeadingDecisionStatus.HumanVerified,
                    ConfidenceBasis = "human-review-approved-writeback",
                };
            })
            .ToArray();

        var outline = new DocumentOutline
        {
            File = Path.GetFileName(sourceDocxPath),
            ParagraphCount = source.Paragraphs.Count,
            CandidateCount = headings.Length,
            Headings = headings,
        };
        return OutlineWriteback.Apply(
            sourceDocxPath,
            targetPath,
            outline,
            extraction,
            new OutlineWritebackOptions { Overwrite = true });
    }
}
