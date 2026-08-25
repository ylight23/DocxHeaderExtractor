using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Eval;
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
/// <para>
/// Occurrence identity, not text identity. 054 has a reviewed <see cref="PdfReviewedOccurrenceBridge"/>
/// - its line indexes are used directly, no text matching at all. The other populations have no bridge
/// yet, so their gold lines are joined against the current extraction by
/// <see cref="PdfTextUtilities.CanonicalForMatch"/> - the same canonical-equality the bridge's own
/// deterministic proposal step uses - never by a looser, locally reinvented normalisation an earlier
/// version of this probe used (plain whitespace collapsing), which reported two 092 occurrences
/// "absent" on a text mismatch alone.
/// </para>
/// <para>
/// Fixing the join did NOT make those two occurrences full: it correctly resolves each to its unique
/// source line (page match, no ambiguity - `BENCH_A1_DEBUG=1` prints the resolved index and every
/// candidate whose line indexes fall nearby), and at that line neither `Covers` nor `touching` finds a
/// candidate. That is a real candidate-construction gap, not a join artifact - and it corrects a
/// specific earlier claim that a candidate for one of them already existed at a named rank; the named
/// candidate exists but its actual line indexes are elsewhere, a stale reference from before this
/// codebase's candidate ids last shifted. Candidate ids are discovery-order assignments and are not
/// stable evidence across a code change; only re-resolving against the current run is.
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

            // Canonical equality, not the probe's own text collapsing - the same join
            // PdfOccurrenceBridgeProposal already uses to place a reviewed bridge in the first place,
            // so a population without a bridge yet is at least joined the project's one established way.
            var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < snapshot.Lines.Count; index++)
                indexByKey.TryAdd(Key(snapshot.Lines[index].Page, snapshot.Lines[index].Text), index);

            int full = 0, partial = 0, absent = 0, selected = 0;
            var rows = new List<string>();
            foreach (var occurrence in occurrences)
            {
                // A reviewed occurrence bridge (054) already names its own line indexes - true
                // occurrence identity, no text matching needed or wanted. Everything else falls back
                // to the canonical-text join above.
                var required = occurrence.ResolvedIndexes ?? occurrence.Lines
                    .Select(line => indexByKey.TryGetValue(Key(line.Page, line.Text), out var index) ? index : -1)
                    .Where(index => index >= 0)
                    .ToArray();
                if (required.Count == 0)
                {
                    absent++;
                    rows.Add($"    no-source-line {Trim(occurrence.Label)}");
                    if (Environment.GetEnvironmentVariable("BENCH_A1_DEBUG") == "1")
                    {
                        var sought = occurrence.Lines.Select(l => Key(l.Page, l.Text));
                        rows.Add($"      sought: {string.Join(" | ", sought)}");
                        var page = occurrence.Lines.Count > 0 ? occurrence.Lines[0].Page : -1;
                        var nearby = snapshot.Lines
                            .Select((l, i) => (l, i))
                            .Where(x => x.l.Page == page)
                            .Select(x => $"[{x.i}]{Key(x.l.Page, x.l.Text)}={Trim(x.l.Text)}");
                        foreach (var n in nearby) rows.Add($"      have:   {n}");
                    }
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
                else
                {
                    absent++;
                    rows.Add($"    absent         {Trim(occurrence.Label)}");
                    if (Environment.GetEnvironmentVariable("BENCH_A1_DEBUG") == "1")
                    {
                        foreach (var index in required)
                            rows.Add($"      resolved index {index}: page={snapshot.Lines[index].Page} text={Trim(snapshot.Lines[index].Text)}");
                        var duplicates = occurrence.Lines
                            .SelectMany(line => snapshot.Lines
                                .Select((l, i) => (l, i))
                                .Where(x => x.l.Page == line.Page && PdfTextUtilities.CanonicalForMatch(x.l.Text) == PdfTextUtilities.CanonicalForMatch(line.Text)));
                        foreach (var (l, i) in duplicates)
                            rows.Add($"      same-key line [{i}]: {Trim(l.Text)}");
                        var nearbyCandidates = snapshot.Provenance.Values
                            .Where(p => p.LineIndexes.Any(i => required.Any(r => Math.Abs(i - r) <= 5)))
                            .Select(p => $"{p.CandidateSourceId} rank={(rankOf.TryGetValue(p.CandidateSourceId, out var r) ? r.ToString() : "unranked")} lines=[{string.Join(",", p.LineIndexes)}] kind={p.RepresentationKind}");
                        foreach (var nc in nearbyCandidates) rows.Add($"      nearby candidate: {nc}");
                    }
                }
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

    /// <summary>
    /// True occurrence identity: 054 has a reviewed bridge, so its line indexes are read directly and
    /// never re-derived by matching text against the current extraction.
    /// </summary>
    private static List<Occurrence> Bridge054()
    {
        var directory = Path.Combine(RepositoryRoot(), "keys", "occurrence-bridge");
        var path = Directory.Exists(directory) ? Directory.GetFiles(directory, "054_*.json").FirstOrDefault() : null;
        if (path is null) return [];
        var bridge = PdfReviewedOccurrenceBridge.Load(File.ReadAllText(path));
        return bridge.Occurrences
            .Where(occurrence => occurrence.ReviewStatus == "reviewed")
            .Select(occurrence => new Occurrence(
                occurrence.GoldText,
                [],
                occurrence.RequiredLines.Select(line => line.Index).ToArray()))
            .Where(occurrence => occurrence.ResolvedIndexes!.Count > 0)
            .ToList();
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

    private static Occurrence Single(int page, string text) => new(text, [(page, text)], null);

    /// <param name="ResolvedIndexes">
    /// Line indexes already known from a reviewed occurrence bridge (true occurrence identity). Null
    /// means no bridge exists yet for this population, so <see cref="Lines"/> is joined against the
    /// current extraction by canonical text equality instead.
    /// </param>
    private sealed record Occurrence(string Label, List<(int Page, string Text)> Lines, IReadOnlyList<int>? ResolvedIndexes);

    /// <summary>
    /// Canonical text equality - separators and case removed, same as
    /// <see cref="PdfOccurrenceBridgeProposal"/>'s own deterministic proposal step - never the
    /// probe's own weaker whitespace-collapsing. A join built any looser here reports a source line
    /// "absent" when it only looks different, exactly the failure this fix exists to close.
    /// </summary>
    private static string Key(int page, string text) =>
        $"{page}|{PdfTextUtilities.CanonicalForMatch(text)}";

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
