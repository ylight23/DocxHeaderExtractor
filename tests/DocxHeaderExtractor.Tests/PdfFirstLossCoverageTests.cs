using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.1e-B1 locks. How completely a heading is represented is reported separately from where it is
/// first blocked, because on this corpus the two differ: a heading can reach the model truncated
/// while the candidate carrying all of it never reaches the budget at all.
/// </summary>
public sealed class PdfFirstLossCoverageTests
{
    [Fact]
    public void CoverageIsReportedSeparatelyFromTheGateThatBlocks()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, @"todo10_8\heading_corpus_95_word\03_tai_chinh_ke_toan\054_IBRD_Information_Statement_FY25.docx");
        var keyPath = Path.Combine(root, @"keys\rebased\054_IBRD_Information_Statement_FY25.v3-occurrence-reviewed.key");
        var bridgePath = Path.Combine(root, @"keys\occurrence-bridge\054_IBRD_Information_Statement_FY25.occurrence-bridge.json");
        if (!File.Exists(docx) || !File.Exists(keyPath) || !File.Exists(bridgePath)) return;

        var slim = new DocxSlimExtractor().Extract(docx);
        var rawKey = AnswerKey.Load(keyPath);
        var stableMap = slim.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.StableId))
            .ToDictionary(p => p.StableId!, p => p.Index, StringComparer.Ordinal);
        var goldStableIds = rawKey.PositiveEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Text))
            .Select(entry => entry.StableId)
            .ToArray();
        var bridge = PdfReviewedOccurrenceBridge.Load(File.ReadAllText(bridgePath));

        var report = PdfFirstLossAudit.Evaluate(docx, slim, rawKey.ResolveStableIds(stableMap), 160, bridge, goldStableIds);

        // Every reviewed heading has some candidate carrying all of its lines; the question is rank.
        Assert.All(report.Entries, entry => Assert.Equal("full", entry.CandidateCoverage));

        var split = report.Entries.Single(entry => entry.Gold.StartsWith("SECTION XIV", StringComparison.Ordinal));
        Assert.Equal("partial", split.SelectedCoverage);
        Assert.NotNull(split.BestPartialCoverageRank);
        Assert.True(split.BestPartialCoverageRank <= 160,
            "the truncated first line of the heading is inside the budget");
        Assert.True(split.OccurrenceRank > 160,
            "the candidate carrying the whole heading is not");
        // The gate that stopped the complete occurrence is still ranking; partial coverage describes
        // what got through, and does not rename where the loss happened.
        Assert.Equal("ranking_or_budget", split.FirstLoss);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
