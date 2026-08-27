using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.7-D1. Measures how much of the candidate budget is spent on candidates that the existing
/// output policy would already refuse on the scope they already carry. Observation only: no
/// counterfactual, no reordering, no change to either stage.
/// <para>
/// The boundary that makes this measurable is narrow and deliberate. A slot counts as structurally
/// non-emittable only when a deterministic fact available <em>before</em> the model - the candidate's
/// scope - falls in the output policy's own excluded set. A candidate that reaches the model and is
/// then rejected does not count: treating it as waste would be using an outcome that has not happened
/// yet to decide what should have been selected, which is how a selection stage learns to chase its
/// own downstream noise.
/// </para>
/// <para>
/// The excluded set is read from the policy rather than restated here, so the two cannot drift.
/// </para>
/// <para>
/// A high figure is not by itself a defect. Some of these scopes are wrong because of the appendix
/// and quote leaks already recorded, and a candidate carrying a wrong scope would have occupied the
/// same slot under the right one - scope reaches rank only through the `table_scope` penalty. What
/// the number establishes is only how much of the budget is, in fact, unshippable at the moment it is
/// spent.
/// </para>
/// </summary>
public sealed class PdfSelectionBudgetWasteProbe
{
    private const int SelectedBudget = 160;

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_7_D1_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var sweep = Environment.GetEnvironmentVariable("M10_7_D1_SWEEP") == "1";

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var focus = new[] { "091", "032", "054", "076", "092" };

        var paths = Directory.EnumerateFiles(corpus, "*.docx", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Where(path => sweep || focus.Any(stem =>
                Path.GetFileName(path).StartsWith(stem + "_", StringComparison.Ordinal)))
            .ToArray();

        var rows = paths.Select(Measure).Where(row => row is not null).Select(row => row!).ToArray();

        var report = new
        {
            contract = "observation_only; a slot counts as wasted only when its current scope is in the output policy's excluded set",
            excludedScopes = PdfOutputDecisionPolicy.ExcludedScopes,
            budget = SelectedBudget,
            documents = rows.Length,
            rows = rows.OrderByDescending(r => r.WastedFraction).ToArray(),
        };

        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(output, JsonSerializer.Serialize(report, options));

        var text = new StringBuilder();
        text.AppendLine($"{"document",-46} {"cand",6} {"sel",5} {"wasted",7} {"pct",6} {"nextOk",7} {"ok161_320",10}");
        foreach (var row in rows.OrderByDescending(r => r.WastedFraction))
            text.AppendLine($"{row.Document,-46} {row.Candidates,6} {row.Selected,5} {row.WastedSlots,7} " +
                            $"{row.WastedFraction,6:P0} {row.FirstEligibleRankBelowBudget,7} {row.EligibleJustBelowBudget,10}");
        File.WriteAllText(Path.ChangeExtension(output, ".txt"), text.ToString());
    }

    private static Row? Measure(string path)
    {
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        if (snapshot.CandidateBlocks.Count == 0) return null;
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);

        string Scope(string id) => contexts.TryGetValue(id, out var c) ? c.Source.StructuralScope : "-";
        bool Excluded(string id) => Array.IndexOf(PdfOutputDecisionPolicy.ExcludedScopes, Scope(id)) >= 0;

        var selected = ranked.Take(SelectedBudget).ToArray();
        var wasted = selected.Count(item => Excluded(item.SourceId));
        var below = ranked.Skip(SelectedBudget).ToArray();
        var firstEligible = below
            .Select((item, index) => (item, Rank: SelectedBudget + index + 1))
            .FirstOrDefault(x => !Excluded(x.item.SourceId));

        return new Row(
            Path.GetFileNameWithoutExtension(path),
            snapshot.CandidateBlocks.Count,
            selected.Length,
            wasted,
            selected.Length == 0 ? 0 : wasted / (double)selected.Length,
            firstEligible.item is null ? -1 : firstEligible.Rank,
            below.Take(SelectedBudget).Count(item => !Excluded(item.SourceId)),
            selected.GroupBy(item => Scope(item.SourceId))
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
    }

    private sealed record Row(
        string Document,
        int Candidates,
        int Selected,
        int WastedSlots,
        double WastedFraction,
        int FirstEligibleRankBelowBudget,
        int EligibleJustBelowBudget,
        IReadOnlyDictionary<string, int> SelectedByScope);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
