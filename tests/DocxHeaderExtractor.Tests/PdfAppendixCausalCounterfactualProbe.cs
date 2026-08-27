using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.6-A. Withholds only the reviewed false appendix transitions and lets every existing consumer
/// run unchanged. Ranks are allowed to move as a consequence of scope changing; that is the effect
/// being measured, not a second intervention.
/// <para>
/// The genuine appendix openings are kept. Withholding all triggers would remove the document's real
/// appendices too and would answer a different question - whether appendix scope matters at all -
/// rather than whether these particular transitions are false.
/// </para>
/// <para>
/// What matters more than the recovery count is where the surviving losses move to. Ten of the
/// fourteen reviewed headings are blocked by scope *and* by the budget, so a repair to one of them
/// recovers none of those ten on its own; the point of a single-variable run is to find out what
/// blocks them once the scope is right, and whether correcting scope introduces a blocker that was
/// not there before.
/// </para>
/// </summary>
public sealed class PdfAppendixCausalCounterfactualProbe
{
    private const int SelectedBudget = 160;

    // Reviewed false transitions, listed as occurrences rather than summarised by a rule. 032's fall
    // into three shapes: contents listings with dot leaders (pages 121 and 254), the same listings
    // merged into single blocks (pages 133 and 265), and body prose referring to an appendix
    // (pages 166, 209, 219, 269). Every genuine annex or appendix opening is left alone.
    private static readonly string[] False032 =
    [
        "s-block-28", "s-line-3310", "s-window-8174", "s-line-3311", "s-window-8177",
        "s-line-3312", "s-window-8180", "s-line-3313", "s-window-8183", "s-line-3314",
        "s-window-8186", "s-line-3315", "s-window-8189", "s-line-3316", "s-window-8192",
        "b2099", "b2100",
        "b2590", "b3329", "b3510",
        "s-block-51", "s-line-7733", "s-window-19829", "s-line-7734", "s-window-19832",
        "s-line-7735", "s-window-19835", "s-line-7736", "s-window-19838", "b4137",
        "s-window-19841", "s-block-52", "s-line-7739", "s-window-19847", "s-line-7740",
        "s-window-19850", "s-line-7741", "s-window-19853",
        "b4345", "b4348", "b4388",
    ];

    // 091: the contents block on page 12, and body prose on page 35 reading "Appendix A shows the
    // collected ABNF...". The real appendices on pages 174 and 178 keep their triggers.
    private static readonly string[] False091 = ["b113", "b443"];

    private static readonly (string Stem, string Relative, string[] Withheld)[] Documents =
    [
        ("032", @"02_hop_dong_mua_sam\032_WB_Plant_TwoStage_2020.docx", False032),
        ("091", @"07_system_generated\091_RFC9110_HTTP_Semantics.docx", False091),
    ];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_6_A_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var reviewed = ReviewedHeadings();
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("intervention=withhold_reviewed_false_appendix_transitions_only");
        Line("unchanged: TableLike, scorer, comparator, budget 160, output policy, TOC detector, quote logic");

        foreach (var (stem, relative, withheld) in Documents)
        {
            var path = Path.Combine(corpus, relative);
            Line("");
            Line($"================ {stem} ================");
            if (!File.Exists(path)) { Line("document not found"); continue; }

            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var before = new World(snapshot, null);
            var after = new World(snapshot, new HashSet<string>(withheld, StringComparer.Ordinal));

            Line($"withheld transitions: {withheld.Length}");
            Line($"{"measure",-34} {"before",8} {"after",8}");
            Line($"{"scope appendix",-34} {before.Scopes.GetValueOrDefault("appendix"),8} {after.Scopes.GetValueOrDefault("appendix"),8}");
            Line($"{"scope appendix_table",-34} {before.Scopes.GetValueOrDefault("appendix_table"),8} {after.Scopes.GetValueOrDefault("appendix_table"),8}");
            Line($"{"scope document_body",-34} {before.Scopes.GetValueOrDefault("document_body"),8} {after.Scopes.GetValueOrDefault("document_body"),8}");
            Line($"{"scope table",-34} {before.Scopes.GetValueOrDefault("table"),8} {after.Scopes.GetValueOrDefault("table"),8}");
            Line($"{"blocks with table_scope penalty",-34} {before.Penalised,8} {after.Penalised,8}");
            Line($"{"emittable at budget",-34} {before.Emittable,8} {after.Emittable,8}");

            Line("");
            Line("  first appendix transition that survives:");
            var survivor = after.Trace.FirstOrDefault(t => t.AppendixTriggeredHere);
            Line(survivor is null
                ? "    none - the latch never turns on"
                : $"    p{survivor.Page} {survivor.SourceId} {Trim(survivor.RawText, 80)}");

            if (!reviewed.TryGetValue(stem, out var headings)) continue;

            Line("");
            Line("  reviewed headings, and where the loss moves to");
            Line($"  {"heading",-34} {"scope before",-16} {"scope after",-16} {"rank",6} {"->",4} {"rank",6} {"outcome"}");
            var outcomes = new List<string>();
            foreach (var (page, readable) in headings)
            {
                var key = Key(page, readable);
                var outcome = after.Outcome(key);
                outcomes.Add(outcome);
                Line($"  {Trim(readable, 34),-34} {before.Scope(key),-16} {after.Scope(key),-16} " +
                     $"{before.Rank(key),6} {"->",4} {after.Rank(key),6} {outcome}");
            }
            Line("");
            foreach (var group in outcomes.GroupBy(x => x).OrderByDescending(g => g.Count()))
                Line($"    {group.Key,-40} {group.Count()}");
        }

        File.WriteAllText(output, report.ToString());
    }

    private sealed class World
    {
        private readonly IReadOnlyDictionary<string, PdfCandidateContext> _contexts;
        private readonly Dictionary<string, int> _rank;
        private readonly Dictionary<string, RankedCandidate> _ranked;
        private readonly Dictionary<string, List<string>> _blocksByLineKey = new(StringComparer.Ordinal);

        public World(PdfCandidateRankingSnapshot snapshot, IReadOnlySet<string>? withheld)
        {
            var trace = new List<StructuralScopeTransition>();
            _contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations,
                scopeTrace: trace, withheldAppendixEntries: withheld);
            Trace = trace;
            var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, _contexts);
            _ranked = ranked.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
            _rank = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
            foreach (var block in snapshot.CandidateBlocks)
                foreach (var line in block.Lines)
                {
                    var key = Key(line.Page, PdfTextUtilities.Readable(line.Text));
                    if (!_blocksByLineKey.TryGetValue(key, out var list)) _blocksByLineKey[key] = list = [];
                    list.Add(block.Id);
                }
            Scopes = _contexts.Values.GroupBy(c => c.Source.StructuralScope)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            Penalised = ranked.Count(item => item.NegativeSignals.Contains("table_scope"));
            Selected = ranked.Take(SelectedBudget).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
            Emittable = Selected.Count(id => Array.IndexOf(PdfOutputDecisionPolicy.ExcludedScopes, ScopeOf(id)) < 0);
        }

        public IReadOnlyList<StructuralScopeTransition> Trace { get; }
        public Dictionary<string, int> Scopes { get; }
        public int Penalised { get; }
        public HashSet<string> Selected { get; }
        public int Emittable { get; }

        private string ScopeOf(string id) => _contexts.TryGetValue(id, out var c) ? c.Source.StructuralScope : "-";
        private string? Best(string lineKey) =>
            _blocksByLineKey.TryGetValue(lineKey, out var list)
                ? list.OrderBy(id => _rank.GetValueOrDefault(id, int.MaxValue)).First()
                : null;

        public string Scope(string lineKey) => Best(lineKey) is { } id ? ScopeOf(id) : "-";
        public int Rank(string lineKey) => Best(lineKey) is { } id ? _rank.GetValueOrDefault(id, -1) : -1;

        /// <summary>Where the loss sits now: the first thing that still stops this heading.</summary>
        public string Outcome(string lineKey)
        {
            var id = Best(lineKey);
            if (id is null) return "no_candidate";
            var scope = ScopeOf(id);
            var excluded = Array.IndexOf(PdfOutputDecisionPolicy.ExcludedScopes, scope) >= 0;
            var selected = Selected.Contains(id);
            if (selected && !excluded) return "emittable";
            if (!selected && _ranked.TryGetValue(id, out var candidate) &&
                candidate.NegativeSignals.Contains("table_scope")) return "table_scope_then_budget";
            if (!selected) return "ranking_or_budget";
            return $"excluded_scope:{scope}";
        }
    }

    private static Dictionary<string, List<(int Page, string Readable)>> ReviewedHeadings()
    {
        var result = new Dictionary<string, List<(int, string)>>(StringComparer.Ordinal);
        var path = Path.Combine(RepositoryRoot(), "eval", "manual-labels",
            "m105b-cross-document-review-labels.v1.json");
        if (!File.Exists(path)) return result;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var doc in document.RootElement.GetProperty("documents").EnumerateArray())
        {
            var stem = doc.GetProperty("stem").GetString() ?? "";
            var rows = doc.GetProperty("sample").EnumerateArray()
                .Where(r => r.TryGetProperty("role", out var role) && role.GetString() == "outline_heading")
                .Select(r => (r.GetProperty("page").GetInt32(), r.GetProperty("readable").GetString() ?? ""))
                .ToList();
            if (rows.Count > 0) result[stem] = rows;
        }
        return result;
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
