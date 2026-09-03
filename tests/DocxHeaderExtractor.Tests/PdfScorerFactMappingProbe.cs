using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.7-B1. For each reviewed heading whose only remaining blocker is the budget, reports which
/// marker facts the pipeline actually produced and which of them the scorer credited.
/// <para>
/// It reports facts and signals as observed and computes no score of its own. Restating the scorer's
/// arithmetic here to show a decomposition would be a second implementation of the thing under audit,
/// and the question does not need one: whether a fact reached the scorer is answered by whether its
/// signal appears.
/// </para>
/// <para>
/// The 032 headings are the denominator - four occurrences whose scope blocker was independently
/// removed in M10.6-A. 054's `AVAILABILITY OF INFORMATION` is a comparison and is kept out of that
/// count, because a case that shares a symptom need not share a mechanism.
/// </para>
/// </summary>
public sealed class PdfScorerFactMappingProbe
{
    private const int SelectedBudget = 160;

    private static readonly (string Stem, string Relative, string[] Needles, bool Denominator)[] Cases =
    [
        ("032", @"02_hop_dong_mua_sam\032_WB_Plant_TwoStage_2020.docx",
            // Addressed by block id: the reviewed text and the extracted text differ in spacing, and
            // matching on text would silently drop occurrences from the denominator.
            ["b2912", "b2437", "b2919", "b4484"],
            true),
        // 091 is checked separately rather than assumed to share 032's mechanism: a family
        // resemblance is not a traced fact, and the name of an owner must not run ahead of evidence.
        ("091", @"07_system_generated\091_RFC9110_HTTP_Semantics.docx",
            ["b229", "b294", "b928", "b1367", "b2346"], false),
        ("054", @"03_tai_chinh_ke_toan\054_IBRD_Information_Statement_FY25.docx",
            ["b7"], false),
    ];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_7_B1_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("contract=fact_and_signal_observation_only no_score_recomputation no_weight_change");

        foreach (var (stem, relative, needles, denominator) in Cases)
        {
            var path = Path.Combine(corpus, relative);
            Line("");
            Line($"================ {stem} {(denominator ? "(denominator)" : "(comparison only)")} ================");
            if (!File.Exists(path)) { Line("document not found"); continue; }

            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
            var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
            var rankOf = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);

            foreach (var needle in needles)
            {
                var block = snapshot.CandidateBlocks.FirstOrDefault(b => b.Id == needle);
                if (block is null) { Line($"  {needle}: no candidate"); continue; }
                var candidate = ranked.FirstOrDefault(item => item.SourceId == block.Id);
                if (candidate is null) { Line($"  {needle}: not ranked"); continue; }

                var text = block.DisplayText;
                var strict = PdfMarkerFactsParser.Parse(PdfTextUtilities.Readable(text));
                var loose = PdfLayoutEvidenceOutline.ParseLooseLabelledMarkerForAudit(text);
                var generic = NumberingAudit.Parse(text) is not null;

                Line("");
                Line($"  {Trim(block.DisplayText, 50)}  [{block.Id}] p{block.Page}");
                Line($"    facts produced:");
                Line($"      PdfMarkerFactsParser (strict structural) = " +
                     $"{(strict is null ? "none" : $"{strict.Value.Signature} family={strict.Value.Family} depth={strict.Value.Depth} isPath={strict.Value.IsPath}")}");
                Line($"      HasStructuralMarker                      = {PdfLineBlockAnnotation.HasStructuralMarker(text)}");
                Line($"      ParseLooseLabelledMarkerForAudit         = {loose ?? "none"}");
                Line($"      NumberingAudit (generic)                 = {generic}");
                Line($"    signals credited by the scorer:");
                Line($"      positive  = [{string.Join(",", candidate.PositiveSignals)}]");
                Line($"      negative  = [{string.Join(",", candidate.NegativeSignals)}]");
                Line($"      ambiguity = [{string.Join(",", candidate.AmbiguitySignals)}]");
                Line($"    score={candidate.CandidateScore:F2} rank={rankOf.GetValueOrDefault(block.Id, -1)} " +
                     $"lines={block.LineCount} scope={candidate.Scope}");
            }

            // What the candidates that do clear the budget actually carry, so "outscored by hundreds"
            // can be checked against which facts those hundreds have.
            Line("");
            Line("  what the selected population carries (top 160)");
            var selected = ranked.Take(SelectedBudget).ToArray();
            foreach (var signal in selected.SelectMany(item => item.PositiveSignals).Distinct().OrderBy(x => x))
                Line($"    positive {signal,-30} {selected.Count(item => item.PositiveSignals.Contains(signal))}");
            Line($"    score at the budget edge: rank1={selected.First().CandidateScore:F2} " +
                 $"rank160={selected.Last().CandidateScore:F2}");
        }

        File.WriteAllText(output, report.ToString());
    }

    private static string Normalise(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static string Trim(string value, int max = 70)
    {
        var single = Normalise(value);
        return single.Length <= max ? single : single[..max] + "...";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
