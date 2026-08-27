using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.7-A diagnostic probe. Takes the reviewed heading occurrences that fall outside the budget
/// before scope can matter, and asks how their rank was formed - not whether the ranker is wrong.
/// <para>
/// The ranker orders by score descending, then escalation descending, then page, then source id.
/// Nothing with a lower score can stand above something with a higher one, so the count of
/// lower-scoring candidates ranked above each occurrence is carried as a parity check on this
/// probe's own arithmetic rather than as a finding.
/// </para>
/// <para>
/// The population above each occurrence is decomposed by representation, because one source line can
/// reach the ranking as a standalone block, a window and a supplement at once. If the candidates
/// filling the budget are largely re-representations of the same source occurrences, that is a
/// different owner from a heading that is simply outscored.
/// </para>
/// <para>
/// Budget 160 is an observation boundary here and nothing else. What budget would recover these is
/// not asked: 054 already taught that raising it is not a remedy, and the question is premature
/// before rank formation is understood.
/// </para>
/// </summary>
public sealed class PdfRankingFirstLossProbe
{
    private const int SelectedBudget = 160;

    private static readonly (string Stem, string Relative)[] Documents =
    [
        ("032", @"02_hop_dong_mua_sam\032_WB_Plant_TwoStage_2020.docx"),
        ("091", @"07_system_generated\091_RFC9110_HTTP_Semantics.docx"),
    ];

    // Recorded separately and never mixed into the eleven: 054's deferred cases are a comparison,
    // not part of this population, and folding them in would silently change the denominator.
    private static readonly (string Stem, string Relative, string[] Needles)[] Comparison =
    [
        ("054", @"03_tai_chinh_ke_toan\054_IBRD_Information_Statement_FY25.docx",
            ["AVAILABILITY OF INFORMATION", "SUMMARY INFORMATION", "APPENDIX"]),
    ];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_7_A_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("contract=passive_rank_formation_audit no_model no_counterfactual budget_unchanged");
        Line("ranker order = score desc, escalation desc, page asc, sourceId asc (no tier in the ordering)");

        var reviewed = ReviewedHeadings();

        foreach (var (stem, relative) in Documents)
        {
            var path = Path.Combine(corpus, relative);
            Line("");
            Line($"================ {stem} ================");
            if (!File.Exists(path)) { Line("document not found"); continue; }
            if (!reviewed.TryGetValue(stem, out var headings)) { Line("no reviewed headings"); continue; }

            Audit(path, headings.Select(h => Key(h.Page, h.Readable)).ToArray(), Line, onlyUnselected: true);
        }

        foreach (var (stem, relative, needles) in Comparison)
        {
            var path = Path.Combine(corpus, relative);
            Line("");
            Line($"================ {stem} (comparison only, not in the eleven) ================");
            if (!File.Exists(path)) { Line("document not found"); continue; }
            AuditByText(path, needles, Line);
        }

        File.WriteAllText(output, report.ToString());
    }

    private static void Audit(string path, IReadOnlyList<string> lineKeys, Action<string> Line, bool onlyUnselected)
    {
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
        var rankOf = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
            .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);

        var firstLineOf = snapshot.CandidateBlocks.ToDictionary(
            b => b.Id,
            b => b.Lines.Count == 0 ? "" : Key(b.Lines[0].Page, PdfTextUtilities.Readable(b.Lines[0].Text)),
            StringComparer.Ordinal);

        foreach (var key in lineKeys)
        {
            var block = snapshot.CandidateBlocks
                .Where(b => b.Lines.Any(l => Key(l.Page, PdfTextUtilities.Readable(l.Text)) == key))
                .OrderBy(b => rankOf.GetValueOrDefault(b.Id, int.MaxValue))
                .FirstOrDefault();
            if (block is null) continue;
            var rank = rankOf.GetValueOrDefault(block.Id, -1);
            if (onlyUnselected && rank > 0 && rank <= SelectedBudget) continue;
            Describe(ranked, rank, block.Id, contexts, snapshot, firstLineOf, key, Line);
        }
    }

    private static void AuditByText(string path, IReadOnlyList<string> needles, Action<string> Line)
    {
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
        var rankOf = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
            .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
        var firstLineOf = snapshot.CandidateBlocks.ToDictionary(
            b => b.Id,
            b => b.Lines.Count == 0 ? "" : Key(b.Lines[0].Page, PdfTextUtilities.Readable(b.Lines[0].Text)),
            StringComparer.Ordinal);

        foreach (var needle in needles)
        {
            var block = snapshot.CandidateBlocks
                .Where(b => PdfTextUtilities.Readable(b.DisplayText)
                    .Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => rankOf.GetValueOrDefault(b.Id, int.MaxValue))
                .FirstOrDefault();
            if (block is null) { Line($"  {needle}: no candidate"); continue; }
            Describe(ranked, rankOf.GetValueOrDefault(block.Id, -1), block.Id, contexts, snapshot,
                firstLineOf, needle, Line);
        }
    }

    private static void Describe(
        IReadOnlyList<RankedCandidate> ranked,
        int rank,
        string blockId,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        PdfCandidateRankingSnapshot snapshot,
        IReadOnlyDictionary<string, string> firstLineOf,
        string label,
        Action<string> Line)
    {
        var self = ranked.FirstOrDefault(item => item.SourceId == blockId);
        if (self is null) { Line($"  {Trim(label)}: not ranked"); return; }
        var above = ranked.Take(Math.Max(0, rank - 1)).ToArray();

        var higher = above.Count(item => item.CandidateScore > self.CandidateScore + 1e-9);
        var equal = above.Count(item => Math.Abs(item.CandidateScore - self.CandidateScore) <= 1e-9);
        var lower = above.Count(item => item.CandidateScore < self.CandidateScore - 1e-9);

        var distinctFirstLines = above.Select(item => firstLineOf.GetValueOrDefault(item.SourceId, item.SourceId))
            .Distinct(StringComparer.Ordinal).Count();
        var kinds = above
            .Select(item => snapshot.Provenance.TryGetValue(item.SourceId, out var p)
                ? p.RepresentationKind.ToString() : "unknown")
            .GroupBy(x => x).OrderByDescending(g => g.Count()).ToArray();
        var scopes = above
            .Select(item => contexts.TryGetValue(item.SourceId, out var c) ? c.Source.StructuralScope : "-")
            .GroupBy(x => x).OrderByDescending(g => g.Count()).ToArray();

        Line("");
        Line($"  {Trim(label, 60)}");
        Line($"    block={blockId} score={self.CandidateScore:F2} escalation={self.EscalationScore:F2} " +
             $"tier={self.Tier} rank={rank}");
        Line($"    positive=[{string.Join(",", self.PositiveSignals)}]");
        Line($"    negative=[{string.Join(",", self.NegativeSignals)}]");
        Line($"    ambiguity=[{string.Join(",", self.AmbiguitySignals)}]");
        Line($"    above: total={above.Length} higherScore={higher} equalScore={equal} lowerScore={lower}");
        Line($"    above: distinct first-line identities={distinctFirstLines} " +
             $"(re-representation inflation = {above.Length - distinctFirstLines})");
        Line($"    above by representation: {string.Join(" ", kinds.Select(g => $"{g.Key}={g.Count()}"))}");
        Line($"    above by scope: {string.Join(" ", scopes.Take(5).Select(g => $"{g.Key}={g.Count()}"))}");
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

    private static string Key(int page, string readable) =>
        $"{page}|{Regex.Replace(readable, @"\s+", " ").Trim()}";

    private static string Trim(string value, int max = 70)
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
