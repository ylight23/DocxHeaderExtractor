using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Repair;

namespace DocxHeaderExtractor.Tests;

public sealed class RepairValidationGateTests
{
    [Fact]
    public void CandidateReportMarksCrossRouteScoreAsUntrustedAndKeepsRouteMetrics()
    {
        var outline = Outline(
            "auto:rfc-toc-dictionary",
            (10, 1, "1. Introduction"),
            (20, 2, "1.1. Requirements Notation"));

        var report = RepairCandidateRunner.Analyze(outline);
        var best = Assert.Single(report.Candidates);

        Assert.Equal("untrusted_cross_route_score", report.ScoreCalibrationStatus);
        Assert.Equal("untrusted_until_route_calibrated", best.ScoreCalibrationStatus);
        Assert.Contains("duplicateRate", best.RouteMetrics.Keys);
        Assert.Contains("titlePollutionRate", best.RouteMetrics.Keys);
    }

    [Fact]
    public void PdfBoldLabelGateFailsFragmentAndCoverArtifacts()
    {
        var outline = Outline(
            "auto:pdf-bold-label",
            (3, 1, "INTERNATIONAL COMPARISON PROGRAM (ICP"),
            (3, 1, "FEBRUARY 26, 2023"),
            (3, 1, "Session II: Update on the"),
            (7, 1, "Regional progress reports"));
        var candidates = RepairCandidateRunner.Analyze(outline);

        var report = RepairValidationGate.Validate(outline, candidates);

        Assert.False(report.Passed);
        Assert.Contains(report.Gates, g => g.Name == "pdf_bold_fragment_rate" && !g.Passed);
        Assert.Contains(report.Gates, g => g.Name == "pdf_bold_cover_artifact_rate" && !g.Passed);
    }

    [Fact]
    public void PartSectionGateFailsWhenSectionNumbersDecrease()
    {
        var outline = Outline(
            "auto:part-section-text-toc",
            (22, 2, "Section 2. Instructions to Consultants (ITC)"),
            (22, 2, "Section 1. Request for Proposal Letter"),
            (30, 2, "Section 2. Instructions to Consultants and Data Sheet"));
        var candidates = RepairCandidateRunner.Analyze(outline);

        var report = RepairValidationGate.Validate(outline, candidates);

        Assert.False(report.Passed);
        Assert.Contains(report.Gates, g => g.Name == "part_section_number_order" && !g.Passed);
    }

    private static DocumentOutline Outline(string route, params (int Index, int Level, string Text)[] headings) =>
        new()
        {
            File = "test.docx",
            ParagraphCount = 100,
            CandidateCount = headings.Length,
            DeterministicRoute = route,
            Headings = headings.Select(h => new HeadingRecord
            {
                Index = h.Index,
                Level = h.Level,
                Text = h.Text,
                Source = HeadingSource.Structure,
                Confidence = 0.95,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = route,
            }).ToList(),
        };
}
