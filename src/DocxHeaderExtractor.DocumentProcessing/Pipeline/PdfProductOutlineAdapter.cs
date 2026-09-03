using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// M9.5b compatibility oracle. <c>DocumentOutline</c>/<c>HeadingRecord</c> is the contract the whole
/// downstream stack (CLI formatters, Web, MCP, <c>AgentHarness</c> validators) already reads with no
/// abstraction over it. Normal runtime now projects through generic structural authority; this class
/// remains as a lossless old-path oracle for R5 parity tests. It does not decide emission, resolve
/// level or parent, or recompute <see cref="PdfProductHeading.RequiresReview"/>.
/// <para>
/// Fields <see cref="HeadingRecord"/> has that <see cref="PdfProductHeading"/> carries no authority
/// for (<c>InlineBody</c>/<c>InlineBodySpan</c>, <c>StyleId</c>, <c>Evidence</c>,
/// <c>ModelConfirmed</c>/<c>CriticConfirmed</c>, <c>Disputed</c>, <c>CalibrationSamples</c>) are left
/// at their honest default rather than silently filled from anywhere else. <c>OriginalText</c> is
/// copied only when ProductOutput carries the canonical paragraph source text.
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
        // ProductOutput text is already the validated source slice. Carry it through the
        // compatibility shell so downstream source-span invariants can inspect the same fact.
        OriginalText = heading.SourceText,
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
