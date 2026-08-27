using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.7-B2. One hypothesis: should the strict structural-marker fact count as strong marker evidence
/// the way a labelled marker already does?
/// <para>
/// The counterfactual admits the existing fact to the existing strong-marker path. No weight is
/// tuned, no signal is invented, the parser is not repaired and the known marker-depth damage on 032
/// is deliberately left alone - fixing that in the same run would destroy the isolation. The answer
/// is pass or fail, not a search for a number.
/// </para>
/// <para>
/// 054 is a negative control rather than a beneficiary: its heading has no structural marker at all,
/// so if its score moves, the intervention is reaching further than the hypothesis. 092 is the
/// collateral stress case, because the same fact holds for all 35 of its reviewed contents entries.
/// </para>
/// </summary>
public sealed class PdfMarkerEquivalenceProbe
{
    private const int SelectedBudget = 160;

    private static readonly (string Stem, string Relative)[] Documents =
    [
        ("032", @"02_hop_dong_mua_sam\032_WB_Plant_TwoStage_2020.docx"),
        ("091", @"07_system_generated\091_RFC9110_HTTP_Semantics.docx"),
        ("092", @"07_system_generated\092_RFC9111_HTTP_Caching.docx"),
        ("054", @"03_tai_chinh_ke_toan\054_IBRD_Information_Statement_FY25.docx"),
    ];

    private static readonly Dictionary<string, string[]> Watched = new(StringComparer.Ordinal)
    {
        ["032"] = ["b2912", "b2437", "b2919", "b4484"],
        ["091"] = ["b229", "b294", "b928", "b1367", "b2346"],
        ["054"] = ["b7"],
    };

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_7_B2_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("hypothesis: strict structural marker qualifies for the existing strong-marker path");
        Line("intervention=existing_fact_to_existing_path no_weight_tuning no_parser_repair no_depth_fix");

        var labels = Labels092();

        foreach (var (stem, relative) in Documents)
        {
            var path = Path.Combine(corpus, relative);
            Line("");
            Line($"================ {stem} ================");
            if (!File.Exists(path)) { Line("document not found"); continue; }

            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
            var before = new World(snapshot, contexts, false);
            var after = new World(snapshot, contexts, true);

            Line($"{"measure",-38} {"before",8} {"after",8}");
            Line($"{"candidates carrying strong marker",-38} {before.StrongMarker,8} {after.StrongMarker,8}");
            Line($"{"score at rank 160",-38} {before.EdgeScore,8:F2} {after.EdgeScore,8:F2}");
            Line($"{"emittable at budget",-38} {before.Emittable,8} {after.Emittable,8}");
            Line($"top-160 churn: {after.Selected.Except(before.Selected, StringComparer.Ordinal).Count()} in, " +
                 $"{before.Selected.Except(after.Selected, StringComparer.Ordinal).Count()} out");

            if (Watched.TryGetValue(stem, out var watched))
            {
                Line("");
                Line($"  {"watched occurrence",-36} {"score",12} {"rank",14} {"selected"}");
                foreach (var id in watched)
                    Line($"  {Trim(before.Text(id), 36),-36} " +
                         $"{before.Score(id),5:F2} -> {after.Score(id),4:F2} " +
                         $"{before.Rank(id),6} -> {after.Rank(id),-6} " +
                         $"{before.Selected.Contains(id)} -> {after.Selected.Contains(id)}");
            }

            if (stem == "054")
            {
                var moved = before.Score("b7") != after.Score("b7") || before.Rank("b7") != after.Rank("b7");
                Line("");
                Line($"  NEGATIVE CONTROL: AVAILABILITY OF INFORMATION own score/rank moved = {moved}");
                Line("  (its own facts must not change; the document's ranking may still shift around it)");
            }

            if (stem == "092" && labels.Count > 0)
            {
                Line("");
                Line("  collateral by reviewed role (092)");
                Line($"  {"role",-30} {"n",4} {"selected before",16} {"selected after",15}");
                foreach (var group in labels.GroupBy(l => l.Role).OrderByDescending(g => g.Count()))
                {
                    var keys = group.Select(l => Key(l.Page, l.Readable)).ToArray();
                    Line($"  {group.Key,-30} {group.Count(),4} " +
                         $"{keys.Count(before.IsSelectedLine),16} {keys.Count(after.IsSelectedLine),15}");
                }
            }
        }

        File.WriteAllText(output, report.ToString());
    }

    private sealed class World
    {
        private readonly Dictionary<string, RankedCandidate> _ranked;
        private readonly Dictionary<string, int> _rank;
        private readonly Dictionary<string, PdfSemanticBlock> _blocks;
        private readonly Dictionary<string, List<string>> _byLineKey = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, PdfCandidateContext> _contexts;

        public World(PdfCandidateRankingSnapshot snapshot,
            IReadOnlyDictionary<string, PdfCandidateContext> contexts, bool structuralCountsAsStrong)
        {
            _contexts = contexts;
            var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts, structuralCountsAsStrong);
            _ranked = ranked.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
            _rank = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
            _blocks = snapshot.CandidateBlocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
            foreach (var block in snapshot.CandidateBlocks)
                foreach (var line in block.Lines)
                {
                    var key = Key(line.Page, PdfTextUtilities.Readable(line.Text));
                    if (!_byLineKey.TryGetValue(key, out var list)) _byLineKey[key] = list = [];
                    list.Add(block.Id);
                }
            StrongMarker = ranked.Count(item => item.PositiveSignals.Contains("labelled_numbering_marker"));
            Selected = ranked.Take(SelectedBudget).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
            EdgeScore = ranked.Count >= SelectedBudget ? ranked[SelectedBudget - 1].CandidateScore : 0;
            Emittable = Selected.Count(id =>
                Array.IndexOf(PdfOutputDecisionPolicy.ExcludedScopes,
                    contexts.TryGetValue(id, out var c) ? c.Source.StructuralScope : "-") < 0);
        }

        public int StrongMarker { get; }
        public HashSet<string> Selected { get; }
        public double EdgeScore { get; }
        public int Emittable { get; }

        public double Score(string id) => _ranked.TryGetValue(id, out var item) ? item.CandidateScore : double.NaN;
        public int Rank(string id) => _rank.GetValueOrDefault(id, -1);
        public string Text(string id) => _blocks.TryGetValue(id, out var b) ? Trim(b.DisplayText, 36) : id;

        public bool IsSelectedLine(string lineKey) =>
            _byLineKey.TryGetValue(lineKey, out var list) &&
            list.OrderBy(id => _rank.GetValueOrDefault(id, int.MaxValue)).Take(1).Any(Selected.Contains);
    }

    private static List<(int Page, string Readable, string Role)> Labels092()
    {
        var path = Path.Combine(RepositoryRoot(), "eval", "manual-labels",
            "092-short-numbered-line-labels.v1.json");
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (
                item.GetProperty("page").GetInt32(),
                item.GetProperty("readable").GetString() ?? "",
                item.GetProperty("role").GetString() ?? ""))
            .ToList();
    }

    private static string Key(int page, string readable) =>
        $"{page}|{Regex.Replace(readable, @"\s+", " ").Trim()}";

    private static string Trim(string value, int max = 70)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
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
