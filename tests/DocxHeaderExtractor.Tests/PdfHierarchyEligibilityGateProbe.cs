using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M11-A0. Establishes whether a population exists in which hierarchy can be assessed at all: a
/// reviewed source occurrence that produces a candidate, is selected, and carries a scope the output
/// policy allows.
/// <para>
/// This measures an <em>upper bound</em>, and says so rather than implying more. Validation is a model
/// lane, so no offline run can report which of these would actually be validated - the true eligible
/// population is a subset of what is counted here, and how much smaller cannot be known without a
/// live pass.
/// </para>
/// <para>
/// The point of running it first is cheap refutation: if the upper bound is already near zero, a
/// hierarchy milestone has nothing to measure and should not be opened, and no model spend is needed
/// to find that out.
/// </para>
/// </summary>
public sealed class PdfHierarchyEligibilityGateProbe
{
    private const int SelectedBudget = 160;

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M11_A0_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("contract=upper_bound_only; validation is a model lane and is not simulated here");
        Line($"stages measured: reviewed occurrence -> candidate -> selected@{SelectedBudget} -> scope allows emission");

        foreach (var (stem, relative, occurrences) in Populations(corpus))
        {
            var path = Path.Combine(corpus, relative);
            Line("");
            Line($"================ {stem} ({occurrences.Count} reviewed occurrences) ================");
            if (!File.Exists(path)) { Line("document not found"); continue; }

            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
            var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
            var rankOf = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
            var selected = ranked.Take(SelectedBudget).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);

            var byLine = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var block in snapshot.CandidateBlocks)
                foreach (var line in block.Lines)
                {
                    var key = Key(line.Page, PdfTextUtilities.Readable(line.Text));
                    if (!byLine.TryGetValue(key, out var list)) byLine[key] = list = [];
                    list.Add(block.Id);
                }

            var hasCandidate = 0;
            var isSelected = 0;
            var scopeAllows = 0;
            foreach (var (page, readable) in occurrences)
            {
                var key = Key(page, readable);
                if (!byLine.TryGetValue(key, out var list)) continue;
                hasCandidate++;
                var best = list.OrderBy(id => rankOf.GetValueOrDefault(id, int.MaxValue)).First();
                if (!selected.Contains(best)) continue;
                isSelected++;
                var scope = contexts.TryGetValue(best, out var c) ? c.Source.StructuralScope : "-";
                if (Array.IndexOf(PdfOutputDecisionPolicy.ExcludedScopes, scope) < 0) scopeAllows++;
            }

            Line($"  produces a candidate       {hasCandidate,4} / {occurrences.Count}");
            Line($"  selected at budget         {isSelected,4} / {occurrences.Count}");
            Line($"  scope allows emission      {scopeAllows,4} / {occurrences.Count}   <- upper bound for hierarchy");
        }

        File.WriteAllText(output, report.ToString());
    }

    private static IEnumerable<(string Stem, string Relative, List<(int Page, string Readable)> Occurrences)>
        Populations(string corpus)
    {
        // 092: the reviewed outline headings from the short-numbered labelling.
        var labels092 = ReadLabels(Path.Combine(RepositoryRoot(), "eval", "manual-labels",
            "092-short-numbered-line-labels.v1.json"), "items");
        yield return ("092", @"07_system_generated\092_RFC9111_HTTP_Caching.docx", labels092);

        // 032 and 091: the reviewed outline headings from the cross-document sample.
        foreach (var (stem, relative) in new[]
                 {
                     ("032", @"02_hop_dong_mua_sam\032_WB_Plant_TwoStage_2020.docx"),
                     ("091", @"07_system_generated\091_RFC9110_HTTP_Semantics.docx"),
                 })
            yield return (stem, relative, CrossDocumentLabels(stem));

        // 054: the reviewed occurrence bridge, the only gold in this project that is occurrence-safe.
        yield return ("054", @"03_tai_chinh_ke_toan\054_IBRD_Information_Statement_FY25.docx", Bridge054());
    }

    private static List<(int Page, string Readable)> ReadLabels(string path, string arrayName)
    {
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(arrayName).EnumerateArray()
            .Where(r => r.TryGetProperty("role", out var role) && role.GetString() == "outline_heading")
            .Select(r => (r.GetProperty("page").GetInt32(), r.GetProperty("readable").GetString() ?? ""))
            .ToList();
    }

    private static List<(int Page, string Readable)> CrossDocumentLabels(string stem)
    {
        var path = Path.Combine(RepositoryRoot(), "eval", "manual-labels",
            "m105b-cross-document-review-labels.v1.json");
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var doc in document.RootElement.GetProperty("documents").EnumerateArray())
        {
            if (doc.GetProperty("stem").GetString() != stem) continue;
            return doc.GetProperty("sample").EnumerateArray()
                .Where(r => r.TryGetProperty("role", out var role) && role.GetString() == "outline_heading")
                .Select(r => (r.GetProperty("page").GetInt32(), r.GetProperty("readable").GetString() ?? ""))
                .ToList();
        }
        return [];
    }

    private static List<(int Page, string Readable)> Bridge054()
    {
        var directory = Path.Combine(RepositoryRoot(), "keys", "occurrence-bridge");
        if (!Directory.Exists(directory)) return [];
        var path = Directory.GetFiles(directory, "054_*.json").FirstOrDefault();
        if (path is null) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<(int, string)>();
        if (!document.RootElement.TryGetProperty("occurrences", out var occurrences)) return result;
        foreach (var occurrence in occurrences.EnumerateArray())
        {
            if (!occurrence.TryGetProperty("lines", out var lines)) continue;
            var first = lines.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Undefined) continue;
            var page = occurrence.TryGetProperty("page", out var p) ? p.GetInt32() : 0;
            var text = first.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (page > 0 && text.Length > 0) result.Add((page, text));
        }
        return result;
    }

    private static string Key(int page, string readable) =>
        $"{page}|{Regex.Replace(readable, @"\s+", " ").Trim()}";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
