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

        Assert.Equal(25, result.Headings.Count);
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Key Trust Fund Activity");
        Assert.Contains(result.Headings, h => h.Level == 2 && h.Text == "Trust Fund Asset Summary");
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Contribution and Receivables");
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Investments");
        Assert.Equal(2, result.Headings.Count(h => h.Text == "Cost Recovery"));
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Cost Recovery");
        Assert.Contains(result.Headings, h => h.Level == 2 && h.Text == "Cost Recovery");
        Assert.All(result.Headings, h => Assert.Equal(PdfFinancialReportOutline.Basis, h.ConfidenceBasis));

        // Luật A: mọi trang mang marker tiếp nối tự phát hiện — "(cont'd)"/"(Cont'd)" — bị gộp vào
        // node mở gần nhất cùng cấp; không còn dòng "(cont'd)" nào lọt ra ngoài.
        Assert.DoesNotContain(result.Headings, h => h.Text.Contains("cont'd", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Headings, h => h.Text == "New Administration Agreements");
        Assert.Contains(result.Headings, h => h.Text == "Cash Contributions");
        Assert.Contains(result.Headings, h => h.Text == "Contributions Receivable");
        Assert.Equal(1, result.Headings.Count(h => h.Text == "Cash and Investments"));
    }

    [Fact]
    public async Task PipelineWbgTrustFund051MatchesAuthoritativeUserKey()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "03_tai_chinh_ke_toan",
            "051_WBG_Trust_Fund_FIS_June_2024.docx");
        var pdf = Path.Combine(
            root, "todo10_8", "heading_corpus_100", "03_tai_chinh_ke_toan",
            "051_WBG_Trust_Fund_FIS_June_2024.pdf");
        var keyPath = Path.Combine(root, "keys", "partial-human", "051_WBG_Trust_Fund_FIS_June_2024.key");
        if (!File.Exists(docx) || !File.Exists(pdf) || !File.Exists(keyPath)) return;

        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var outline = await pipeline.RunAsync(docx);
        var key = ResolveKey(docx, keyPath);
        var score = Score(outline, key);

        Assert.Equal("auto:pdf-financial-report", outline.DeterministicRoute);
        AssertPerfect(score);
    }

    [Fact]
    public void WbgTrustFund052MergesRepeatedPortfolioAndCostRecoveryPages()
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

        // "Portfolio at a Glance - IBRD/IDA/IFC Trust Funds" và "Cost Recovery" (cấp 2) đều trải hai
        // trang liền kề, nguyên văn, không nhãn "(cont'd)" — luật B gộp thành một node mỗi mục.
        Assert.Equal(25, result.Headings.Count);
        Assert.Equal(1, result.Headings.Count(h => h.Text == "Portfolio at a Glance - IBRD/IDA/IFC Trust Funds"));
        Assert.Contains(result.Headings, h => h.Level == 1 && h.Text == "Key Trust Fund Activity");
        Assert.Contains(result.Headings, h => h.Level == 2 && h.Text == "Composition of Active Umbrella Programs");
        Assert.Equal(2, result.Headings.Count(h => h.Text == "Cost Recovery"));
        Assert.Equal(1, result.Headings.Count(h => h.Level == 1 && h.Text == "Cost Recovery"));
        Assert.Equal(1, result.Headings.Count(h => h.Level == 2 && h.Text == "Cost Recovery"));
        Assert.All(result.Headings, h => Assert.Equal(PdfFinancialReportOutline.Basis, h.ConfidenceBasis));
    }

    [Fact]
    public async Task PipelineWbgTrustFund052MatchesAuthoritativeUserKey()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "03_tai_chinh_ke_toan",
            "052_WBG_Trust_Fund_FIS_December_2025.docx");
        var pdf = Path.Combine(
            root, "todo10_8", "heading_corpus_100", "03_tai_chinh_ke_toan",
            "052_WBG_Trust_Fund_FIS_December_2025.pdf");
        var keyPath = Path.Combine(root, "keys", "partial-human", "052_WBG_Trust_Fund_FIS_December_2025.key");
        if (!File.Exists(docx) || !File.Exists(pdf) || !File.Exists(keyPath)) return;

        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var outline = await pipeline.RunAsync(docx);
        var key = ResolveKey(docx, keyPath);
        var score = Score(outline, key);

        Assert.Equal("auto:pdf-financial-report", outline.DeterministicRoute);
        AssertPerfect(score);
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
