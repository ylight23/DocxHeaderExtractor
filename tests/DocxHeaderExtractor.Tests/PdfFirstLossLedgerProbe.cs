using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Lane 3: one row per reviewed occurrence (all 74, across 054/092/032/091), each stamped with the
/// single pipeline stage that first cost it - CANDIDATE_CONSTRUCTION, RANKING_BUDGET,
/// DETERMINISTIC_ELIGIBILITY, SEMANTIC_ANALYSIS, VALIDATOR, or SURVIVED. Every field is read from a
/// stage this project already computes (<see cref="PdfExtractorQualityBenchmarkProbe.Classify"/> for
/// candidate/rank/selection/deterministic-exclusion, the frozen A2 artifact for validated status) -
/// no new implementation of eligibility or ranking, no model call, no new occurrence gold.
/// <para>
/// VALIDATED is only checkable for occurrences that reach decision-relevant, and only where a live A2
/// artifact exists (054). 092/032/091's decision-relevant cohort is empty (A2c), so nothing there needs
/// an artifact to resolve - "not validated" already follows from never becoming decision-relevant.
/// </para>
/// </summary>
public sealed class PdfFirstLossLedgerProbe
{
    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_LEDGER_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var validatedLineIdSets = LoadValidatedLineIdSets(Environment.GetEnvironmentVariable("BENCH_A2_ARTIFACT_054"));

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("document\tcandidateExists\tbestRank\tselected\tdeterministicExclusion\texclusionReason\tdecisionRelevant\tvalidated\tFIRST_LOSS\tlabel");

        var tally = new Dictionary<string, int>
        {
            ["CANDIDATE_CONSTRUCTION"] = 0,
            ["RANKING_BUDGET"] = 0,
            ["DETERMINISTIC_ELIGIBILITY"] = 0,
            ["SEMANTIC_ANALYSIS"] = 0,
            ["VALIDATOR"] = 0,
            ["SURVIVED"] = 0,
        };

        foreach (var (stem, relative, occurrences) in PdfExtractorQualityBenchmarkProbe.Populations(corpus))
        {
            var path = Path.Combine(corpus, relative);
            if (!File.Exists(path) || occurrences.Count == 0) continue;

            var classifications = PdfExtractorQualityBenchmarkProbe.Classify(path, occurrences);
            foreach (var occ in classifications)
            {
                var candidateExists = occ.Status == "full";
                string firstLoss;
                bool? validated = null;

                if (!candidateExists)
                {
                    firstLoss = "CANDIDATE_CONSTRUCTION";
                }
                else if (!occ.Selected)
                {
                    firstLoss = "RANKING_BUDGET";
                }
                else if (occ.DeterministicExclusionReason is not null)
                {
                    firstLoss = "DETERMINISTIC_ELIGIBILITY";
                }
                else
                {
                    // Decision-relevant. Resolve VALIDATOR/SURVIVED only where a live artifact exists;
                    // otherwise this occurrence is a genuine benchmark gap, not a loss - reported as such.
                    if (validatedLineIdSets.TryGetValue(stem, out var validatedSets))
                    {
                        validated = validatedSets.Any(set => occ.RequiredLineIds.All(set.Contains));
                        firstLoss = validated == true ? "SURVIVED" : "VALIDATOR";
                    }
                    else
                    {
                        firstLoss = "NOT_MEASURED_NO_LIVE_ARTIFACT";
                    }
                }

                tally.TryAdd(firstLoss, 0);
                tally[firstLoss]++;

                Line($"{stem}\t{candidateExists}\t{occ.CoveringRank?.ToString() ?? "-"}\t{occ.Selected}\t" +
                     $"{occ.DeterministicExclusionReason is not null}\t{occ.DeterministicExclusionReason ?? "-"}\t" +
                     $"{candidateExists && occ.Selected && occ.DeterministicExclusionReason is null}\t" +
                     $"{(validated is null ? "-" : validated.ToString())}\t{firstLoss}\t{Trim(occ.Occurrence.Label)}");
            }
        }

        Line("");
        Line("FIRST_LOSS reconciliation:");
        var total = 0;
        foreach (var (category, count) in tally.OrderByDescending(kv => kv.Value))
        {
            if (count == 0) continue;
            Line($"  {category,-32} {count}");
            total += count;
        }
        Line($"  {"TOTAL",-32} {total}");

        File.WriteAllText(output, report.ToString());
    }

    /// <summary>
    /// Loads the validated items' lineId sets from a frozen A2 artifact, keyed by the document stem
    /// inferred from the artifact's own row file name. Same join primitive as A2b/A2c - superset of
    /// required lineIds, never text.
    /// </summary>
    private static Dictionary<string, List<HashSet<string>>> LoadValidatedLineIdSets(string? artifactPath)
    {
        var result = new Dictionary<string, List<HashSet<string>>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath)) return result;

        using var document = JsonDocument.Parse(File.ReadAllText(artifactPath));
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var file = row.GetProperty("file").GetString() ?? "";
            var stem = file.Length >= 3 ? file[..3] : file;
            var sets = row.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("lineIds").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal))
                .ToList();
            result[stem] = sets;
        }
        return result;
    }

    private static string Trim(string value)
    {
        var single = value.Length <= 60 ? value : value[..60] + "...";
        return single.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
    }
}
