using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A3: corpus-wide, model-free semantic-benchmark population screening. For every document in the
/// 95-document corpus, runs the same candidate construction, rank@160 selection, and deterministic
/// eligibility check A2c already established - reused via
/// <see cref="PdfExtractorQualityBenchmarkProbe.DeterministicExclusionReason"/> - over the FULL
/// candidate pool, not just previously-reviewed gold occurrences (no gold exists yet for 91 of these
/// 95 documents; that is exactly the point - this stage answers "is this document worth reviewing"
/// before any review happens).
/// <para>
/// Selection rule for which documents are worth a semantic live run is stated here, before this probe
/// is ever run, not chosen after seeing which documents look good: selected@160 &gt;= 20 and
/// decisionRelevant &gt;= 15 (see <see cref="MinimumSelected"/>/<see cref="MinimumDecisionRelevant"/>).
/// No model call anywhere in this file.
/// </para>
/// </summary>
public sealed class PdfA3PopulationScreeningProbe
{
    private const int SelectedBudget = 160;
    private const int MinimumSelected = 20;
    private const int MinimumDecisionRelevant = 15;

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_A3_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpusRoot = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("A3 - corpus-wide model-free population screening. No gold, no model call.");
        Line("Frozen rule (stated before results): usable = selected@160 >= " + MinimumSelected +
             " and decisionRelevant >= " + MinimumDecisionRelevant + ".");
        Line("");
        Line($"{"domain",-24} {"document",-55} {"candidates",10} {"selected",9} {"answerIrrelevant",16} {"decisionRelevant",16} {"ratio",6} {"usable",7}");

        var usable = new List<(string Domain, string Document, int DecisionRelevant, double Ratio)>();
        var failures = new List<string>();

        foreach (var docxPath in Directory.EnumerateFiles(corpusRoot, "*.docx", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var domain = Path.GetFileName(Path.GetDirectoryName(docxPath)) ?? "?";
            var name = Path.GetFileNameWithoutExtension(docxPath);
            try
            {
                var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
                var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
                var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
                var selected = ranked.Take(SelectedBudget).ToArray();

                var answerIrrelevant = 0;
                foreach (var candidate in selected)
                    if (contexts.TryGetValue(candidate.SourceId, out var ctx) &&
                        PdfExtractorQualityBenchmarkProbe.DeterministicExclusionReason(ctx) is not null)
                        answerIrrelevant++;

                var decisionRelevant = selected.Length - answerIrrelevant;
                var ratio = selected.Length == 0 ? 0.0 : decisionRelevant / (double)selected.Length;
                var isUsable = selected.Length >= MinimumSelected && decisionRelevant >= MinimumDecisionRelevant;

                Line($"{domain,-24} {Trim(name, 55),-55} {snapshot.CandidateBlocks.Count,10} {selected.Length,9} " +
                     $"{answerIrrelevant,16} {decisionRelevant,16} {ratio,6:P0} {(isUsable ? "YES" : "-"),7}");

                if (isUsable) usable.Add((domain, name, decisionRelevant, ratio));
            }
            catch (Exception ex)
            {
                failures.Add($"{domain}/{name}: {ex.GetType().Name}: {ex.Message}");
                Line($"{domain,-24} {Trim(name, 55),-55} FAILED: {ex.GetType().Name}");
            }
        }

        Line("");
        Line($"usable documents (selected@160 >= {MinimumSelected}, decisionRelevant >= {MinimumDecisionRelevant}): {usable.Count}");
        Line("");
        Line("by domain family:");
        foreach (var group in usable.GroupBy(u => u.Domain).OrderBy(g => g.Key, StringComparer.Ordinal))
            Line($"  {group.Key,-24} {group.Count()}");
        Line("");
        Line("usable documents, deterministic order (domain, then decisionRelevant descending, then name):");
        foreach (var (domain, document, decisionRelevant, ratio) in usable
                     .OrderBy(u => u.Domain, StringComparer.Ordinal)
                     .ThenByDescending(u => u.DecisionRelevant)
                     .ThenBy(u => u.Document, StringComparer.Ordinal))
            Line($"  {domain,-24} {document,-55} decisionRelevant={decisionRelevant} ratio={ratio:P0}");

        if (failures.Count > 0)
        {
            Line("");
            Line($"{failures.Count} document(s) failed to process (excluded from screening, not silently zero):");
            foreach (var failure in failures) Line($"  {failure}");
        }

        File.WriteAllText(output, report.ToString());
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 3)] + "...";
}
