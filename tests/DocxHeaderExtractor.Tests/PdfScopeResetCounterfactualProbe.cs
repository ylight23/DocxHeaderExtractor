using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.3-A2 diagnostic probe. Withholds exactly one reviewed scope transition - the appendix entry at
/// b43 on page 4 of 092 - and replays the deterministic path below it. Everything else in the tracker
/// is untouched, including the real appendix triggers on page 32, so the question it answers is
/// narrow: was that single transition the cause of the downstream loss?
/// <para>
/// The intervention is a reviewed source id, not a predicate. It is deliberately not a rule anything
/// could learn, because inventing a classifier here would be a remediation wearing a counterfactual's
/// clothes.
/// </para>
/// </summary>
public sealed class PdfScopeResetCounterfactualProbe
{
    private const int SelectedBudget = 160;
    private const string WithheldTransition = "b43";

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_3_CF_REPORT");
        if (output is null) return;

        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\07_system_generated\092_RFC9111_HTTP_Caching.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var blocks = snapshot.CandidateBlocks;

        var actual = Run(blocks, snapshot.Annotations, null);
        var counterfactual = Run(blocks, snapshot.Annotations,
            new HashSet<string>(StringComparer.Ordinal) { WithheldTransition });

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"intervention=withhold_appendix_entry:{WithheldTransition}");
        Line("interventionBasis=reviewed_false_scope_transition usesModel=false productionRule=false");
        Line($"blocks={blocks.Count} budget={SelectedBudget}");

        Line("");
        Line("-- scope distribution");
        Line($"{"scope",-24} {"actual",8} {"counterfactual",15}");
        foreach (var scope in actual.Scopes.Keys.Union(counterfactual.Scopes.Keys).OrderBy(s => s))
            Line($"{scope,-24} {actual.Scopes.GetValueOrDefault(scope),8} " +
                 $"{counterfactual.Scopes.GetValueOrDefault(scope),15}");

        Line("");
        Line("-- scope by page band");
        foreach (var band in new (string Name, int Lo, int Hi)[] { ("p1-4", 1, 4), ("p5-31", 5, 31), ("p32-35", 32, 35) })
        {
            Line($"{band.Name}:");
            foreach (var scope in actual.Scopes.Keys.Union(counterfactual.Scopes.Keys).OrderBy(s => s))
            {
                var a = actual.Band(band.Lo, band.Hi, scope);
                var c = counterfactual.Band(band.Lo, band.Hi, scope);
                if (a == 0 && c == 0) continue;
                Line($"  {scope,-22} {a,8} {c,15}");
            }
        }

        Line("");
        Line("-- selection and output");
        Line($"{"measure",-40} {"actual",8} {"counterfactual",15}");
        Line($"{"blocks scoring scope_conflict",-40} {actual.ScopeConflict,8} {counterfactual.ScopeConflict,15}");
        Line($"{"selected at budget",-40} {actual.Selected.Count,8} {counterfactual.Selected.Count,15}");
        Line($"{"selected and in an excluded scope",-40} {actual.SelectedExcluded,8} {counterfactual.SelectedExcluded,15}");
        Line($"{"emittable (selected, scope allowed)",-40} {actual.Emittable,8} {counterfactual.Emittable,15}");
        Line($"{"excluded by scope, whole population",-40} {actual.ExcludedByScope,8} {counterfactual.ExcludedByScope,15}");

        Line("");
        Line("-- selection churn (whole population, not just the headings of interest)");
        var entered = counterfactual.Selected.Except(actual.Selected, StringComparer.Ordinal).ToArray();
        var left = actual.Selected.Except(counterfactual.Selected, StringComparer.Ordinal).ToArray();
        Line($"entered selection: {entered.Length}");
        Line($"left selection:    {left.Length}");
        foreach (var id in entered.Take(40))
            Line($"  + {id} p{actual.Page(id)} {actual.Scope(id)}->{counterfactual.Scope(id)} {actual.Text(id)}");
        foreach (var id in left.Take(40))
            Line($"  - {id} p{actual.Page(id)} {actual.Scope(id)}->{counterfactual.Scope(id)} {actual.Text(id)}");

        Line("");
        Line("-- newly emittable");
        var newlyEmittable = counterfactual.EmittableIds.Except(actual.EmittableIds, StringComparer.Ordinal).ToArray();
        Line($"count: {newlyEmittable.Length}");
        foreach (var id in newlyEmittable.OrderBy(counterfactual.RankOf))
            Line($"  {id} p{actual.Page(id)} rank={counterfactual.RankOf(id)} {actual.Text(id)}");

        Line("");
        Line("-- no longer emittable");
        var lost = actual.EmittableIds.Except(counterfactual.EmittableIds, StringComparer.Ordinal).ToArray();
        Line($"count: {lost.Length}");
        foreach (var id in lost.Take(40))
            Line($"  {id} p{actual.Page(id)} {actual.Text(id)}");

        Line("");
        Line("-- the four headings 092 is missing");
        // The body occurrences, by reviewed source id. Looking them up by text would find the
        // contents block on page 2 first, which is a different occurrence of the same words.
        foreach (var id in new[] { "b45", "b53", "b58", "b61" })
        {
            var needle = actual.Text(id);
            Line($"  {needle}: {id} p{actual.Page(id)} " +
                 $"scope {actual.Scope(id)} -> {counterfactual.Scope(id)}, " +
                 $"rank {actual.RankOf(id)} -> {counterfactual.RankOf(id)}, " +
                 $"emittable {actual.EmittableIds.Contains(id)} -> {counterfactual.EmittableIds.Contains(id)}");
            Line($"      actual  score={actual.Score(id):F2} neg=[{actual.Negative(id)}] amb=[{actual.Ambiguity(id)}]");
            Line($"      cf      score={counterfactual.Score(id):F2} neg=[{counterfactual.Negative(id)}] amb=[{counterfactual.Ambiguity(id)}]");
        }

        File.WriteAllText(output, report.ToString());
    }

    private static Outcome Run(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        IReadOnlySet<string>? withheld)
    {
        var contexts = PdfCandidateContextBuilder.Build(blocks, annotations, withheldAppendixEntries: withheld);
        var ranked = PdfCandidateRanker.Rank(blocks, contexts);
        return new Outcome(blocks, contexts, ranked);
    }

    private sealed class Outcome
    {
        private static readonly string[] ExcludedScopes = ["embedded_amendment", "quoted_replacement", "appendix_table"];
        private readonly IReadOnlyDictionary<string, PdfCandidateContext> _contexts;
        private readonly Dictionary<string, int> _rank;
        private readonly Dictionary<string, PdfSemanticBlock> _blocks;
        private readonly Dictionary<string, RankedCandidate> _ranked;

        public Outcome(IReadOnlyList<PdfSemanticBlock> blocks,
            IReadOnlyDictionary<string, PdfCandidateContext> contexts,
            IReadOnlyList<RankedCandidate> ranked)
        {
            _contexts = contexts;
            _blocks = blocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
            _rank = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
            _ranked = ranked.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
            Scopes = contexts.Values.GroupBy(c => c.Source.StructuralScope)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            ScopeConflict = ranked.Count(item => item.AmbiguitySignals.Contains("scope_conflict"));
            Selected = ranked.Take(SelectedBudget).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
            SelectedExcluded = Selected.Count(id => IsExcluded(Scope(id)));
            EmittableIds = Selected.Where(id => !IsExcluded(Scope(id))).ToHashSet(StringComparer.Ordinal);
            Emittable = EmittableIds.Count;
            ExcludedByScope = contexts.Values.Count(c => IsExcluded(c.Source.StructuralScope));
        }

        public Dictionary<string, int> Scopes { get; }
        public int ScopeConflict { get; }
        public HashSet<string> Selected { get; }
        public int SelectedExcluded { get; }
        public HashSet<string> EmittableIds { get; }
        public int Emittable { get; }
        public int ExcludedByScope { get; }

        private static bool IsExcluded(string scope) => Array.IndexOf(ExcludedScopes, scope) >= 0;
        public string Scope(string id) => _contexts.TryGetValue(id, out var c) ? c.Source.StructuralScope : "?";
        public int Page(string id) => _blocks.TryGetValue(id, out var b) ? b.Page : 0;
        public int RankOf(string id) => _rank.GetValueOrDefault(id, -1);
        public double Score(string id) => _ranked.TryGetValue(id, out var item) ? item.CandidateScore : double.NaN;
        public string Negative(string id) =>
            _ranked.TryGetValue(id, out var item) ? string.Join(",", item.NegativeSignals) : "?";
        public string Ambiguity(string id) =>
            _ranked.TryGetValue(id, out var item) ? string.Join(",", item.AmbiguitySignals) : "?";

        public string Text(string id)
        {
            if (!_blocks.TryGetValue(id, out var block)) return "?";
            var single = Regex.Replace(block.DisplayText, @"\s+", " ").Trim();
            return single.Length <= 80 ? single : single[..80] + "...";
        }

        public int Band(int lo, int hi, string scope) => _contexts.Values
            .Count(c => c.Source.Page >= lo && c.Source.Page <= hi && c.Source.StructuralScope == scope);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
