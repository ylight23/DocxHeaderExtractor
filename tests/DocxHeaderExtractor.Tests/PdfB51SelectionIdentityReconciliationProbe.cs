using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// B5.1 is a model-free identity reconciliation. It joins B3's frozen TRUE outputs to the exact
/// occurrence line ids used by B2's existing candidate/rank/selection census; it never joins text.
/// </summary>
public sealed class PdfB51SelectionIdentityReconciliationProbe
{
    [Fact]
    public void Report()
    {
        var artifactPath = Environment.GetEnvironmentVariable("BENCH_B51_ARTIFACT");
        var reviewPath = Environment.GetEnvironmentVariable("BENCH_B51_REVIEW");
        var output = Environment.GetEnvironmentVariable("BENCH_B51_REPORT");
        if (string.IsNullOrWhiteSpace(artifactPath) || string.IsNullOrWhiteSpace(reviewPath) || string.IsNullOrWhiteSpace(output)) return;

        using var artifact = JsonDocument.Parse(File.ReadAllText(artifactPath));
        using var review = JsonDocument.Parse(File.ReadAllText(reviewPath));
        var stem = Path.GetFileName(artifactPath)[..3];
        var corpus = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var population = PdfExtractorQualityBenchmarkProbe.Populations(corpus).Single(item => item.Stem == stem);
        var classifications = PdfExtractorQualityBenchmarkProbe.Classify(Path.Combine(corpus, population.Relative), population.Occurrences);
        var decisions = review.RootElement.GetProperty("documents").GetProperty(stem).GetProperty("decisions");
        var trueItems = artifact.RootElement.GetProperty("rows")[0].GetProperty("items").EnumerateArray()
            .Where(item => decisions.GetProperty(item.GetProperty("sourceFactId").GetString()!).GetString() == "TRUE_HEADING").ToArray();

        var report = new StringBuilder();
        report.AppendLine($"B5.1 selection identity reconciliation: doc={stem}; B3 TRUE={trueItems.Length}; B2 occurrences={classifications.Count}");
        var selected = 0;
        var notSelected = 0;
        var outsideGold = 0;
        foreach (var item in trueItems)
        {
            var id = item.GetProperty("sourceFactId").GetString()!;
            var lineIds = item.GetProperty("lineIds").EnumerateArray().Select(line => line.GetString()!).ToHashSet(StringComparer.Ordinal);
            var matches = classifications.Where(c => c.RequiredLineIds.All(lineIds.Contains)).ToArray();
            if (matches.Length == 0)
            {
                outsideGold++;
                report.AppendLine($"{id}: OUTSIDE_GOLD_SCOPE page={item.GetProperty("page").GetInt32()}");
                continue;
            }
            foreach (var match in matches)
            {
                if (match.Selected) selected++; else notSelected++;
                report.AppendLine($"{id}: gold={match.Occurrence.Label}; status={match.Status}; candidate={match.CoveringCandidateId}; rank={match.CoveringRank}; selected={match.Selected}; eligibility={match.DeterministicExclusionReason ?? "decision-relevant"}");
            }
        }
        report.AppendLine($"summary selectedMatches={selected} notSelectedMatches={notSelected} outsideGoldScope={outsideGold}");
        File.WriteAllText(output, report.ToString());
    }
}
