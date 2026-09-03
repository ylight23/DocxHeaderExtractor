using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Round 2C: offline score-owner counterfactual; never changes production ranking.</summary>
public sealed class PdfRound2RankingScoreCounterfactualProbe
{
    private const int Budget = 160;
    private static readonly int[] Cutoffs = [40, 80, 160, 320, 640];
    private static readonly (string Id, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    private sealed record Gold(string Id, int Page, string[] LineIds, string SourceText);
    private sealed record CandidateView(
        string Id, string Text, int Rank, double Score, double EscalationScore, string Tier,
        string Scope, string RepresentationKind, IReadOnlyList<string> PositiveSignals,
        IReadOnlyList<string> NegativeSignals, IReadOnlyList<string> AmbiguitySignals);
    private sealed record Occurrence(
        string DocumentId, Gold Gold, CandidateView Baseline, CandidateView Counterfactual,
        IReadOnlyList<CandidateView> BaselineCompetitors, IReadOnlyList<CandidateView> CounterfactualCompetitors);

    [Fact]
    public void WriteScoreCounterfactual()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R2_SCORE_COUNTERFACTUAL");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var all = new List<Occurrence>();
        var documents = new List<object>();
        var totalCandidates = 0;
        var observedNoiseDelta = 0;
        foreach (var document in Documents)
        {
            var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            totalCandidates += snapshot.CandidateBlocks.Count;
            var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
            var baseline = snapshot.Audit.Candidates;
            var counterfactual = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts, structuralMarkerCountsAsStrong: true);
            var gold = ReadGold(root, document.Id);
            observedNoiseDelta += NoiseDelta(snapshot, gold, baseline, counterfactual);
            var baselineRows = Covering(gold, snapshot, baseline);
            var counterfactualRows = Covering(gold, snapshot, counterfactual);
            Assert.Equal(baselineRows.Keys.OrderBy(x => x), counterfactualRows.Keys.OrderBy(x => x));

            var rows = baselineRows.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(id =>
            {
                var before = baselineRows[id].Candidate;
                var after = counterfactualRows[id].Candidate;
                return new Occurrence(document.Id, baselineRows[id].Gold, before, after,
                    Neighbors(snapshot, baseline, before.Rank), Neighbors(snapshot, counterfactual, after.Rank));
            }).ToArray();
            all.AddRange(rows);
            documents.Add(Summary(document.Id, snapshot.CandidateBlocks.Count, gold.Length, rows));
        }

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round2_ranking_score_counterfactual",
            phase = "round2c",
            modelCalls = 0,
            productionChanges = false,
            candidateGenerationChanged = false,
            rankingChanged = false,
            selectionBudget = Budget,
            baseline = new
            {
                revision = "d51cf62",
                fullCandidate = all.Count,
                recallAt160 = all.Count(row => row.Baseline.Rank <= Budget),
                recallAt640 = all.Count(row => row.Baseline.Rank <= 640),
                rankP50 = Percentile(all.Select(row => row.Baseline.Rank).OrderBy(x => x).ToArray(), .50),
                rankP90 = Percentile(all.Select(row => row.Baseline.Rank).OrderBy(x => x).ToArray(), .90),
                rankMax = all.Select(row => row.Baseline.Rank).DefaultIfEmpty().Max()
            },
            counterfactual = new
            {
                name = "structural_marker_counts_as_strong",
                rule = "admit existing HasStructuralMarker fact to existing strong-marker path; no weight change",
                candidateCountBefore = totalCandidates,
                candidateCountAfter = totalCandidates,
                metrics = Metrics(all, row => row.Counterfactual),
                trueHeadingsRecoveredAt160 = all.Count(row => row.Baseline.Rank > Budget && row.Counterfactual.Rank <= Budget),
                trueHeadingsLostFrom160 = all.Count(row => row.Baseline.Rank <= Budget && row.Counterfactual.Rank > Budget),
                existingTrueHeadingsDisplaced = all.Count(row => row.Baseline.Rank <= Budget && row.Counterfactual.Rank > Budget),
                observedNoiseEntering160 = observedNoiseDelta
            },
            scoreOwnerClasses = new[]
            {
                new
                {
                    causalStatus = "SCORE_SEPARATION_FAILURE",
                    count = all.Count(row => row.Baseline.Rank > Budget && row.Counterfactual.Score != row.Baseline.Score),
                    documents = all.Where(row => row.Baseline.Rank > Budget).Select(row => row.DocumentId).Distinct().OrderBy(x => x).ToArray(),
                    ownerFeatures = new[] { "HasStructuralMarker", "labelled_numbering_marker" },
                    counterfactual = "structural_marker_counts_as_strong",
                    Recall160Before = all.Count(row => row.Baseline.Rank <= Budget),
                    Recall160After = all.Count(row => row.Counterfactual.Rank <= Budget),
                    Recall640Before = all.Count(row => row.Baseline.Rank <= 640),
                    Recall640After = all.Count(row => row.Counterfactual.Rank <= 640),
                    trueRecovered = all.Count(row => row.Baseline.Rank > Budget && row.Counterfactual.Rank <= Budget),
                    trueLost = all.Count(row => row.Baseline.Rank <= Budget && row.Counterfactual.Rank > Budget),
                    noiseDelta = observedNoiseDelta,
                    selectiveInvariant = "existing structural-marker fact may be equivalent to labelled marker only if the same fact is safe across reviewed heading and non-heading populations",
                    status = Status(all)
                }
            },
            perDocument = documents,
            lossLedger = all.Where(row => row.Baseline.Rank > Budget).Select(row => new
            {
                row.DocumentId,
                goldStableId = row.Gold.Id,
                row.Gold.Page,
                sourceLineIds = row.Gold.LineIds,
                row.Gold.SourceText,
                baseline = row.Baseline,
                counterfactual = row.Counterfactual,
                baselineScoreDelta = row.Counterfactual.Score - row.Baseline.Score,
                baselineCompetitors = row.BaselineCompetitors,
                counterfactualCompetitors = row.CounterfactualCompetitors,
                lossStatus = "UNRESOLVED"
            }).ToArray(),
            stopReason = "offline counterfactual only; no production implementation until a selective invariant passes collateral review"
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void ScoreCounterfactualPreservesCandidateAccounting()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R2_SCORE_COUNTERFACTUAL");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        var cf = report.RootElement.GetProperty("counterfactual");
        Assert.Equal(cf.GetProperty("candidateCountBefore").GetInt32(), cf.GetProperty("candidateCountAfter").GetInt32());
        Assert.Equal(375, report.RootElement.GetProperty("baseline").GetProperty("fullCandidate").GetInt32());
    }

    private static Dictionary<string, (Gold Gold, CandidateView Candidate)> Covering(
        IReadOnlyList<Gold> gold, PdfCandidateRankingSnapshot snapshot, IReadOnlyList<RankedCandidate> ranked)
    {
        var lineIndex = snapshot.Lines.Select((line, index) => (Id: PdfCandidateProvenance.LineId(line), Index: index))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);
        var result = new Dictionary<string, (Gold, CandidateView)>(StringComparer.Ordinal);
        foreach (var item in gold)
        {
            var required = item.LineIds.Select(id => lineIndex.TryGetValue(id, out var index) ? index : -1).ToArray();
            if (required.Any(index => index < 0)) continue;
            var candidates = ranked.Where(candidate => snapshot.Provenance.TryGetValue(candidate.SourceId, out var provenance) && provenance.Covers(required)).ToArray();
            if (candidates.Length == 0) continue;
            var candidate = candidates[0];
            result[item.Id] = (item, View(candidate, RankOf(ranked, candidate.SourceId),
                snapshot.Provenance[candidate.SourceId].RepresentationKind.ToString()));
        }
        return result;
    }

    private static CandidateView View(RankedCandidate c, int rank, string representationKind = "unknown") => new(c.SourceId, c.Text, rank, c.CandidateScore,
        c.EscalationScore, c.Tier.ToString(), c.Scope,
        representationKind,
        c.PositiveSignals, c.NegativeSignals, c.AmbiguitySignals);

    private static IReadOnlyList<CandidateView> Neighbors(PdfCandidateRankingSnapshot snapshot,
        IReadOnlyList<RankedCandidate> ranked, int rank) => ranked
        .Select((candidate, index) => View(candidate, index + 1,
            snapshot.Provenance[candidate.SourceId].RepresentationKind.ToString()))
        .Where(candidate => candidate.Rank >= Math.Max(1, rank - 2) && candidate.Rank <= rank + 2 && candidate.Rank != rank)
        .ToArray();

    private static int RankOf(IReadOnlyList<RankedCandidate> ranked, string id) =>
        ranked.Select((candidate, index) => (candidate.SourceId, Rank: index + 1)).First(x => x.SourceId == id).Rank;

    private static object Metrics(IReadOnlyList<Occurrence> rows, Func<Occurrence, CandidateView> selector)
    {
        var ranks = rows.Select(selector).Select(x => x.Rank).OrderBy(x => x).ToArray();
        return new
        {
            recallAt40 = rows.Count(row => selector(row).Rank <= 40),
            recallAt80 = rows.Count(row => selector(row).Rank <= 80),
            recallAt160 = rows.Count(row => selector(row).Rank <= 160),
            recallAt320 = rows.Count(row => selector(row).Rank <= 320),
            recallAt640 = rows.Count(row => selector(row).Rank <= 640),
            recallAtAll = rows.Count,
            rankP50 = Percentile(ranks, .50),
            rankP90 = Percentile(ranks, .90),
            rankMax = ranks.DefaultIfEmpty().Max(),
            outsideBudget = rows.Count(row => selector(row).Rank > Budget)
        };
    }

    private static object Summary(string documentId, int candidates, int reviewed, IReadOnlyList<Occurrence> rows) => new
    {
        documentId,
        candidateCount = candidates,
        reviewed,
        fullCandidate = rows.Count,
        baseline = Metrics(rows, row => row.Baseline),
        counterfactual = Metrics(rows, row => row.Counterfactual),
        recoveredAt160 = rows.Count(row => row.Baseline.Rank > Budget && row.Counterfactual.Rank <= Budget),
        lostFrom160 = rows.Count(row => row.Baseline.Rank <= Budget && row.Counterfactual.Rank > Budget)
    };

    private static int NoiseDelta(PdfCandidateRankingSnapshot snapshot, IReadOnlyList<Gold> gold,
        IReadOnlyList<RankedCandidate> baseline, IReadOnlyList<RankedCandidate> counterfactual)
    {
        var lineIndex = snapshot.Lines.Select((line, index) => (Id: PdfCandidateProvenance.LineId(line), Index: index))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);
        var goldCovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in gold)
        {
            var required = item.LineIds.Select(id => lineIndex.TryGetValue(id, out var index) ? index : -1).ToArray();
            if (required.Any(index => index < 0)) continue;
            foreach (var candidate in baseline)
                if (snapshot.Provenance.TryGetValue(candidate.SourceId, out var provenance) && provenance.Covers(required))
                    goldCovered.Add(candidate.SourceId);
        }
        var beforeNoise = baseline.Take(Budget).Count(candidate => !goldCovered.Contains(candidate.SourceId));
        var afterNoise = counterfactual.Take(Budget).Count(candidate => !goldCovered.Contains(candidate.SourceId));
        return afterNoise - beforeNoise;
    }

    private static string Status(IReadOnlyList<Occurrence> rows)
    {
        var recovered = rows.Count(row => row.Baseline.Rank > Budget && row.Counterfactual.Rank <= Budget);
        var lost = rows.Count(row => row.Baseline.Rank <= Budget && row.Counterfactual.Rank > Budget);
        return recovered > 0 && lost == 0 ? "RANK_REMEDIATION_CANDIDATE" : "RANK_REMEDIATION_NOT_JUSTIFIED";
    }

    private static Gold[] ReadGold(string root, string documentId)
    {
        var path = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", $"{documentId}-n3.2-silver-model-assisted.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("headingOccurrences").EnumerateArray().Select(item => new Gold(
            item.GetProperty("goldStableId").GetString()!, item.GetProperty("page").GetInt32(),
            item.GetProperty("sourceLineIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            item.GetProperty("sourceText").GetString()!)).ToArray();
    }

    private static int Percentile(IReadOnlyList<int> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1);
        return values[index];
    }
}
