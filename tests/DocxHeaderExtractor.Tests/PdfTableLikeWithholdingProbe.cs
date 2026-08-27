using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.3-B2 diagnostic probe. Withholds the table-like mark from exactly the reviewed outline-heading
/// occurrences on 092 and replays the deterministic path below it, to test whether that annotation is
/// causal for their loss.
/// <para>
/// The intervention is addressed by line index, resolved from the reviewed labels and refused if a
/// label does not identify exactly one line. Two lines can read identically - a contents entry and
/// the heading it points at - and only one of them is the occurrence under study; this project has
/// spent several milestones removing exactly that confusion.
/// </para>
/// <para>
/// What this can and cannot measure: it has an oracle, so it reports recovery inside the reviewed
/// set. It cannot report the collateral of a general production rule, because no general rule is
/// being applied. That collateral is the population B1 recorded.
/// </para>
/// </summary>
public sealed class PdfTableLikeWithholdingProbe
{
    private const int SelectedBudget = 160;
    private const string WithheldScopeTransition = "b43";

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_3_B2_REPORT");
        if (output is null) return;

        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\07_system_generated\092_RFC9111_HTTP_Caching.docx");

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        var baseline = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);

        // Resolve reviewed labels to line identities, refusing anything ambiguous.
        var labelPath = Path.Combine(RepositoryRoot(), "eval", "manual-labels",
            "092-short-numbered-line-labels.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(labelPath));
        var eligible = document.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("shouldBeOutlineEligible").GetString() == "yes")
            .Select(item => (
                Page: item.GetProperty("page").GetInt32(),
                Readable: item.GetProperty("readable").GetString() ?? ""))
            .ToArray();

        var withheld = new HashSet<int>();
        var ambiguous = new List<string>();
        var unresolved = new List<string>();
        var lineKeys = baseline.Lines
            .Select((line, index) => (Index: index, Key: Key(line.Page, PdfTextUtilities.Readable(line.Text))))
            .ToArray();
        foreach (var label in eligible)
        {
            var matches = lineKeys.Where(x => x.Key == Key(label.Page, label.Readable)).ToArray();
            if (matches.Length == 1) withheld.Add(matches[0].Index);
            else if (matches.Length == 0) unresolved.Add($"p{label.Page} {label.Readable}");
            else ambiguous.Add($"p{label.Page} {label.Readable} x{matches.Length}");
        }

        Line("intervention=withhold_table_like_for_reviewed_outline_occurrences");
        Line("basis=reviewed_occurrence_line_identity usesModel=false productionRule=false");
        Line($"reviewed outline-eligible labels: {eligible.Length}");
        Line($"resolved to exactly one line:     {withheld.Count}");
        Line($"unresolved:                       {unresolved.Count}");
        foreach (var item in unresolved) Line($"    {item}");
        Line($"ambiguous (left out):             {ambiguous.Count}");
        foreach (var item in ambiguous) Line($"    {item}");

        var intervened = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx, withheld);

        var before = Measure(baseline);
        var after = Measure(intervened);

        Line("");
        Line("-- candidate population");
        Line($"{"measure",-40} {"before",8} {"after",8}");
        Line($"{"candidate blocks",-40} {baseline.CandidateBlocks.Count,8} {intervened.CandidateBlocks.Count,8}");
        Line($"{"blocks in scope table",-40} {before.ScopeTable,8} {after.ScopeTable,8}");
        Line($"{"blocks carrying table_scope",-40} {before.Penalised,8} {after.Penalised,8}");
        Line($"{"blocks scoring 0.00",-40} {before.ZeroScore,8} {after.ZeroScore,8}");
        Line($"{"emittable at budget",-40} {before.Emittable,8} {after.Emittable,8}");

        Line("");
        Line("-- recovery inside the reviewed set");
        var recovered = 0;
        var stillLost = 0;
        var nowEmittable = 0;
        foreach (var label in eligible.OrderBy(l => l.Page))
        {
            var key = Key(label.Page, label.Readable);
            var beforeBlock = before.BlockCarrying(key);
            var afterBlock = after.BlockCarrying(key);
            if (beforeBlock is null && afterBlock is null) continue;
            var wasSelected = beforeBlock is not null && before.Selected.Contains(beforeBlock);
            var isSelected = afterBlock is not null && after.Selected.Contains(afterBlock);
            var wasEmittable = beforeBlock is not null && before.EmittableIds.Contains(beforeBlock);
            var isEmittable = afterBlock is not null && after.EmittableIds.Contains(afterBlock);
            if (!wasSelected && isSelected) recovered++;
            if (!isSelected) stillLost++;
            if (!wasEmittable && isEmittable) nowEmittable++;
            Line($"  p{label.Page,-3} {Trim(label.Readable),-40}");
            Line($"       before {beforeBlock ?? "-",-12} scope={before.Scope(beforeBlock),-20} " +
                 $"score={before.Score(beforeBlock),5:F2} rank={before.Rank(beforeBlock),-5} " +
                 $"selected={wasSelected,-5} emittable={wasEmittable}");
            Line($"       after  {afterBlock ?? "-",-12} scope={after.Scope(afterBlock),-20} " +
                 $"score={after.Score(afterBlock),5:F2} rank={after.Rank(afterBlock),-5} " +
                 $"selected={isSelected,-5} emittable={isEmittable}");
        }

        Line("");
        Line($"entered selection: {recovered}");
        Line($"still not selected: {stillLost}");
        Line($"became emittable:  {nowEmittable}");

        Line("");
        Line("-- displacement at a fixed budget (whole population)");
        var leftEmittable = before.EmittableIds.Except(after.EmittableIds, StringComparer.Ordinal).ToArray();
        var joinedEmittable = after.EmittableIds.Except(before.EmittableIds, StringComparer.Ordinal).ToArray();
        Line($"left emittable:   {leftEmittable.Length}");
        Line($"joined emittable: {joinedEmittable.Length}");
        Line("");
        Line("what left:");
        foreach (var id in leftEmittable.OrderBy(before.Rank))
            Line($"  - {id,-12} rank {before.Rank(id),-5} -> {after.Rank(id),-5} {before.Text(id)}");
        Line("");
        Line("what joined:");
        foreach (var id in joinedEmittable.OrderBy(after.Rank))
            Line($"  + {id,-12} rank {before.Rank(id),-5} -> {after.Rank(id),-5} {after.Text(id)}");

        Line("");
        Line("-- recovery cost inside the reviewed set only");
        Line("This intervention has an oracle. It cannot report the collateral of a general rule,");
        Line("because no general rule was applied. B1's population figure stands as that number:");
        Line("93 of 125 short_numbered lines are not outline-eligible.");

        File.WriteAllText(output, report.ToString());
    }

    private static string Key(int page, string readable) =>
        $"{page}|{Regex.Replace(readable, @"\s+", " ").Trim()}";

    private static Outcome Measure(PdfCandidateRankingSnapshot snapshot)
    {
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations,
            withheldAppendixEntries: new HashSet<string>(StringComparer.Ordinal) { WithheldScopeTransition });
        var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
        return new Outcome(snapshot.CandidateBlocks, contexts, ranked);
    }

    private sealed class Outcome
    {
        private static readonly string[] ExcludedScopes = ["embedded_amendment", "quoted_replacement", "appendix_table"];
        private readonly IReadOnlyDictionary<string, PdfCandidateContext> _contexts;
        private readonly Dictionary<string, RankedCandidate> _ranked;
        private readonly Dictionary<string, int> _rank;
        // An occurrence can appear in several candidate representations - a standalone block and one
        // or more windows carrying it. Taking whichever was seen first would report a window's fate as
        // the occurrence's, so the best-ranked representation is the one that answers "did this
        // occurrence become reachable".
        private readonly Dictionary<string, List<string>> _blocksByLineKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PdfSemanticBlock> _blocks;

        public Outcome(IReadOnlyList<PdfSemanticBlock> blocks,
            IReadOnlyDictionary<string, PdfCandidateContext> contexts,
            IReadOnlyList<RankedCandidate> ranked)
        {
            _contexts = contexts;
            _blocks = blocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
            _ranked = ranked.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
            _rank = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
            foreach (var block in blocks)
                foreach (var line in block.Lines)
                {
                    var key = Key(line.Page, PdfTextUtilities.Readable(line.Text));
                    if (!_blocksByLineKey.TryGetValue(key, out var list))
                        _blocksByLineKey[key] = list = [];
                    list.Add(block.Id);
                }
            ScopeTable = contexts.Values.Count(c => c.Source.StructuralScope == "table");
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

        public string? BlockCarrying(string lineKey) =>
            _blocksByLineKey.TryGetValue(lineKey, out var candidates)
                ? candidates.OrderBy(id => _rank.GetValueOrDefault(id, int.MaxValue)).First()
                : null;
        public string Scope(string? id) =>
            id is not null && _contexts.TryGetValue(id, out var c) ? c.Source.StructuralScope : "-";
        public double Score(string? id) =>
            id is not null && _ranked.TryGetValue(id, out var item) ? item.CandidateScore : double.NaN;
        public int Rank(string? id) => id is null ? -1 : _rank.GetValueOrDefault(id, -1);

        public string Text(string? id)
        {
            if (id is null || !_blocks.TryGetValue(id, out var block)) return "-";
            var single = Regex.Replace(block.DisplayText, @"\s+", " ").Trim();
            return single.Length <= 70 ? single : single[..70] + "...";
        }
    }

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 40 ? single : single[..40] + "...";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
