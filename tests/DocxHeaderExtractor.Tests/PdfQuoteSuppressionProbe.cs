using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.4-A2a diagnostic probe. Withholds one reviewed quote-open transition - `s-line-830` on page 28
/// of 092 - and changes nothing else: the other five opening blocks keep their triggers, the close
/// semantics are untouched, and appendix, table-like, ranking and output policy all run as they are.
/// <para>
/// One transition rather than all six on purpose. Suppressing every trigger would answer whether
/// quote scope matters, but would hide which trigger the loss actually needed, and a document whose
/// close condition can never fire will relatch on the next opening block if one exists. So the first
/// thing this reports is whether the latch comes back, and where.
/// </para>
/// <para>
/// The reviewed basis: the source sentence quotes "HTTP/1 1" and "HTTP Semantics", and the renderer
/// splits the first across two lines, leaving three straight quotes on one of them. Page 28 carries
/// eight straight quotes in total, an even count, so the source quoting is balanced and the odd
/// parity belongs to line segmentation.
/// </para>
/// </summary>
public sealed class PdfQuoteSuppressionProbe
{
    private const int SelectedBudget = 160;
    // The reviewed occurrence, not one block. The same source line reaches candidate construction as
    // both a standalone line and a window, and each satisfies the open condition on its own, so
    // withholding one representation leaves the other to set the latch on the same page. This is one
    // reviewed transition addressed completely, not two transitions.
    private static readonly string[] WithheldQuoteEntries = ["s-line-830", "s-window-2004"];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_4_A2_REPORT");
        if (output is null) return;

        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\07_system_generated\092_RFC9111_HTTP_Caching.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"intervention=withhold_quote_open:{string.Join('+', WithheldQuoteEntries)}");
        Line("basis=reviewed_segmentation_artifact usesGold=false usesModel=false productionRule=false");
        Line("unchanged: five other opening blocks, close semantics, appendix, TableLike, ranking, output");

        var withheld = new HashSet<string>(WithheldQuoteEntries, StringComparer.Ordinal);
        var before = new Outcome(snapshot, null);
        var after = new Outcome(snapshot, withheld);

        Line("");
        Line("-- does the latch come back, and where");
        var relatch = after.Trace.FirstOrDefault(t => t.QuoteOpened && !t.QuoteClosed && !withheld.Contains(t.SourceId)
                                                      && t.Page >= 28);
        if (relatch is null)
        {
            Line("no block opens the latch again after the withheld one");
        }
        else
        {
            Line($"first subsequent opening block: p{relatch.Page} {relatch.SourceId}");
            Line($"  straight={relatch.StraightQuotes} leftCurly={relatch.LeftCurlyQuotes} " +
                 $"rightCurly={relatch.RightCurlyQuotes} quoteBefore={relatch.QuoteStateBefore}");
            Line($"  incoming={relatch.IncomingScope} resulting={relatch.ResultingScope}");
            Line($"  text: {Trim(relatch.RawText)}");
        }

        Line("");
        Line("-- scope distribution");
        Line($"{"scope",-24} {"before",8} {"after",8}");
        foreach (var scope in before.Scopes.Keys.Union(after.Scopes.Keys).OrderBy(x => x))
            Line($"{scope,-24} {before.Scopes.GetValueOrDefault(scope),8} {after.Scopes.GetValueOrDefault(scope),8}");

        Line("");
        Line("-- pages 28-35 only");
        foreach (var scope in before.Scopes.Keys.Union(after.Scopes.Keys).OrderBy(x => x))
        {
            var a = before.Band(28, 35, scope);
            var b = after.Band(28, 35, scope);
            if (a == 0 && b == 0) continue;
            Line($"{scope,-24} {a,8} {b,8}");
        }

        Line("");
        Line("-- the real appendix headings on page 32");
        foreach (var id in new[] { "b485", "b490" })
            Line($"  {id}: {before.Scope(id)} -> {after.Scope(id)}, " +
                 $"rank {before.Rank(id)} -> {after.Rank(id)}, " +
                 $"emittable {before.EmittableIds.Contains(id)} -> {after.EmittableIds.Contains(id)}");

        Line("");
        Line("-- selection and output");
        Line($"{"measure",-38} {"before",8} {"after",8}");
        Line($"{"selected at budget",-38} {before.Selected.Count,8} {after.Selected.Count,8}");
        Line($"{"emittable at budget",-38} {before.Emittable,8} {after.Emittable,8}");

        var labelPath = Path.Combine(RepositoryRoot(), "eval", "manual-labels",
            "092-short-numbered-line-labels.v1.json");
        if (File.Exists(labelPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(labelPath));
            var labels = document.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => (
                    Page: item.GetProperty("page").GetInt32(),
                    Readable: item.GetProperty("readable").GetString() ?? "",
                    Role: item.GetProperty("role").GetString() ?? ""))
                .ToArray();

            Line("");
            Line("-- the reviewed headings the quote latch was holding (pages 28 and later)");
            var held = labels.Where(l => l.Role == "outline_heading" && l.Page >= 28).ToArray();
            Line($"count: {held.Length}");
            foreach (var item in held)
            {
                var key = Key(item.Page, item.Readable);
                Line($"  p{item.Page,-3} {Trim(item.Readable),-38} " +
                     $"selected {before.IsSelected(key)} -> {after.IsSelected(key)}, " +
                     $"emittable {before.IsEmittable(key)} -> {after.IsEmittable(key)}");
            }

            Line("");
            Line("-- fate by reviewed role, whole population");
            Line($"{"role",-30} {"n",4} {"emit before",12} {"emit after",11}");
            foreach (var group in labels.GroupBy(l => l.Role).OrderByDescending(g => g.Count()))
            {
                var keys = group.Select(l => Key(l.Page, l.Readable)).ToArray();
                Line($"{group.Key,-30} {group.Count(),4} " +
                     $"{keys.Count(before.IsEmittable),12} {keys.Count(after.IsEmittable),11}");
            }
        }

        Line("");
        Line("-- everything newly emittable, whatever it is");
        var newly = after.EmittableIds.Except(before.EmittableIds, StringComparer.Ordinal).ToArray();
        Line($"count: {newly.Length}");
        foreach (var id in newly.OrderBy(after.Rank))
            Line($"  + {id,-14} p{after.Page(id),-3} rank {before.Rank(id)} -> {after.Rank(id)} {after.Text(id)}");
        var lost = before.EmittableIds.Except(after.EmittableIds, StringComparer.Ordinal).ToArray();
        Line($"no longer emittable: {lost.Length}");
        foreach (var id in lost.OrderBy(before.Rank))
            Line($"  - {id,-14} p{before.Page(id),-3} rank {before.Rank(id)} -> {after.Rank(id)} {before.Text(id)}");

        File.WriteAllText(output, report.ToString());
    }

    private sealed class Outcome
    {
        private static readonly string[] ExcludedScopes = ["embedded_amendment", "quoted_replacement", "appendix_table"];
        private readonly IReadOnlyDictionary<string, PdfCandidateContext> _contexts;
        private readonly Dictionary<string, int> _rank;
        private readonly Dictionary<string, PdfSemanticBlock> _blocks;
        private readonly Dictionary<string, List<string>> _blocksByLineKey = new(StringComparer.Ordinal);

        public Outcome(PdfCandidateRankingSnapshot snapshot, IReadOnlySet<string>? withheldQuote)
        {
            var trace = new List<StructuralScopeTransition>();
            _contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations,
                scopeTrace: trace, withheldQuoteEntries: withheldQuote);
            Trace = trace;
            var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, _contexts);
            _rank = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
            _blocks = snapshot.CandidateBlocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
            foreach (var block in snapshot.CandidateBlocks)
                foreach (var line in block.Lines)
                {
                    var key = Key(line.Page, PdfTextUtilities.Readable(line.Text));
                    if (!_blocksByLineKey.TryGetValue(key, out var list)) _blocksByLineKey[key] = list = [];
                    list.Add(block.Id);
                }
            Scopes = _contexts.Values.GroupBy(c => c.Source.StructuralScope)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            Selected = ranked.Take(SelectedBudget).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
            EmittableIds = Selected.Where(id => Array.IndexOf(ExcludedScopes, Scope(id)) < 0)
                .ToHashSet(StringComparer.Ordinal);
            Emittable = EmittableIds.Count;
        }

        public IReadOnlyList<StructuralScopeTransition> Trace { get; }
        public Dictionary<string, int> Scopes { get; }
        public HashSet<string> Selected { get; }
        public HashSet<string> EmittableIds { get; }
        public int Emittable { get; }

        public string Scope(string id) => _contexts.TryGetValue(id, out var c) ? c.Source.StructuralScope : "-";
        public int Rank(string id) => _rank.GetValueOrDefault(id, -1);
        public int Page(string id) => _blocks.TryGetValue(id, out var b) ? b.Page : 0;

        public string Text(string id)
        {
            if (!_blocks.TryGetValue(id, out var block)) return "-";
            return Trim(block.DisplayText);
        }

        public int Band(int lo, int hi, string scope) => _contexts.Values
            .Count(c => c.Source.Page >= lo && c.Source.Page <= hi && c.Source.StructuralScope == scope);

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
