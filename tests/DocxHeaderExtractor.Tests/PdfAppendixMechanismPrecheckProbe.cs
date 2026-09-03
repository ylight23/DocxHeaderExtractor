using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.6 precheck. Asks whether 032 and 091 reach `appendix_table` by the mechanism 092 did - a
/// non-appendix source occurrence setting a latch that never resets - or by some other route.
/// Passive trace of existing state only: no counterfactual, no model, no production change.
/// <para>
/// It also splits the fourteen reviewed headings by why they fail, because "all fourteen carry
/// appendix_table" does not establish that appendix scope caused fourteen losses. A heading already
/// outside the budget would not have been emitted whatever its scope, and treating those as
/// appendix-caused would repeat the M10.3-A error of reading a visible wrong scope as the whole
/// cause.
/// </para>
/// </summary>
public sealed class PdfAppendixMechanismPrecheckProbe
{
    private const int SelectedBudget = 160;
    private static readonly string[] ExcludedScopes = ["embedded_amendment", "quoted_replacement", "appendix_table"];

    private static readonly (string Stem, string Relative)[] Documents =
    [
        ("032", @"02_hop_dong_mua_sam\032_WB_Plant_TwoStage_2020.docx"),
        ("091", @"07_system_generated\091_RFC9110_HTTP_Semantics.docx"),
        ("092", @"07_system_generated\092_RFC9111_HTTP_Caching.docx"),
    ];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_6_PRECHECK");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("contract=passive_state_trace_only no_counterfactual no_model no_production_change");

        var headingsByDocument = ReviewedHeadings();

        foreach (var (stem, relative) in Documents)
        {
            var path = Path.Combine(corpus, relative);
            Line("");
            Line($"================ {stem} ================");
            if (!File.Exists(path)) { Line("document not found"); continue; }

            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var trace = new List<StructuralScopeTransition>();
            var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations,
                scopeTrace: trace);
            var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
            var rankOf = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
            var selected = ranked.Take(SelectedBudget).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);

            var triggers = trace.Where(t => t.AppendixTriggeredHere).ToArray();
            var first = triggers.FirstOrDefault();
            Line($"blocks={trace.Count} appendixTriggers={triggers.Length}");
            if (first is null)
            {
                Line("no appendix trigger; appendix_table cannot come from this mechanism here");
            }
            else
            {
                var after = trace.SkipWhile(t => t.SourceId != first.SourceId).ToArray();
                Line($"firstAppendixTransition = {first.SourceId} p{first.Page}");
                Line($"  trigger text     = {Trim(first.RawText)}");
                Line($"  incomingScope    = {first.IncomingScope}");
                Line($"  resultingScope   = {first.ResultingScope}");
                Line($"  latchPersists    = {after.Count(t => t.AppendixLatched)} of {after.Length} following blocks");
                Line($"  resetCount       = {Resets(trace)}");
                Line($"  pagesAfter       = {after.Min(t => t.Page)}..{after.Max(t => t.Page)}");
                Line($"  scopes produced  = appendix {trace.Count(t => t.ResultingScope == "appendix")}, " +
                     $"appendix_table {trace.Count(t => t.ResultingScope == "appendix_table")}");
                Line("  all appendix triggers:");
                foreach (var entry in triggers.Take(8))
                    Line($"    p{entry.Page,-4} {entry.SourceId,-12} incoming={entry.IncomingScope,-18} " +
                         $"{Trim(entry.RawText)}");
            }

            if (!headingsByDocument.TryGetValue(stem, out var headings)) continue;

            Line("");
            Line("  reviewed headings, split by why they fail");
            Line($"  {"line",-34} {"rank",6} {"sel",6} {"scope",-16} {"failure"}");
            var blocked = 0;
            var outside = 0;
            foreach (var (page, readable) in headings)
            {
                var key = Key(page, readable);
                var blockId = snapshot.CandidateBlocks
                    .Where(b => b.Lines.Any(l => Key(l.Page, PdfTextUtilities.Readable(l.Text)) == key))
                    .OrderBy(b => rankOf.GetValueOrDefault(b.Id, int.MaxValue))
                    .FirstOrDefault()?.Id;
                var scope = blockId is not null && contexts.TryGetValue(blockId, out var c)
                    ? c.Source.StructuralScope : "-";
                var rank = blockId is null ? -1 : rankOf.GetValueOrDefault(blockId, -1);
                var isSelected = blockId is not null && selected.Contains(blockId);
                var excluded = Array.IndexOf(ExcludedScopes, scope) >= 0;
                var failure = isSelected && excluded ? "A_selected_but_scope_excluded"
                    : !isSelected ? "B_outside_budget_before_scope_applies"
                    : "emitted";
                if (failure.StartsWith("A_", StringComparison.Ordinal)) blocked++;
                if (failure.StartsWith("B_", StringComparison.Ordinal)) outside++;
                Line($"  {Trim(readable, 34),-34} {rank,6} {isSelected,6} {scope,-16} {failure}");
            }
            Line($"  A (appendix scope is the direct first loss): {blocked}");
            Line($"  B (already outside budget, scope coexists):  {outside}");
        }

        File.WriteAllText(output, report.ToString());
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

    private static int Resets(IReadOnlyList<StructuralScopeTransition> trace)
    {
        var seenOn = false;
        var resets = 0;
        foreach (var entry in trace)
        {
            if (seenOn && !entry.AppendixLatched) resets++;
            if (entry.AppendixLatched) seenOn = true;
        }
        return resets;
    }

    private static string Key(int page, string readable) =>
        $"{page}|{Regex.Replace(readable, @"\s+", " ").Trim()}";

    private static string Trim(string value, int max = 90)
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
