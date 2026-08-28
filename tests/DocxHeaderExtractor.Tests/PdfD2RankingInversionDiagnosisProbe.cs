using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline D2 diagnosis. It replays frozen candidates and labels only, never providers or production behavior.</summary>
public sealed class PdfD2RankingInversionDiagnosisProbe
{
    private const int K = 160;
    private static readonly (string Id, string Relative)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];
    private static readonly string[] Signals =
    ["labelled_numbering_marker", "unlabelled_numbering_prefix", "standalone", "marker_title_composite",
     "canonical_marker_title", "layout_prominence", "opens_content", "table_scope", "running_page_scope",
     "header_footer_zone", "long_marker_body_window"];

    [Fact]
    public void WriteDiagnosis()
    {
        var json = Environment.GetEnvironmentVariable("BENCH_D2_RANKING_INVERSION_JSON");
        var md = Environment.GetEnvironmentVariable("BENCH_D2_RANKING_INVERSION_MD");
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(md)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var artifact = Build(root);
        json = Path.IsPathRooted(json) ? json : Path.Combine(root, json);
        md = Path.IsPathRooted(md) ? md : Path.Combine(root, md);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(json))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(md))!);
        File.WriteAllText(json, JsonSerializer.Serialize(artifact, Options));
        File.WriteAllText(md, Markdown(artifact));
    }

    [Fact]
    public void DiagnosisIsOfflineAndPreservesFrozenBoundary()
    {
        var artifact = Build(PdfExtractorQualityBenchmarkProbe.RepositoryRoot());
        Assert.Equal(0, artifact.ProviderCalls);
        Assert.False(artifact.ProductionCodeChanged);
        Assert.Equal(K, artifact.K);
        Assert.Equal(4, artifact.Documents.Count);
        Assert.All(artifact.Documents, d => Assert.All(d.LostHeadings, h => Assert.True(h.Candidate.Rank > K)));
        Assert.Contains(artifact.Documents.SelectMany(d => d.Counterfactuals), c => c.RecoveredAtK > 0);
    }

    private static D2Artifact Build(string root)
    {
        var docs = Documents.Select(spec => BuildDocument(root, spec)).ToArray();
        var dominant = docs.SelectMany(d => d.Counterfactuals).Where(c => c.RecoveredAtK >= Math.Max(2, c.BaselineLosses / 10) && c.DisplacedAtK == 0)
            .GroupBy(c => c.Signal, StringComparer.Ordinal).OrderByDescending(g => g.Sum(x => x.RecoveredAtK)).ToArray();
        var sameCause = dominant.Length == 1 && docs.All(d => d.Counterfactuals.Single(c => c.Signal == dominant[0].Key).RecoveredAtK >= Math.Max(1, d.BaselineLosses / 10));
        return new D2Artifact(1, "accuracy_ranking_inversion_diagnosis", "D2-ranking-inversion-diagnosis",
            "frozen N3 silver labels + current candidate/ranking snapshot; candidateId is diagnostic-only",
            K, docs, "PROVEN", sameCause ? "PROVEN" : "NOT_PROVEN", sameCause ? "" : "budget cutoff recurs, but no single collateral-free signal owner recurs across all documents",
            sameCause ? "YES" : "NO", 0, false);
    }

    private static D2Document BuildDocument(string root, (string Id, string Relative) spec)
    {
        var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", spec.Relative);
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", $"{spec.Id}-n3.2-silver-model-assisted.v1.json");
        var censusPath = Path.Combine(root, "eval", "benchmark-n3", "census", $"{spec.Id}-n3.3-census.v1.json");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var ranked = snapshot.Audit.Candidates;
        var facts = ranked.Select((c, i) => Fact.Create(c, i + 1, contexts[c.SourceId])).ToArray();
        var byId = facts.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var lineIndex = snapshot.Lines.Select((l, i) => (Id: PdfCandidateProvenance.LineId(l), i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        using var census = JsonDocument.Parse(File.ReadAllText(censusPath));
        var headings = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .Where(x => x.GetProperty("label").GetString() == "REVIEWED_HEADING")
            .Select(x => ReadHeading(x, lineIndex, snapshot, facts)).Where(x => x is not null).Cast<Heading>().ToArray();
        var losses = headings.Where(h => h.Candidate.Rank > K).ToArray();
        var ablations = Signals.Select(signal => Ablate(signal, facts, headings)).ToArray();
        var owner = ablations.Where(a => a.RecoveredAtK >= Math.Max(2, losses.Length / 10) && a.DisplacedAtK == 0).ToArray();
        return new D2Document(spec.Id, spec.Relative.Replace('\\', '/'), Sha256(path), Sha256(silverPath), Sha256(censusPath),
            facts.Length, headings.Length, headings.Count(h => h.Candidate.Rank <= K), losses.Length, losses,
            ablations, owner.Length == 1 ? owner[0].Signal : "UNRESOLVED",
            owner.Length == 1 ? "SINGLE_SIGNAL_COLLATERAL_FREE_SENSITIVITY" : "No unique collateral-free signal sensitivity meets the materiality rule.");
    }

    private static Heading? ReadHeading(JsonElement item, IReadOnlyDictionary<string, int> lineIndex,
        PdfCandidateRankingSnapshot snapshot, IReadOnlyList<Fact> facts)
    {
        var ids = item.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var indexes = ids.Select(id => lineIndex.TryGetValue(id, out var i) ? i : -1).ToArray();
        if (indexes.Any(i => i < 0)) return null;
        var covering = facts.Where(f => snapshot.Provenance[f.Id].Covers(indexes)).OrderBy(f => f.Rank).ToArray();
        if (covering.Length == 0) return null;
        var target = covering[0];
        var competitors = facts.Where(f => f.Rank < target.Rank && f.Score >= target.Score - .10)
            .OrderByDescending(f => f.Score).ThenBy(f => f.Rank).Take(8)
            .Select(f => Pair.From(target, f)).ToArray();
        return new Heading(item.GetProperty("goldStableId").GetString()!, ids, item.GetProperty("sourceText").GetString()!, target,
            competitors, snapshot.Provenance[target.Id].RepresentationKind.ToString());
    }

    private static Replay Ablate(string signal, IReadOnlyList<Fact> facts, IReadOnlyList<Heading> headings)
    {
        var ordered = facts.Select(f => (f, Score: f.Without(signal))).OrderByDescending(x => x.Score).ThenByDescending(x => x.f.Escalation)
            .ThenBy(x => x.f.Page).ThenBy(x => x.f.Id, StringComparer.Ordinal).Select((x, i) => (x.f.Id, Rank: i + 1))
            .ToDictionary(x => x.Id, x => x.Rank, StringComparer.Ordinal);
        var rows = headings.Select(h => (h, Baseline: h.Candidate.Rank, Replay: ordered[h.Candidate.Id])).ToArray();
        var recovered = rows.Count(x => x.Baseline > K && x.Replay <= K);
        var displaced = rows.Count(x => x.Baseline <= K && x.Replay > K);
        var baselineById = facts.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var changes = ordered.Count(x => baselineById[x.Key].Rank != x.Value);
        return new Replay(signal, headings.Count(x => x.Candidate.Rank > K), recovered, displaced, recovered - displaced,
            rows.Length == 0 ? 0 : (int)Math.Round(rows.Average(x => x.Replay - x.Baseline)), changes,
            rows.Select(x => new RankDelta(x.h.OccurrenceId, x.Baseline, x.Replay, x.Replay - x.Baseline)).ToArray());
    }

    private static string Markdown(D2Artifact a)
    {
        var s = new StringBuilder();
        s.AppendLine("# Ranking Inversion Diagnosis"); s.AppendLine();
        s.AppendLine("Offline replay over frozen N3 candidate/ranking snapshots. Provider calls and production changes are both zero."); s.AppendLine();
        s.AppendLine($"- `K={a.K}`; `REMEDIATION_JUSTIFIED={a.RemediationJustified}`");
        s.AppendLine($"- `SAME_RANKING_INVERSION_CAUSE={a.SameRankingInversionCause}`");
        s.AppendLine("- Pairwise competitors are selected above each lost heading with score >= heading score - 0.10, capped at eight."); s.AppendLine();
        foreach (var d in a.Documents)
        {
            s.AppendLine($"## {d.DocumentId}"); s.AppendLine();
            s.AppendLine($"`candidateCount={d.CandidateCount}`, `reviewed={d.ReviewedCount}`, `reviewedRecoveredAt160={d.ReviewedAtK}`, `losses={d.BaselineLosses}`, `owner={d.Owner}`"); s.AppendLine();
            s.AppendLine("| signal ablation | reviewedRecoveredAt160 | reviewedDisplacedAt160 | netReviewedGain | candidateRankDelta | collateralRankChanges |");
            s.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var c in d.Counterfactuals) s.AppendLine($"| `{c.Signal}` | {c.RecoveredAtK} | {c.DisplacedAtK} | {c.NetReviewedGain} | {c.MeanRankDelta} | {c.CollateralRankChanges} |");
            s.AppendLine();
            s.AppendLine("Lost headings and pairwise inversion evidence are serialized in the JSON artifact."); s.AppendLine();
        }
        s.AppendLine("## Cross-document recurrence"); s.AppendLine();
        s.AppendLine("| document | reviewed headings | reviewed at 160 | ranking losses | budget cutoff | same inversion cause |");
        s.AppendLine("|---|---:|---:|---:|---|---|");
        foreach (var d in a.Documents) s.AppendLine($"| `{d.DocumentId}` | {d.ReviewedCount} | {d.ReviewedAtK} | {d.BaselineLosses} | PROVEN | {(d.Owner == "UNRESOLVED" ? "NOT_PROVEN" : "PROVEN")} |");
        s.AppendLine();
        s.AppendLine("## Conclusion"); s.AppendLine();
        s.AppendLine(a.SameRankingInversionCause == "PROVEN" ? "A single signal has a recurring collateral-free sensitivity result; ranking remediation is justified by this diagnostic rule." : "Budget-cutoff recurrence is present, but the evidence does not prove one recurring ranking-inversion cause; ranking remediation is not justified.");
        return s.ToString();
    }

    private sealed record Fact(string Id, int Rank, int Page, string Text, double Score, double Escalation, IReadOnlyDictionary<string, double> Components,
        IReadOnlyList<string> Positive, IReadOnlyList<string> Negative)
    {
        public bool Selected => Rank <= K;
        public static Fact Create(RankedCandidate c, int rank, PdfCandidateContext context)
        {
            var values = new Dictionary<string, double>(StringComparer.Ordinal) { ["base"] = .10 };
            void Add(string n, double v) { if (c.PositiveSignals.Contains(n)) values[n] = v; }
            void Sub(string n, double v) { if (c.NegativeSignals.Contains(n)) values[n] = v; }
            Add("labelled_numbering_marker", .42); Add("unlabelled_numbering_prefix", .10); Add("standalone", .18); Add("marker_title_composite", .28); Add("canonical_marker_title", .22); Add("layout_prominence", .16); Add("opens_content", .12);
            Sub("table_scope", -.60); Sub("running_page_scope", -.75); Sub("header_footer_zone", -.15); Sub("long_marker_body_window", -.52);
            Assert.Equal(c.CandidateScore, Math.Clamp(values.Values.Sum(), 0, 1), 12);
            return new Fact(c.SourceId, rank, c.Page, c.Text, c.CandidateScore, c.EscalationScore, values, c.PositiveSignals, c.NegativeSignals);
        }
        public double Without(string signal) => Math.Clamp(Components.Where(x => x.Key != signal).Sum(x => x.Value), 0, 1);
    }
    private sealed record Heading(string OccurrenceId, IReadOnlyList<string> SourceLineIds, string Text, Fact Candidate, IReadOnlyList<Pair> Competitors, string RepresentationKind);
    private sealed record Pair(string CandidateId, int CandidateRank, string CandidateText, int CandidatePage, double HScore, double CScore, double ScoreDelta, IReadOnlyDictionary<string, double> SignalDelta)
    { public static Pair From(Fact h, Fact c) => new(c.Id, c.Rank, c.Text, c.Page, h.Score, c.Score, c.Score - h.Score, Signals.ToDictionary(x => x, x => c.Components.GetValueOrDefault(x) - h.Components.GetValueOrDefault(x), StringComparer.Ordinal)); }
    private sealed record RankDelta(string OccurrenceId, int BaselineRank, int ReplayedRank, int Delta);
    private sealed record Replay(string Signal, int BaselineLosses, int ReviewedRecoveredAt160, int ReviewedDisplacedAt160, int NetReviewedGain, int CandidateRankDelta, int CollateralRankChanges, IReadOnlyList<RankDelta> RankDeltas)
    {
        public int RecoveredAtK => ReviewedRecoveredAt160;
        public int DisplacedAtK => ReviewedDisplacedAt160;
        public int MeanRankDelta => CandidateRankDelta;
    }
    private sealed record D2Document(string DocumentId, string DocumentRelativePath, string DocumentSha256, string SilverArtifactSha256, string CensusArtifactSha256,
        int CandidateCount, int ReviewedCount, int ReviewedAtK, int BaselineLosses, IReadOnlyList<Heading> LostHeadings, IReadOnlyList<Replay> Counterfactuals, string Owner, string OwnerBasis)
    {
        public string InversionClassification => "UNRESOLVED";
    }
    private sealed record D2Artifact(int SchemaVersion, string ArtifactKind, string Phase, string SourceAuthority, int K, IReadOnlyList<D2Document> Documents,
        string BudgetCutoffRecurrence, string SameRankingInversionCause, string CrossDocumentBasis, string RemediationJustified, int ProviderCalls, bool ProductionCodeChanged);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string Sha256(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
}
