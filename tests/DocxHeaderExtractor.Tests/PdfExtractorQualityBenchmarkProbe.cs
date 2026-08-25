using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Extractor quality benchmark, A1: candidate recall and, kept strictly separate, rank and selection.
/// Model-free - it measures what candidate construction produced, before any model or output policy.
/// <para>
/// Candidate recall uses the project's occurrence definition: a candidate counts only if it covers
/// <em>every</em> semantic-bearing line of the reviewed occurrence. A block holding one line of a
/// three-line heading has not represented it. For single-line occurrences this coincides with "some
/// candidate contains the line"; for multi-line ones it does not, which is why the looser M11-A0
/// figure must not be quoted as candidate recall.
/// </para>
/// <para>
/// Rank and selection are reported beside recall and never folded into it. 032 and 091 already show
/// occurrences whose candidate exists and is not selected; one combined number would hide exactly the
/// first loss this project spent M10 learning to locate.
/// </para>
/// </summary>
public sealed class PdfExtractorQualityBenchmarkProbe
{
    private const int SelectedBudget = 160;

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_A1_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("scope=source occurrence -> candidate -> rank/selection. No model, no output policy, no hierarchy.");
        Line("candidate recall = a candidate covering EVERY semantic-bearing line of the occurrence");
        Line("");
        Line($"{"doc",-6} {"reviewed",9} {"full",6} {"partial",8} {"absent",7} {"rank<=160",10} {"recall",8}");

        var totals = new int[4];
        var selectedTotal = 0;
        foreach (var (stem, relative, occurrences) in Populations(corpus))
        {
            var path = Path.Combine(corpus, relative);
            if (!File.Exists(path) || occurrences.Count == 0)
            {
                Line($"{stem,-6} not measured");
                continue;
            }

            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
            var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
            var rankOf = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
                .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);

            var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < snapshot.Lines.Count; index++)
                indexByKey.TryAdd(Key(snapshot.Lines[index].Page, snapshot.Lines[index].Text), index);

            int full = 0, partial = 0, absent = 0, selected = 0;
            var rows = new List<string>();
            foreach (var occurrence in occurrences)
            {
                var required = occurrence.Lines
                    .Select(line => indexByKey.TryGetValue(Key(line.Page, line.Text), out var index) ? index : -1)
                    .Where(index => index >= 0)
                    .ToArray();
                if (required.Length == 0)
                {
                    absent++;
                    rows.Add($"    no-source-line {Trim(occurrence.Label)}");
                    continue;
                }

                var covering = ranked
                    .Where(item => snapshot.Provenance.TryGetValue(item.SourceId, out var p) && p.Covers(required))
                    .OrderBy(item => rankOf[item.SourceId])
                    .ToArray();
                if (covering.Length > 0)
                {
                    full++;
                    if (rankOf[covering[0].SourceId] <= SelectedBudget) selected++;
                    continue;
                }

                var touching = ranked.Any(item =>
                    snapshot.Provenance.TryGetValue(item.SourceId, out var p) &&
                    required.Any(p.LineIndexes.Contains));
                if (touching) { partial++; rows.Add($"    partial-only   {Trim(occurrence.Label)}"); }
                else { absent++; rows.Add($"    absent         {Trim(occurrence.Label)}"); }
            }

            totals[0] += occurrences.Count;
            totals[1] += full;
            totals[2] += partial;
            totals[3] += absent;
            selectedTotal += selected;
            Line($"{stem,-6} {occurrences.Count,9} {full,6} {partial,8} {absent,7} {selected,10} " +
                 $"{full / (double)occurrences.Count,8:P1}");
            foreach (var row in rows) Line(row);
        }

        Line("");
        Line($"{"ALL",-6} {totals[0],9} {totals[1],6} {totals[2],8} {totals[3],7} {selectedTotal,10} " +
             $"{totals[1] / (double)Math.Max(1, totals[0]),8:P1}");
        Line("");
        Line("Metric 1 candidate recall      = full / reviewed");
        Line("Metric 2 selection, kept apart = best full-coverage rank <= 160");
        Line("These are the reviewed populations that exist today, not a corpus-wide figure.");

        File.WriteAllText(output, report.ToString());
    }

    private static IEnumerable<(string Stem, string Relative, List<Occurrence> Occurrences)> Populations(string corpus)
    {
        yield return ("054", @"03_tai_chinh_ke_toan\054_IBRD_Information_Statement_FY25.docx", Bridge054());
        yield return ("092", @"07_system_generated\092_RFC9111_HTTP_Caching.docx",
            LabelFile("092-short-numbered-line-labels.v1.json"));
        yield return ("032", @"02_hop_dong_mua_sam\032_WB_Plant_TwoStage_2020.docx", CrossDocument("032"));
        yield return ("091", @"07_system_generated\091_RFC9110_HTTP_Semantics.docx", CrossDocument("091"));
    }

    private static List<Occurrence> Bridge054()
    {
        var directory = Path.Combine(RepositoryRoot(), "keys", "occurrence-bridge");
        var path = Directory.Exists(directory) ? Directory.GetFiles(directory, "054_*.json").FirstOrDefault() : null;
        if (path is null) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<Occurrence>();
        if (!document.RootElement.TryGetProperty("occurrences", out var occurrences)) return result;
        foreach (var occurrence in occurrences.EnumerateArray())
        {
            var page = occurrence.TryGetProperty("page", out var p) ? p.GetInt32() : 0;
            if (!occurrence.TryGetProperty("lines", out var lineArray)) continue;
            var lines = lineArray.EnumerateArray()
                .Select(line => (Page: page, Text: line.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""))
                .Where(line => PdfTextUtilities.CanonicalForMatch(line.Text).Length > 0)
                .ToList();
            if (lines.Count > 0) result.Add(new Occurrence(lines[0].Text, lines));
        }
        return result;
    }

    private static List<Occurrence> LabelFile(string name)
    {
        var path = Path.Combine(RepositoryRoot(), "eval", "manual-labels", name);
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Where(r => r.TryGetProperty("role", out var role) && role.GetString() == "outline_heading")
            .Select(r => Single(r.GetProperty("page").GetInt32(), r.GetProperty("readable").GetString() ?? ""))
            .ToList();
    }

    private static List<Occurrence> CrossDocument(string stem)
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
                .Select(r => Single(r.GetProperty("page").GetInt32(), r.GetProperty("readable").GetString() ?? ""))
                .ToList();
        }
        return [];
    }

    private static Occurrence Single(int page, string text) => new(text, [(page, text)]);

    private sealed record Occurrence(string Label, List<(int Page, string Text)> Lines);

    private static string Key(int page, string text) =>
        $"{page}|{Regex.Replace(PdfTextUtilities.Readable(text), @"\s+", " ").Trim()}";

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 62 ? single : single[..62] + "...";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
