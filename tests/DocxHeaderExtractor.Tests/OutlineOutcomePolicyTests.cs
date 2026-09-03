using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class OutlineOutcomePolicyTests
{
    [Fact]
    public void Empty_outline_abstains()
    {
        var outcome = OutlineOutcomePolicy.Evaluate([], null, null, Audit(requiresReview: 0), hasCalibrationProfile: false);

        Assert.Equal(OutlineDisposition.Abstained, outcome.Disposition);
        Assert.Equal("no-grounded-headings", outcome.Reason);
    }

    [Fact]
    public void Unmeasured_pdf_evidence_requires_review_even_when_headings_exist()
    {
        var outcome = OutlineOutcomePolicy.Evaluate([Heading()], "auto:pdf-financial-report", null, Audit(requiresReview: 0), hasCalibrationProfile: false);

        Assert.Equal(OutlineDisposition.RequiresReview, outcome.Disposition);
        Assert.Equal("unmeasured-evidence-route", outcome.Reason);
    }

    [Fact]
    public void Pdf_recovery_is_not_rejected_by_the_docx_conversion_diagnostic()
    {
        var diagnostics = new DocumentDiagnosticReport(
            "needs_analysis",
            "merged_layout_without_valid_candidate",
            new StyleSignalDiagnostic(0, 0, 0, 0, 0, false, false, false),
            new LayoutSignalDiagnostic(1, 0, 0, 0),
            []);

        var outcome = OutlineOutcomePolicy.Evaluate(
            [Heading()],
            "auto:pdf-financial-report",
            diagnostics,
            Audit(requiresReview: 0),
            hasCalibrationProfile: false);

        Assert.Equal(OutlineDisposition.RequiresReview, outcome.Disposition);
        Assert.Equal("unmeasured-evidence-route", outcome.Reason);
    }

    [Fact]
    public void Calibrated_clean_result_is_accepted()
    {
        var outcome = OutlineOutcomePolicy.Evaluate([Heading()], "auto:rfc-toc-dictionary", null, Audit(requiresReview: 0, calibrated: 1), hasCalibrationProfile: true);

        Assert.Equal(OutlineDisposition.Accepted, outcome.Disposition);
    }

    private static HeadingRecord Heading() => new()
    {
        Index = 1,
        Level = 1,
        Text = "Introduction",
        Source = HeadingSource.Structure,
    };

    private static PrecisionDecisionAudit Audit(int requiresReview, int calibrated = 0) => new(
        AutoAcceptedTotal: calibrated,
        AutoAcceptedCalibrated: calibrated,
        AutoAcceptedDeterministic: 0,
        AutoAcceptedUncalibratedEvidence: 0,
        HumanVerified: 0,
        RequiresReview: requiresReview,
        ByConfidenceBasis: new Dictionary<string, int>());
}
