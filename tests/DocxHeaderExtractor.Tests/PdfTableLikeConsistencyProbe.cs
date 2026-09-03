using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.3-B4 diagnostic probe. Tests one hypothesis over the whole population, with no oracle:
/// a line the <c>short_numbered</c> branch marks, which also carries the existing
/// <c>HasStructuralMarker</c> fact, is treated as not table-like - and every existing consumer then
/// runs unchanged.
/// <para>
/// The intervention sits at the fact boundary on purpose. Production already applies
/// <c>HasStructuralMarker</c> at candidate grouping; applying it a second time inside scope
/// derivation would duplicate a judgement rather than make two layers agree. So this asks what the
/// pipeline does when the two layers are made consistent, and deliberately does not ask which file a
/// repair would live in. That is a question for after the gate, not a thing to decide by where a
/// probe happened to reach in.
/// </para>
/// <para>
/// The predicate uses no gold. Labels appear only to report where the affected lines land, and the
/// contents entries are reported as first-class collateral - "the contents lane will catch them" is
/// an assumption about a different owner that is currently silent on this document, not a result.
/// </para>
/// </summary>
public sealed class PdfTableLikeConsistencyProbe
{
    private const int SelectedBudget = 160;
    private const string WithheldScopeTransition = "b43";

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_3_B4_REPORT");
        if (output is null) return;

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("hypothesis: short_numbered AND HasStructuralMarker => diagnostically not table-like");
        Line("intervention=fact_boundary usesGold=false usesModel=false productionRule=false");

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var target = Path.Combine(corpus, "07_system_generated", "092_RFC9111_HTTP_Caching.docx");

        // --- 092, the document the hypothesis came from
        Line("");
        Line("================ 092, corrected-scope world ================");
        Measure(target, Line, withholdAppendix: true, withLabels: true);

        Line("");
        Line("================ 092, shipped world (appendix leak present) ================");
        Measure(target, Line, withholdAppendix: false, withLabels: true);

        // --- cross-domain holdout: same intervention, unchanged, no tuning
        foreach (var (name, path) in new[]
                 {
                     ("010", Path.Combine(corpus, "01_phap_quy", "010_Luat_An_ninh_mang_24-2018-QH14.docx")),
                     ("054", Path.Combine(corpus, "03_tai_chinh_ke_toan", "054_IBRD_Information_Statement_FY25.docx")),
                     ("076", Path.Combine(corpus, "05_ky_thuat_cong_nghe", "076_RFC2616_HTTP11.docx")),
                 })
        {
            Line("");
            Line($"================ {name}, cross-domain holdout ================");
            var resolved = File.Exists(path)
                ? path
                : Directory.Exists(corpus)
                    ? Directory.GetFiles(corpus, name + "_*.docx", SearchOption.AllDirectories).FirstOrDefault()
                    : null;
            if (resolved is null) { Line("document not found; not measured"); continue; }
            Measure(resolved, Line, withholdAppendix: false, withLabels: false);
        }

        File.WriteAllText(output, report.ToString());
    }

    private static void Measure(string docx, Action<string> Line, bool withholdAppendix, bool withLabels)
    {
        var baseline = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        if (baseline.CandidateBlocks.Count == 0) { Line("no candidates; not measured"); return; }

        // The hypothesis, evaluated over every line. No labels consulted.
        var withheld = new HashSet<int>();
        for (var index = 0; index < baseline.Lines.Count; index++)
        {
            var text = baseline.Lines[index].Text;
            if (PdfLineBlockFilter.ClassifyTableLine(text) == "short_numbered" &&
                PdfLineBlockAnnotation.HasStructuralMarker(text))
                withheld.Add(index);
        }
        var intervened = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx, withheld);

        var before = new Outcome(baseline, withholdAppendix);
        var after = new Outcome(intervened, withholdAppendix);

        Line($"lines affected by the hypothesis: {withheld.Count} of {baseline.Lines.Count}");
        Line($"{"measure",-38} {"before",8} {"after",8}");
        Line($"{"candidate blocks",-38} {baseline.CandidateBlocks.Count,8} {intervened.CandidateBlocks.Count,8}");
        Line($"{"blocks in scope table",-38} {before.ScopeTable,8} {after.ScopeTable,8}");
        Line($"{"blocks carrying table_scope",-38} {before.Penalised,8} {after.Penalised,8}");
        Line($"{"blocks scoring 0.00",-38} {before.ZeroScore,8} {after.ZeroScore,8}");
        Line($"{"selected at budget",-38} {before.Selected.Count,8} {after.Selected.Count,8}");
        Line($"{"emittable at budget",-38} {before.Emittable,8} {after.Emittable,8}");

        if (!withLabels) return;

        var labelPath = Path.Combine(RepositoryRoot(), "eval", "manual-labels",
            "092-short-numbered-line-labels.v1.json");
        if (!File.Exists(labelPath)) { Line("labels missing; fate by role not measured"); return; }
        using var document = JsonDocument.Parse(File.ReadAllText(labelPath));
        var labels = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (
                Page: item.GetProperty("page").GetInt32(),
                Readable: item.GetProperty("readable").GetString() ?? "",
                Role: item.GetProperty("role").GetString() ?? ""))
            .ToArray();

        Line("");
        Line("-- fate by reviewed role (every role, not just the headings)");
        Line($"{"role",-30} {"n",4} {"sel before",11} {"sel after",10} {"emit before",12} {"emit after",11}");
        foreach (var group in labels.GroupBy(l => l.Role).OrderByDescending(g => g.Count()))
        {
            var keys = group.Select(l => Key(l.Page, l.Readable)).ToArray();
            Line($"{group.Key,-30} {group.Count(),4} " +
                 $"{keys.Count(before.IsSelected),11} {keys.Count(after.IsSelected),10} " +
                 $"{keys.Count(before.IsEmittable),12} {keys.Count(after.IsEmittable),11}");
        }

        Line("");
        Line("-- contents entries that become emittable, listed individually");
        var released = labels.Where(l => l.Role == "toc_entry")
            .Where(l => !before.IsEmittable(Key(l.Page, l.Readable)) && after.IsEmittable(Key(l.Page, l.Readable)))
            .ToArray();
        Line($"count: {released.Length}");
        foreach (var item in released)
            Line($"  p{item.Page,-3} {Trim(item.Readable)}");

        Line("");
        Line("-- other non-outline roles that become emittable");
        foreach (var role in new[] { "body_prose", "metadata", "caption_or_other_structural", "table_cell_or_tabular_value" })
        {
            var newly = labels.Where(l => l.Role == role)
                .Where(l => !before.IsEmittable(Key(l.Page, l.Readable)) && after.IsEmittable(Key(l.Page, l.Readable)))
                .ToArray();
            Line($"  {role,-30} {newly.Length}");
            foreach (var item in newly) Line($"      p{item.Page,-3} {Trim(item.Readable)}");
        }
    }

    private sealed class Outcome
    {
        private static readonly string[] ExcludedScopes = ["embedded_amendment", "quoted_replacement", "appendix_table"];
        private readonly Dictionary<string, List<string>> _blocksByLineKey = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, PdfCandidateContext> _contexts;
        private readonly Dictionary<string, int> _rank;

        public Outcome(PdfCandidateRankingSnapshot snapshot, bool withholdAppendix)
        {
            _contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations,
                withheldAppendixEntries: withholdAppendix
                    ? new HashSet<string>(StringComparer.Ordinal) { WithheldScopeTransition }
                    : null);
            var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, _contexts);
            _rank = ranked.Select((item, index) => (item.SourceId, Rank: index))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
            foreach (var block in snapshot.CandidateBlocks)
                foreach (var line in block.Lines)
                {
                    var key = Key(line.Page, PdfTextUtilities.Readable(line.Text));
                    if (!_blocksByLineKey.TryGetValue(key, out var list)) _blocksByLineKey[key] = list = [];
                    list.Add(block.Id);
                }
            ScopeTable = _contexts.Values.Count(c => c.Source.StructuralScope == "table");
            Penalised = ranked.Count(item => item.NegativeSignals.Contains("table_scope"));
            ZeroScore = ranked.Count(item => item.CandidateScore <= 0.0001);
            Selected = ranked.Take(SelectedBudget).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
            EmittableIds = Selected
                .Where(id => Array.IndexOf(ExcludedScopes, Scope(id)) < 0)
                .ToHashSet(StringComparer.Ordinal);
            Emittable = EmittableIds.Count;
        }

        public int ScopeTable { get; }
        public int Penalised { get; }
        public int ZeroScore { get; }
        public HashSet<string> Selected { get; }
        public HashSet<string> EmittableIds { get; }
        public int Emittable { get; }

        private string Scope(string id) =>
            _contexts.TryGetValue(id, out var c) ? c.Source.StructuralScope : "-";

        // An occurrence can have several representations; it counts as reaching a stage if the
        // best-ranked block carrying it does.
        private string? Best(string lineKey) =>
            _blocksByLineKey.TryGetValue(lineKey, out var list)
                ? list.OrderBy(id => _rank.GetValueOrDefault(id, int.MaxValue)).First()
                : null;

        public bool IsSelected(string lineKey) => Best(lineKey) is { } id && Selected.Contains(id);
        public bool IsEmittable(string lineKey) => Best(lineKey) is { } id && EmittableIds.Contains(id);
    }

    private static string Key(int page, string readable) =>
        $"{page}|{Regex.Replace(readable, @"\s+", " ").Trim()}";

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 70 ? single : single[..70] + "...";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
