using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// M9.5b compatibility shell. <c>DocumentOutline</c>/<c>HeadingRecord</c> is the contract the whole
/// downstream stack (CLI formatters, Web, MCP, <c>AgentHarness</c> validators) already reads with no
/// abstraction over it, so the pdf-first-authority route still returns one - but every field on it is
/// now a structural copy from <see cref="PdfProductHeading"/>, never a re-derivation. This class does
/// not decide emission, does not resolve level or parent, and does not recompute
/// <see cref="PdfProductHeading.RequiresReview"/> - those are M9.1/M9.2's authority, already settled
/// by the time a heading reaches here.
/// <para>
/// Fields <see cref="HeadingRecord"/> has that <see cref="PdfProductHeading"/> carries no authority
/// for (<c>OriginalText</c>, <c>InlineBody</c>/<c>InlineBodySpan</c>, <c>StyleId</c>, <c>Evidence</c>,
/// <c>ModelConfirmed</c>/<c>CriticConfirmed</c>, <c>Disputed</c>, <c>CalibrationSamples</c>) are left
/// at their honest default rather than silently filled from anywhere else - the M9 lane simply does
/// not track those distinctions, and guessing one would misrepresent it as authority this route has.
/// </para>
/// </summary>
internal static class PdfProductOutlineAdapter
{
    /// <summary>Route-specific label so an audit can tell an M9-authority heading from a legacy one.</summary>
    public const string BoundarySource = "pdf-final-structure-v1";

    /// <summary>
    /// Not a calibrated probability - the M9 authority is binary (validated by the M9.2 decision gate
    /// or not emitted at all) - documented via <see cref="ConfidenceBasisValue"/> rather than implying
    /// a holdout-measured number, the same way the legacy route's own fixed confidence values do.
    /// </summary>
    public const double Confidence = 1.0;

    public const string ConfidenceBasisValue = "pdf-final-structure-validated";

    public static IReadOnlyList<HeadingRecord> ToHeadingRecords(PdfProductOutput output) =>
        output.Headings.Select(ToHeadingRecord).ToArray();

    public static HeadingRecord ToHeadingRecord(PdfProductHeading heading) => new()
    {
        Index = heading.ParagraphIndex,
        StableId = heading.StableId,
        SourceId = heading.Id,
        Level = heading.Level,
        Text = heading.Text,
        HeadingSpan = new TextOffsetSpan(heading.Span.Start, heading.Span.End),
        BoundarySource = BoundarySource,
        Source = HeadingSource.Model,
        Confidence = Confidence,
        // RequiresReview is M9.2's own decision, copied verbatim - not re-litigated here. It is true
        // for every heading PdfOutputDecisionPolicy emits today, so this route's headings always
        // required review, exactly like the legacy policy's unconditional assignment did.
        DecisionStatus = heading.RequiresReview
            ? HeadingDecisionStatus.RequiresReview
            : HeadingDecisionStatus.AutoAcceptedEvidence,
        ConfidenceBasis = ConfidenceBasisValue,
        AcceptanceSignature = heading.Reasons.Count > 0 ? string.Join(",", heading.Reasons) : null,
    };
}
