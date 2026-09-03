using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.3-B1 diagnostic probe. Asks what the TableLike rule is classifying on 092 and what happens
/// downstream to what it marks. It starts at the annotation rather than at scope, because A2 showed
/// scope is a consequence of the annotation rather than a cause. It changes nothing and repairs
/// nothing.
/// <para>
/// The population is measured on the corrected-scope counterfactual, not on the shipped state: the
/// old short-numbered false-positive figure was measured while pages 5-31 were labelled appendix,
/// and that population no longer exists once the scope is corrected.
/// </para>
/// </summary>
public sealed class PdfTableLikeFirstLossProbe
{
    private const int SelectedBudget = 160;
    private const string WithheldTransition = "b43";

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_3_B1_REPORT");
        if (output is null) return;

        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\07_system_generated\092_RFC9111_HTTP_Caching.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var blocks = snapshot.CandidateBlocks;

        // The corrected-scope world: the reviewed false appendix entry withheld, nothing else changed.
        var contexts = PdfCandidateContextBuilder.Build(blocks, snapshot.Annotations,
            withheldAppendixEntries: new HashSet<string>(StringComparer.Ordinal) { WithheldTransition });
        var ranked = PdfCandidateRanker.Rank(blocks, contexts);
        var rank = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
            .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
        var byId = ranked.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var selected = ranked.Take(SelectedBudget).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("population=corrected_scope_counterfactual basis=reviewed_false_scope_transition usesModel=false");
        Line($"lines={snapshot.Annotations.Count} candidateBlocks={blocks.Count} budget={SelectedBudget}");

        Line("");
        Line("-- which branch of the rule fired, over every line");
        var lineRules = snapshot.Annotations
            .Select(a => (a, Rule: PdfLineBlockFilter.ClassifyTableLine(a.Line.Text)))
            .ToArray();
        foreach (var group in lineRules.GroupBy(x => x.Rule ?? "not_table_like").OrderByDescending(g => g.Count()))
            Line($"{group.Key,-20} {group.Count()}");

        Line("");
        Line("-- blocks whose every line the rule marked (these are the ones scope calls table)");
        var blockRules = blocks.Select(block =>
        {
            var rules = block.Lines.Select(line => PdfLineBlockFilter.ClassifyTableLine(line.Text)).ToArray();
            var all = rules.Length > 0 && rules.All(r => r is not null);
            var dominant = rules.Where(r => r is not null)
                .GroupBy(r => r!).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "none";
            return (Block: block, AllTableLike: all, Dominant: dominant);
        }).ToArray();
        var allTable = blockRules.Where(x => x.AllTableLike).ToArray();
        Line($"blocks with every line marked: {allTable.Length} of {blocks.Count}");
        foreach (var group in allTable.GroupBy(x => x.Dominant).OrderByDescending(g => g.Count()))
            Line($"  dominant branch {group.Key,-18} {group.Count()}");

        Line("");
        Line("-- what happens downstream to those blocks");
        var scoped = allTable.Select(x => x.Block.Id)
            .Where(id => contexts.ContainsKey(id)).ToArray();
        var scopeTable = scoped.Count(id => contexts[id].Source.StructuralScope == "table");
        var penalised = scoped.Count(id => byId.TryGetValue(id, out var r) && r.NegativeSignals.Contains("table_scope"));
        Line($"scope table:                  {scopeTable}");
        Line($"scope something else:         {scoped.Length - scopeTable}");
        Line($"carries table_scope penalty:  {penalised}");
        Line($"score exactly 0.00:           {scoped.Count(id => byId.TryGetValue(id, out var r) && r.CandidateScore <= 0.0001)}");
        Line($"still selected at budget:     {scoped.Count(selected.Contains)}");

        Line("");
        Line("-- rank distribution of the marked blocks");
        foreach (var group in scoped.GroupBy(id => Bucket(rank.GetValueOrDefault(id, -1))).OrderBy(g => g.Key))
            Line($"{group.Key,-14} {group.Count()}");

        Line("");
        Line("-- the short_numbered branch, block by block (the branch the four headings fall in)");
        var shortNumbered = allTable.Where(x => x.Dominant == "short_numbered")
            .OrderBy(x => x.Block.Page).ToArray();
        Line($"count: {shortNumbered.Length}");
        foreach (var item in shortNumbered)
        {
            var id = item.Block.Id;
            var r = byId.GetValueOrDefault(id);
            Line($"  {id,-6} p{item.Block.Page,-3} rank={rank.GetValueOrDefault(id, -1),-4} " +
                 $"score={(r is null ? double.NaN : r.CandidateScore):F2} " +
                 $"scope={contexts[id].Source.StructuralScope,-16} {Trim(item.Block.DisplayText)}");
        }

        Line("");
        Line("-- reviewed line labels joined to the corrected-scope population");
        // The labels are line-level and the rule is line-level, so this labelling survives the scope
        // correction unchanged. What did not survive is any downstream rate measured under the leak.
        var labelPath = Path.Combine(RepositoryRoot(), "eval", "manual-labels",
            "092-short-numbered-line-labels.v1.json");
        if (!File.Exists(labelPath))
        {
            Line("label artifact missing; not measured");
        }
        else
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(labelPath));
            var labels = document.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => (
                    Page: item.GetProperty("page").GetInt32(),
                    Readable: item.GetProperty("readable").GetString() ?? "",
                    Role: item.GetProperty("role").GetString() ?? "",
                    OutlineEligible: item.GetProperty("shouldBeOutlineEligible").GetString() == "yes"))
                .ToArray();
            Line($"labelled lines: {labels.Length}");
            foreach (var group in labels.GroupBy(l => l.Role).OrderByDescending(g => g.Count()))
                Line($"  {group.Key,-32} {group.Count()}");

            // Join each labelled line to the candidate block that carries it.
            var blockByLine = new Dictionary<string, PdfSemanticBlock>(StringComparer.Ordinal);
            foreach (var block in blocks)
                foreach (var line in block.Lines)
                {
                    var key = LineKey(line.Page, PdfTextUtilities.Readable(line.Text));
                    if (!blockByLine.ContainsKey(key)) blockByLine[key] = block;
                }

            var eligible = labels.Where(l => l.OutlineEligible).ToArray();
            Line("");
            Line($"lines a reviewer called outline-eligible: {eligible.Length}");
            var joined = 0;
            var selectedCount = 0;
            var penalisedCount = 0;
            var zeroScore = 0;
            foreach (var label in eligible)
            {
                if (!blockByLine.TryGetValue(LineKey(label.Page, label.Readable), out var block)) continue;
                joined++;
                var candidate = byId.GetValueOrDefault(block.Id);
                var scope = contexts.TryGetValue(block.Id, out var context)
                    ? context.Source.StructuralScope : "?";
                if (candidate is not null && candidate.NegativeSignals.Contains("table_scope")) penalisedCount++;
                if (candidate is not null && candidate.CandidateScore <= 0.0001) zeroScore++;
                if (selected.Contains(block.Id)) selectedCount++;
                Line($"  p{label.Page,-3} {block.Id,-12} scope={scope,-16} " +
                     $"score={(candidate is null ? double.NaN : candidate.CandidateScore),5:F2} " +
                     $"rank={rank.GetValueOrDefault(block.Id, -1),-5} selected={selected.Contains(block.Id),-5} " +
                     $"{Trim(label.Readable)}");
            }
            Line($"joined to a candidate block: {joined}/{eligible.Length}");
            Line($"  carrying table_scope penalty: {penalisedCount}");
            Line($"  scoring 0.00:                 {zeroScore}");
            Line($"  selected at budget:           {selectedCount}");

            Line("");
            Line("-- what a blanket unmarking of this branch would release");
            foreach (var group in labels.Where(l => !l.OutlineEligible)
                         .GroupBy(l => l.Role).OrderByDescending(g => g.Count()))
                Line($"  {group.Key,-32} {group.Count()}");
        }

        Line("");
        Line("-- the four reviewed heading occurrences, full chain");
        foreach (var id in new[] { "b45", "b53", "b58", "b61" })
        {
            var block = blocks.FirstOrDefault(b => b.Id == id);
            if (block is null) { Line($"  {id}: not among candidates"); continue; }
            var rules = block.Lines.Select(line => PdfLineBlockFilter.ClassifyTableLine(line.Text)).ToArray();
            var r = byId.GetValueOrDefault(id);
            Line($"  {id} p{block.Page} {Trim(block.DisplayText)}");
            Line($"     rule branch per line: [{string.Join(",", rules.Select(x => x ?? "none"))}]");
            Line($"     scope={contexts[id].Source.StructuralScope} " +
                 $"neg=[{(r is null ? "?" : string.Join(",", r.NegativeSignals))}] " +
                 $"score={(r is null ? double.NaN : r.CandidateScore):F2} " +
                 $"rank={rank.GetValueOrDefault(id, -1)} selected={selected.Contains(id)}");
        }

        File.WriteAllText(output, report.ToString());
    }

    private static string LineKey(int page, string readable) =>
        $"{page}|{Regex.Replace(readable, @"\s+", " ").Trim()}";

    private static string Bucket(int rank) => rank switch
    {
        < 0 => "unranked",
        <= 160 => "001-160",
        <= 320 => "161-320",
        <= 480 => "321-480",
        _ => "481+",
    };

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
