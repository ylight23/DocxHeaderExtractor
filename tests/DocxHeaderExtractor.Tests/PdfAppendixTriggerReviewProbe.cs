using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.6-A review aid. Lists every block that fires the appendix trigger, with its page and incoming
/// scope, so the false transitions can be separated from the genuine appendix openings before any of
/// them is withheld. Withholding all of them would remove the real appendices too and measure a
/// different question.
/// </summary>
public sealed class PdfAppendixTriggerReviewProbe
{
    private static readonly (string Stem, string Relative)[] Documents =
    [
        ("032", @"02_hop_dong_mua_sam\032_WB_Plant_TwoStage_2020.docx"),
        ("091", @"07_system_generated\091_RFC9110_HTTP_Semantics.docx"),
    ];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_6_TRIGGERS");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();

        foreach (var (stem, relative) in Documents)
        {
            var path = Path.Combine(corpus, relative);
            report.AppendLine($"================ {stem} ================");
            if (!File.Exists(path)) { report.AppendLine("not found"); continue; }

            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var trace = new List<StructuralScopeTransition>();
            PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations, scopeTrace: trace);

            var triggers = trace.Where(t => t.AppendixTriggeredHere).ToArray();
            report.AppendLine($"appendix triggers: {triggers.Length}");
            foreach (var group in triggers.GroupBy(t => t.Page).OrderBy(g => g.Key))
            {
                report.AppendLine($"  page {group.Key} ({group.Count()} blocks)");
                foreach (var entry in group)
                    report.AppendLine($"    {entry.SourceId,-16} incoming={entry.IncomingScope,-18} " +
                                      $"{Trim(entry.RawText)}");
            }
            report.AppendLine();
        }

        File.WriteAllText(output, report.ToString());
    }

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 96 ? single : single[..96] + "...";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
