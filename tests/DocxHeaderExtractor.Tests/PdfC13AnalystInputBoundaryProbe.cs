using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// C1.3 preflight - which text the analyst actually receives for the block it is judging.
/// <para>
/// C1.2 established that the glue lives in <c>DisplayText</c>, i.e. in <c>HeadingReadable</c>'s
/// fragment repair. The counterfactual that would test causality only makes sense if that repaired
/// text is what the analyst sees. <c>PdfBlockAnalyst</c> sends <c>PromptSourceText(block.Text)</c>,
/// which is a length truncation and nothing else, while <c>DisplayText</c> reaches the prompt only
/// through the neighbouring-context excerpts. This measures the difference rather than trusting the
/// reading.
/// </para>
/// </summary>
public sealed class PdfC13AnalystInputBoundaryProbe
{
    [Fact]
    public void Report()
    {
        var stem = Environment.GetEnvironmentVariable("C13_DOC");
        var output = Environment.GetEnvironmentVariable("C13_REPORT");
        if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(),
            "todo10_8", "heading_corpus_95_word");
        var population = PdfExtractorQualityBenchmarkProbe.Populations(corpus).FirstOrDefault(p => p.Stem == stem);
        if (population.Occurrences is null || population.Occurrences.Count == 0)
        {
            File.WriteAllText(output, $"doc={stem}: no reviewed population");
            return;
        }

        var docx = Path.Combine(corpus, population.Relative);
        var classifications = PdfExtractorQualityBenchmarkProbe.Classify(docx, population.Occurrences);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var blocks = snapshot.CandidateBlocks.ToDictionary(b => b.Id, StringComparer.Ordinal);

        var cohort = classifications
            .Where(c => c.Selected && c.CoveringCandidateId is not null && c.DeterministicExclusionReason is null)
            .Select(c => blocks.GetValueOrDefault(c.CoveringCandidateId!))
            .Where(b => b is not null)
            .ToArray();

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"C1.3 preflight - doc={stem}. Which representation reaches the analyst?");
        Line("PdfBlockAnalyst sends PromptSourceText(block.Text); DisplayText reaches the prompt only");
        Line("through neighbouring-context excerpts (PreviousBlocks / NextBlocks / ActiveHeadingStack).");
        Line($"cohort blocks: {cohort.Length}");

        var rawGlued = cohort.Count(b => LongestToken(b!.Text) > 12);
        var displayGlued = cohort.Count(b => LongestToken(b!.DisplayText) > 12);
        var differ = cohort.Count(b => !string.Equals(
            PdfTextUtilities.Readable(b!.Text), b.DisplayText, StringComparison.Ordinal));

        Line("");
        Line($"   block.Text   (what the analyst is sent)   glued>12 : {rawGlued,5} / {cohort.Length}");
        Line($"   DisplayText  (context excerpts only)      glued>12 : {displayGlued,5} / {cohort.Length}");
        Line($"   Text and DisplayText differ                        : {differ,5} / {cohort.Length}");

        Line("");
        Line("-- samples: analyst-sent text vs repaired display text");
        foreach (var block in cohort.Take(10))
        {
            Line($"   sent    : {Trim(PdfTextUtilities.Readable(block!.Text))}");
            Line($"   display : {Trim(block.DisplayText)}");
        }

        File.WriteAllText(output, report.ToString());
    }

    private static int LongestToken(string text)
    {
        var tokens = PdfTextUtilities.Readable(text)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? 0 : tokens.Max(t => t.Trim('.', ',', ':', ';', ')', '(').Length);
    }

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 72 ? single : single[..72] + "...";
    }
}
