using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfFinancialReportOutlineTests
{
    [Fact]
    public void WbgTrustFund051MatchesPdfOutlineWithGroupsAndCostRecoveryChild()
    {
        var docx = Path.Combine(
            "todo10_8", "heading_corpus_95_word", "03_tai_chinh_ke_toan",
            "051_WBG_Trust_Fund_FIS_June_2024.docx");
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "03_tai_chinh_ke_toan",
            "051_WBG_Trust_Fund_FIS_June_2024.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor().Extract(docx);
        var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);

        var result = PdfFinancialReportOutline.TryBuild(docx, slim, mode);

        Assert.Equal(30, result.Headings.Count);
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Key Trust Fund Activity");
        Assert.Contains(result.Headings, h => h.Level == 2 && h.Text == "Trust Fund Asset Summary");
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Contribution and Receivables");
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Investments");
        Assert.Equal(2, result.Headings.Count(h => h.Text == "Cost Recovery"));
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Cost Recovery");
        Assert.Contains(result.Headings, h => h.Level == 2 && h.Text == "Cost Recovery");
        Assert.All(result.Headings, h => Assert.Equal(PdfFinancialReportOutline.Basis, h.ConfidenceBasis));

        // Key người dùng là page-level outline: continuation/duplicate vẫn là heading nguồn riêng.
        Assert.Contains(result.Headings, h => h.Text.Contains("cont'd", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Headings, h => h.Text == "New Administration Agreements");
        Assert.Contains(result.Headings, h => h.Text.Contains("New Administration Agreements (Cont", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, result.Headings.Count(h => h.Text.StartsWith("Cash and Investments", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void WbgTrustFund052KeepsRepeatedPageHeadings()
    {
        var docx = Path.Combine(
            "todo10_8", "heading_corpus_95_word", "03_tai_chinh_ke_toan",
            "052_WBG_Trust_Fund_FIS_December_2025.docx");
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "03_tai_chinh_ke_toan",
            "052_WBG_Trust_Fund_FIS_December_2025.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor().Extract(docx);
        var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);

        var result = PdfFinancialReportOutline.TryBuild(docx, slim, mode);

        Assert.Equal(32, result.Headings.Count);
        Assert.Equal(2, result.Headings.Count(h => h.Text == "Portfolio at a Glance - IBRD/IDA/IFC Trust Funds"));
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Key Trust Fund Activity");
        Assert.Contains(result.Headings, h => h.Level == 2 && h.Text == "Composition of Active Umbrella Programs");
        Assert.Equal(3, result.Headings.Count(h => h.Text == "Cost Recovery"));
        Assert.Equal(1, result.Headings.Count(h => h.Level == 1 && h.Text == "Cost Recovery"));
        Assert.Equal(2, result.Headings.Count(h => h.Level == 2 && h.Text == "Cost Recovery"));
        Assert.All(result.Headings, h => Assert.Equal(PdfFinancialReportOutline.Basis, h.ConfidenceBasis));
    }

    private static AnswerKey ResolveKey(string docx, string keyPath)
    {
        var slim = new DocxSlimExtractor().Extract(docx);
        var stableIdToIndex = slim.Paragraphs.ToDictionary(p => p.StableId, p => p.Index);
        return AnswerKey.Load(keyPath).ResolveStableIds(stableIdToIndex);
    }

    private static DocScore Score(DocumentOutline outline, AnswerKey key) =>
        Evaluator.Score(
            outline.File,
            outline,
            outline.Headings.Select(h => h.Index).ToHashSet(),
            key);

    private static void AssertPerfect(DocScore score)
    {
        Assert.Equal(score.TruthCount, score.ResultCount);
        Assert.Equal(1.0, score.Precision);
        Assert.Equal(1.0, score.Recall);
        Assert.Equal(1.0, score.F1);
        Assert.Equal(1.0, score.NavigationRecall);
        Assert.Equal(1.0, score.NavigationLevelAccuracy);
        Assert.Empty(score.FalsePositives);
        Assert.Empty(score.FalseNegatives);
        Assert.Empty(score.WrongLevels);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
