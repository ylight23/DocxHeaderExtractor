using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Round 2D: pairwise score-gap diagnosis; no score or production changes.</summary>
public sealed class PdfRound2ScoreGapOwnerProbe
{
    private const int Budget = 160;
    private static readonly (string Id, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    private static readonly IReadOnlyDictionary<string, double> FeatureWeights =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["base"] = 0.10, ["labelled_numbering_marker"] = 0.42, ["unlabelled_numbering_prefix"] = 0.10,
            ["standalone"] = 0.18, ["marker_title_composite"] = 0.28, ["canonical_marker_title"] = 0.22,
            ["layout_prominence"] = 0.16, ["opens_content"] = 0.12, ["table_scope"] = -0.60,
            ["running_page_scope"] = -0.75, ["header_footer_zone"] = -0.15, ["long_marker_body_window"] = -0.52
        };

    private sealed record Gold(string Id, int Page, string[] LineIds, string SourceText);
    private sealed record Candidate(
        string Id, string Text, int Rank, double Score, string RepresentationKind, string Scope,
        IReadOnlyList<string> PositiveSignals, IReadOnlyList<string> NegativeSignals,
        IReadOnlyList<string> AmbiguitySignals, IReadOnlyDictionary<string, double> Contributions);
    private sealed record Gap(
        string DocumentId, Gold Gold, Candidate TrueHeading, Candidate BoundaryCandidate,
        IReadOnlyList<Candidate> AboveCandidates, IReadOnlyList<Candidate> HighRankCandidates,
        double BoundaryGap, IReadOnlyDictionary<string, double> ContributionDelta,
        string Classification);

    [Fact]
    public void WriteScoreGapOwnerReport()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R2_SCORE_GAP_OWNER");
        if (string.IsNullOrWhiteSpace(output)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var all = new List<Gap>();
        var perDocument = new List<object>();
        foreach (var document in Documents)
        {
            var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
            var ranked = snapshot.Audit.Candidates;
            var gold = ReadGold(root, document.Id);
            var covered = Covering(gold, snapshot, ranked);
            var gaps = covered.Values.Where(item => item.Candidate.Rank > Budget).Select(item =>
            {
                var trueCandidate = item.Candidate;
                var boundary = CandidateAt(ranked, snapshot, Budget);
                var above = ranked.Skip(Math.Max(0, trueCandidate.Rank - 4)).Take(3)
                    .Select((candidate, index) => ToCandidate(candidate, snapshot, trueCandidate.Rank - 3 + index)).ToArray();
                var high = ranked.Take(5).Select((candidate, index) => ToCandidate(candidate, snapshot, index + 1)).ToArray();
                var delta = ContributionDelta(trueCandidate, boundary);
                return new Gap(document.Id, item.Gold, trueCandidate, boundary, above, high,
                    trueCandidate.Score - boundary.Score, delta, "UNRESOLVED");
            }).ToArray();
            all.AddRange(gaps);
            perDocument.Add(new
            {
                documentId = document.Id,
                candidateCount = snapshot.CandidateBlocks.Count,
                fullCandidate = covered.Count,
                outsideTop160 = gaps.Length,
                scoreGapMedian = Percentile(gaps.Select(x => x.BoundaryGap).OrderBy(x => x).ToArray(), .50),
                scoreGapP90 = Percentile(gaps.Select(x => x.BoundaryGap).OrderBy(x => x).ToArray(), .90),
                representationKinds = gaps.GroupBy(x => x.TrueHeading.RepresentationKind).ToDictionary(x => x.Key, x => x.Count()),
                classification = new { unresolved = gaps.Length }
            });
        }

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round2_ranking_score_gap_owner",
            phase = "round2d",
            modelCalls = 0,
            productionChanges = false,
            candidateGenerationChanged = false,
            rankingChanged = false,
            frozenBaseline = new { revision = "d51cf62", fullCandidate = 375, recallAt160 = 80, recallAt640 = 184 },
            pairwiseMethod = "true full-candidate occurrence versus rank-160 boundary, bounded candidates above, and top-five representatives",
            contributionMethod = "reconstruct existing PdfCandidateRanker.Build terms from emitted positive/negative signals; no weight search",
            partition = new
            {
                FEATURE_FACT_MISSING = 0,
                REPRESENTATION_SCORE_DISTORTION = 0,
                SCORE_TERM_OVERREWARD_NOISE = 0,
                SCORE_TERM_UNDERREWARD_HEADING = 0,
                MULTIPLE_SMALL_SCORE_GAPS = 0,
                BUDGET_LIMITED = 0,
                UNRESOLVED = all.Count
            },
            classes = new[]
            {
                new
                {
                    classification = "UNRESOLVED",
                    owner = "PdfCandidateRanker.Build",
                    component = "src/DocxHeaderExtractor.Core/Pipeline/PdfCandidateRanking.cs",
                    documents = all.Select(x => x.DocumentId).Distinct().OrderBy(x => x).ToArray(),
                    count = all.Count,
                    scoreTermsInvolved = all.SelectMany(x => x.ContributionDelta.Keys).Distinct().OrderBy(x => x).ToArray(),
                    representationKinds = all.GroupBy(x => x.TrueHeading.RepresentationKind).ToDictionary(x => x.Key, x => x.Count()),
                    medianScoreGap = Percentile(all.Select(x => x.BoundaryGap).OrderBy(x => x).ToArray(), .50),
                    p90ScoreGap = Percentile(all.Select(x => x.BoundaryGap).OrderBy(x => x).ToArray(), .90),
                    nearestNoiseClass = "not established from source-only ranking artifact",
                    crossDocument = all.Select(x => x.DocumentId).Distinct().Count() > 1,
                    selectiveInvariant = (string?)null,
                    repairInvariantAvailable = false,
                    estimatedRecoveryAt160 = 0,
                    collateralRisk = "unknown; no counterfactual run because exact owner is not proven",
                    status = "RANK_OWNER_UNRESOLVED"
                }
            },
            perDocument,
            losses = all.OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.Gold.Id, StringComparer.Ordinal)
                .Select(x => new
                {
                    documentId = x.DocumentId,
                    goldStableId = x.Gold.Id,
                    page = x.Gold.Page,
                    sourceLineIds = x.Gold.LineIds,
                    sourceText = x.Gold.SourceText,
                    trueHeading = x.TrueHeading,
                    boundaryCandidate = x.BoundaryCandidate,
                    boundaryGap = x.BoundaryGap,
                    contributionDelta = x.ContributionDelta,
                    aboveCandidates = x.AboveCandidates,
                    highRankCandidates = x.HighRankCandidates,
                    classification = x.Classification
                }).ToArray(),
            stopReason = "score-gap observation does not by itself prove a selective causal owner; no production implementation"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void ScoreGapReportHasExactPartition()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R2_SCORE_GAP_OWNER");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        var partition = report.RootElement.GetProperty("partition");
        var total = partition.EnumerateObject().Sum(x => x.Value.GetInt32());
        Assert.Equal(295, total);
        Assert.Equal(375, report.RootElement.GetProperty("frozenBaseline").GetProperty("fullCandidate").GetInt32());
    }

    private static Candidate CandidateAt(IReadOnlyList<RankedCandidate> ranked, PdfCandidateRankingSnapshot snapshot, int rank) =>
        ToCandidate(ranked[rank - 1], snapshot, rank);

    private static Candidate ToCandidate(RankedCandidate candidate, PdfCandidateRankingSnapshot snapshot, int rank) => new(
        candidate.SourceId, candidate.Text, rank, candidate.CandidateScore,
        snapshot.Provenance[candidate.SourceId].RepresentationKind.ToString(), candidate.Scope,
        candidate.PositiveSignals, candidate.NegativeSignals, candidate.AmbiguitySignals,
        Contributions(candidate));

    private static IReadOnlyDictionary<string, double> Contributions(RankedCandidate candidate)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal) { ["base"] = FeatureWeights["base"] };
        foreach (var signal in candidate.PositiveSignals.Concat(candidate.NegativeSignals))
            if (FeatureWeights.TryGetValue(signal, out var weight)) result[signal] = weight;
        return result;
    }

    private static IReadOnlyDictionary<string, double> ContributionDelta(Candidate trueHeading, Candidate competitor)
    {
        var keys = trueHeading.Contributions.Keys.Concat(competitor.Contributions.Keys).Distinct(StringComparer.Ordinal);
        return keys.ToDictionary(key => key,
            key => trueHeading.Contributions.GetValueOrDefault(key) - competitor.Contributions.GetValueOrDefault(key),
            StringComparer.Ordinal);
    }

    private static Dictionary<string, (Gold Gold, Candidate Candidate)> Covering(
        IReadOnlyList<Gold> gold, PdfCandidateRankingSnapshot snapshot, IReadOnlyList<RankedCandidate> ranked)
    {
        var lineIndex = snapshot.Lines.Select((line, index) => (Id: PdfCandidateProvenance.LineId(line), Index: index))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);
        var result = new Dictionary<string, (Gold, Candidate)>(StringComparer.Ordinal);
        foreach (var item in gold)
        {
            var required = item.LineIds.Select(id => lineIndex.TryGetValue(id, out var index) ? index : -1).ToArray();
            if (required.Any(index => index < 0)) continue;
            var candidate = ranked.FirstOrDefault(c => snapshot.Provenance.TryGetValue(c.SourceId, out var provenance) && provenance.Covers(required));
            if (candidate is not null) result[item.Id] = (item, ToCandidate(candidate, snapshot, RankOf(ranked, candidate.SourceId)));
        }
        return result;
    }

    private static int RankOf(IReadOnlyList<RankedCandidate> ranked, string id) =>
        ranked.Select((candidate, index) => (candidate.SourceId, Rank: index + 1)).First(x => x.SourceId == id).Rank;

    private static Gold[] ReadGold(string root, string documentId)
    {
        var path = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", $"{documentId}-n3.2-silver-model-assisted.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("headingOccurrences").EnumerateArray().Select(item => new Gold(
            item.GetProperty("goldStableId").GetString()!, item.GetProperty("page").GetInt32(),
            item.GetProperty("sourceLineIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            item.GetProperty("sourceText").GetString()!)).ToArray();
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1);
        return values[index];
    }
}
