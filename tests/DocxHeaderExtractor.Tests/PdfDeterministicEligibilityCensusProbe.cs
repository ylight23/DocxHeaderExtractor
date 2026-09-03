using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A2c: model-free eligibility census over all four reviewed populations. Adds one stage the earlier
/// benchmark passes did not separate - deterministic answer-relevance - between selection and the
/// semantic lane:
/// <para>
/// SOURCE -> CANDIDATE COVERAGE -> RANK/SELECTION -> DETERMINISTIC PRE-ANSWER ELIGIBILITY ->
/// ANALYST EXPOSED -> ANALYST DECISION -> VALIDATOR -> VALIDATED STRUCTURE -> OUTPUT ELIGIBILITY
/// </para>
/// <para>
/// A2b found 092's entire 35-occurrence selected@160 cohort deterministically answer-irrelevant: every
/// one was dispatched to the analyst (selection has no domain filter - the analyst genuinely ran), but
/// <c>PdfProposalValidator.Validate</c>'s domain-role/structural-scope gate discards it afterward
/// regardless of the answer, so "analyst-exposed" and "analyst-decision-relevant" are different counts.
/// This probe answers the same question for 032 and 091's selected occurrences before any decision is
/// made about whether either is worth a live semantic run - no model call anywhere in this file.
/// </para>
/// </summary>
public sealed class PdfDeterministicEligibilityCensusProbe
{
    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_A2C_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("A2c - model-free deterministic eligibility census. No model call in this probe.");
        Line("");
        Line($"{"doc",-6} {"reviewed",9} {"full",6} {"selected@160",13} {"answer-irrelevant",18} {"decision-relevant",18}");

        var totals = new int[5];
        foreach (var (stem, relative, occurrences) in PdfExtractorQualityBenchmarkProbe.Populations(corpus))
        {
            var path = Path.Combine(corpus, relative);
            if (!File.Exists(path) || occurrences.Count == 0)
            {
                Line($"{stem,-6} not measured");
                continue;
            }

            var classifications = PdfExtractorQualityBenchmarkProbe.Classify(path, occurrences);
            var reviewed = classifications.Count;
            var full = classifications.Count(c => c.Status == "full");
            var selected = classifications.Where(c => c.Selected).ToList();
            var answerIrrelevant = selected.Where(c => c.DeterministicExclusionReason is not null).ToList();
            var decisionRelevant = selected.Count - answerIrrelevant.Count;

            totals[0] += reviewed;
            totals[1] += full;
            totals[2] += selected.Count;
            totals[3] += answerIrrelevant.Count;
            totals[4] += decisionRelevant;

            Line($"{stem,-6} {reviewed,9} {full,6} {selected.Count,13} {answerIrrelevant.Count,18} {decisionRelevant,18}");
            foreach (var occ in selected)
            {
                var tag = occ.DeterministicExclusionReason is { } reason ? $"ANSWER-IRRELEVANT ({reason})" : "decision-relevant";
                Line($"    candidate={occ.CoveringCandidateId} rank={occ.CoveringRank} {tag}: {Trim(occ.Occurrence.Label)}");
            }
        }

        Line("");
        Line($"{"ALL",-6} {totals[0],9} {totals[1],6} {totals[2],13} {totals[3],18} {totals[4],18}");
        Line("");
        Line("decision-relevant = selected@160 minus deterministically answer-irrelevant. This is the");
        Line("correct denominator for a semantic live run - not selected@160, which 092's A2b showed can");
        Line("include occurrences whose analyst answer was already outcome-irrelevant before it was given.");

        File.WriteAllText(output, report.ToString());
    }

    private static string Trim(string value)
    {
        var single = value.Length <= 70 ? value : value[..70] + "...";
        return single.Replace('\n', ' ').Replace('\r', ' ');
    }
}
