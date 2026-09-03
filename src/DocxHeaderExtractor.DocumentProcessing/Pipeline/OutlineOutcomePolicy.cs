using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Common terminal policy shared by DOCX- and PDF-derived outlines. Evidence adapters may differ,
/// but none can silently promote a non-empty candidate list to a trusted result.
/// </summary>
internal static class OutlineOutcomePolicy
{
    public static OutlineOutcome Evaluate(
        IReadOnlyList<HeadingRecord> headings,
        string? deterministicRoute,
        DocumentDiagnosticReport? diagnostics,
        PrecisionDecisionAudit decisionAudit,
        bool hasCalibrationProfile)
    {
        if (headings.Count == 0)
            return new OutlineOutcome(OutlineDisposition.Abstained, "no-grounded-headings", deterministicRoute);

        if (decisionAudit.RequiresReview > 0)
            return new OutlineOutcome(OutlineDisposition.RequiresReview, "heading-evidence-requires-review", deterministicRoute);

        // A DOCX conversion diagnostic is a routing signal, not a veto over an outline that was
        // independently recovered from PDF/sidecar evidence and then grounded back to the DOCX.
        if (diagnostics?.Status == "needs_analysis" && !UsesExternalLayoutEvidence(deterministicRoute))
            return new OutlineOutcome(OutlineDisposition.RequiresReview, diagnostics.Reason, deterministicRoute);

        if (!hasCalibrationProfile && decisionAudit.AutoAcceptedCalibrated == 0 && decisionAudit.HumanVerified == 0)
            return new OutlineOutcome(OutlineDisposition.RequiresReview, "unmeasured-evidence-route", deterministicRoute);

        return new OutlineOutcome(OutlineDisposition.Accepted, "evidence-and-calibration-passed", deterministicRoute);
    }

    private static bool UsesExternalLayoutEvidence(string? route) =>
        route?.StartsWith("auto:pdf-", StringComparison.Ordinal) == true ||
        route == "auto:docling-layout";
}
