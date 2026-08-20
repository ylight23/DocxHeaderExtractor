using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Repair;

namespace DocxHeaderExtractor.Tests;

public sealed class RepairDiagnosticGateTests
{
    [Fact]
    public void ReviewRate_counts_requires_review_and_disputed_only()
    {
        var headings = new List<HeadingRecord>
        {
            new() { Index = 1, Level = 1, Text = "a", DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence },
            new() { Index = 2, Level = 1, Text = "b", DecisionStatus = HeadingDecisionStatus.RequiresReview },
            new() { Index = 3, Level = 1, Text = "c", DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence, Disputed = true },
            new() { Index = 4, Level = 1, Text = "d", DecisionStatus = HeadingDecisionStatus.AutoAcceptedCalibrated },
        };

        Assert.Equal(0.5, RepairDiagnosticGate.ReviewRate(headings));
    }

    [Fact]
    public void ReviewRate_of_empty_outline_is_zero()
    {
        Assert.Equal(0, RepairDiagnosticGate.ReviewRate([]));
    }

    [Fact]
    public void Evaluate_flags_file_far_above_corpus_median_and_absolute_floor()
    {
        var rows = new[]
        {
            Row("clean-1", 0.02),
            Row("clean-2", 0.03),
            Row("clean-3", 0.04),
            Row("clean-4", 0.05),
            Row("broken-upstream", 0.60), // heading thật bị cắt vụn -> nhiều mảnh mơ hồ
        };

        var results = RepairDiagnosticGate.Evaluate(rows);

        var broken = results.Single(r => r.File == "broken-upstream");
        Assert.True(broken.SuspectedUpstreamError);
        Assert.True(broken.RatioToMedian > RepairDiagnosticGate.OutlierMultiplier);
        Assert.Contains("nghi lỗi tầng đọc/tách", broken.Reason);

        foreach (var clean in results.Where(r => r.File.StartsWith("clean")))
            Assert.False(clean.SuspectedUpstreamError);
    }

    [Fact]
    public void Evaluate_does_not_flag_small_absolute_rate_even_if_corpus_is_near_zero()
    {
        // Trung vị corpus ~0 (đa số file sạch tuyệt đối) — một file chỉ có 1 mục mơ hồ thật sự (tỷ lệ
        // nhỏ, dưới sàn tuyệt đối) không nên bị coi là lỗi tầng đọc/tách.
        var rows = new[]
        {
            Row("clean-1", 0.0),
            Row("clean-2", 0.0),
            Row("clean-3", 0.0),
            Row("slightly-ambiguous", 0.04), // dưới MinimumReviewRateFloor (0.05)
        };

        var results = RepairDiagnosticGate.Evaluate(rows);

        Assert.False(results.Single(r => r.File == "slightly-ambiguous").SuspectedUpstreamError);
    }

    [Fact]
    public void Evaluate_reports_same_corpus_median_on_every_row()
    {
        var rows = new[] { Row("a", 0.10), Row("b", 0.20), Row("c", 0.30) };

        var results = RepairDiagnosticGate.Evaluate(rows);

        Assert.Equal(0.20, results[0].CorpusMedianReviewRate);
        Assert.All(results, r => Assert.Equal(0.20, r.CorpusMedianReviewRate));
    }

    private static RepairCorpusAuditRow Row(string file, double reviewRate) => new(
        File: file,
        SourcePath: file,
        Group: "test",
        HasKey: false,
        KeyPaths: [],
        DocumentMode: null,
        CurrentRoute: "auto:test",
        BestRoute: "auto:test",
        BaselineRoute: "auto:test",
        BaselineMatchedCurrent: true,
        GatePassed: true,
        FailedGates: [],
        NeedsAnalysis: false,
        DiagnosticStatus: "normal",
        DiagnosticReason: null,
        ParagraphCount: 10,
        CandidateCount: 5,
        HeadingCount: 5,
        DisputedCount: 0,
        ReviewRate: reviewRate,
        SuspectedUpstreamError: false,
        DiagnosticGateReason: null,
        Error: null);
}
