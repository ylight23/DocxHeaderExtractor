using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Round 3A-3C: offline recall/budget feasibility for the unchanged selector.</summary>
public sealed class PdfRound3SelectionArchitectureProbe
{
    private const int BaselineK = 160;
    private const int SemanticBatchSize = 8;
    private static readonly int[] Cutoffs = [40, 80, 160, 320, 640, 1024, 1600, 2048, 2400, 3200, int.MaxValue];
    private static readonly (string Id, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    private sealed record Gold(string Id, int Page, string[] LineIds, string SourceText);
    private sealed record DocumentData(string Id, int Reviewed, int CandidateCount, int FullCandidate,
        IReadOnlyList<RankedCandidate> Ranked, IReadOnlySet<string> GoldCoveredCandidateIds,
        IReadOnlyDictionary<string, int> GoldRankById);
    private sealed record CurveRow(string K, int SelectedCandidateCount, int SelectedReviewedHeadings,
        string ReviewedHeadingRecall, double SelectionCoverage, double OverallPreSemanticRecall,
        int IncrementalHeadingGain, int IncrementalCandidates, double CandidatesPerIncrementalHeading,
        int IncrementalNoiseCost,
        int TrueHeadingCoverage, int CandidatesSentToSemantic, double CandidatesPerRecoveredHeading,
        int EstimatedRequestCount, double RelativeCostVsK160);

    [Fact]
    public void WriteSelectionArchitectureReport()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R3_SELECTION_FEASIBILITY");
        if (string.IsNullOrWhiteSpace(output)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var data = Documents.Select(document => BuildDocument(root, document)).ToArray();
        var allReviewed = data.Sum(x => x.Reviewed);
        var allFull = data.Sum(x => x.FullCandidate);
        var baselineSelected = data.Sum(x => Math.Min(BaselineK, x.CandidateCount));
        var curves = Cutoffs.Select(k => BuildCurveRow(k, data, allReviewed, baselineSelected)).ToArray();
        var baseCurve = curves.First(x => x.K == BaselineK.ToString());
        var perDocument = data.Select(document => new
        {
            documentId = document.Id,
            reviewed = document.Reviewed,
            candidateCount = document.CandidateCount,
            fullCandidate = document.FullCandidate,
            curve = Cutoffs.Select(k => BuildCurveRow(k, [document], document.Reviewed,
                Math.Min(BaselineK, document.CandidateCount))).ToArray()
        }).ToArray();

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round3_selection_architecture_feasibility",
            phase = "round3a-3b-3c",
            modelCalls = 0,
            productionChanges = false,
            candidateGenerationChanged = false,
            rankingChanged = false,
            frozenRanking = new { revision = "d51cf62", round2c = "8137277", round2d = "45d47ad" },
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only",
            semanticBatching = new
            {
                batchSize = SemanticBatchSize,
                estimate = "ceil(candidatesSentToSemantic / batchSize), transport-only; no provider call",
                currentSelector = "unchanged deterministic ranking, top-K per document"
            },
            ceilings = new
            {
                reviewed = allReviewed,
                candidateCeiling = $"{allFull}/{allReviewed}",
                candidateCeilingRatio = (double)allFull / allReviewed,
                baselineSelection = $"{baseCurve.SelectedReviewedHeadings}/{allReviewed}",
                baselineSelectionRatio = (double)baseCurve.SelectedReviewedHeadings / allReviewed
            },
            curve = curves,
            paretoTable = curves.Select(row => new
            {
                K = row.K,
                reviewedHeadingSelected = row.SelectedReviewedHeadings,
                selectionCoverage = row.SelectionCoverage,
                overallPreSemanticRecall = row.OverallPreSemanticRecall,
                incrementalHeadingGain = row.IncrementalHeadingGain,
                incrementalCandidates = row.IncrementalCandidates,
                candidatesPerIncrementalHeading = row.CandidatesPerIncrementalHeading,
                semanticBatchCount = row.EstimatedRequestCount,
                relativeCostVs160 = row.RelativeCostVsK160
            }).ToArray(),
            paretoAssessment = new
            {
                frozenKValues = new[] { "40", "80", "160", "320", "640", "1024", "1600", "2048", "2400", "3200", "All" },
                statusRule = "architecture status is not inferred from one attractive result; a useful knee requires a frozen cost/coverage threshold, otherwise status remains unresolved",
                observed = "coverage keeps increasing through All; no cost/quality knee was frozen before this offline run",
                conclusion = "no bounded pool is promoted by this artifact"
            },
            perDocument,
            architectureOptions = new[]
            {
                new
                {
                    option = "A_current_top160",
                    expectedRecallCeiling = $"{baseCurve.SelectedReviewedHeadings}/{allReviewed} pre-semantic; {allFull}/{allReviewed} absolute candidate ceiling",
                    candidateModelCost = $"{baseCurve.SelectedCandidateCount} selected candidates; ~{baseCurve.EstimatedRequestCount} semantic batches",
                    newComplexity = "none",
                    trainingLabelRequirement = "none",
                    auditability = "highest; unchanged deterministic rank and provenance",
                    failClosedCompatibility = "preserved",
                    decision = "current baseline"
                },
                new
                {
                    option = "B_larger_deterministic_pool_existing_semantic",
                    expectedRecallCeiling = $"up to {allFull}/{allReviewed} if K reaches All; observed curve remains below ceiling until All",
                    candidateModelCost = $"K=640: {curves.First(x => x.K == "640").CandidatesSentToSemantic} candidates / {curves.First(x => x.K == "640").EstimatedRequestCount} batches; K=All: {curves.Last().CandidatesSentToSemantic} / {curves.Last().EstimatedRequestCount}",
                    newComplexity = "bounded scheduling and higher semantic transport cost",
                    trainingLabelRequirement = "none",
                    auditability = "high; deterministic candidates remain source-grounded",
                    failClosedCompatibility = "preserved if validator remains authority",
                    decision = "quantitatively bounded; practical cost threshold not frozen"
                },
                new
                {
                    option = "C_lightweight_semantic_or_learned_reranker",
                    expectedRecallCeiling = $"candidate ceiling {allFull}/{allReviewed}; selection quality unmeasured",
                    candidateModelCost = "reranker cost plus larger candidate pool; not executed",
                    newComplexity = "medium; new score/ordering contract and calibration",
                    trainingLabelRequirement = "likely labels or held-out calibration",
                    auditability = "lower than A/B unless every score feature is persisted",
                    failClosedCompatibility = "possible, but requires explicit authority boundary",
                    decision = "investigation only; no implementation"
                },
                new
                {
                    option = "D_replace_deterministic_ranker_learned",
                    expectedRecallCeiling = $"candidate ceiling {allFull}/{allReviewed}; unmeasured",
                    candidateModelCost = "training/inference cost plus full candidate audit",
                    newComplexity = "high; replacement ranking contract",
                    trainingLabelRequirement = "required representative labels and holdout",
                    auditability = "lowest initially; requires extensive provenance",
                    failClosedCompatibility = "not established",
                    decision = "not justified in Round 3"
                }
            },
            finalStatus = "SELECTION_ARCHITECTURE_UNRESOLVED",
            stopReason = "no provider call and no production change; a useful bounded K was not promoted without a frozen cost/quality gate"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void SelectionReportHasSeparateCeilings()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R3_SELECTION_FEASIBILITY");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        var ceilings = report.RootElement.GetProperty("ceilings");
        Assert.Equal(422, ceilings.GetProperty("reviewed").GetInt32());
        Assert.Equal("375/422", ceilings.GetProperty("candidateCeiling").GetString());
    }

    private static DocumentData BuildDocument(string root, (string Id, string RelativePath) document)
    {
        var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        var ranked = snapshot.Audit.Candidates;
        var gold = ReadGold(root, document.Id);
        var lineIndex = snapshot.Lines.Select((line, index) => (Id: PdfCandidateProvenance.LineId(line), Index: index))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);
        var covered = new Dictionary<string, int>(StringComparer.Ordinal);
        var coveredCandidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in gold)
        {
            var required = item.LineIds.Select(id => lineIndex.TryGetValue(id, out var index) ? index : -1).ToArray();
            if (required.Any(index => index < 0)) continue;
            var candidates = ranked.Where(candidate => snapshot.Provenance.TryGetValue(candidate.SourceId, out var provenance) && provenance.Covers(required)).ToArray();
            if (candidates.Length == 0) continue;
            foreach (var candidate in candidates) coveredCandidates.Add(candidate.SourceId);
            covered[item.Id] = RankOf(ranked, candidates[0].SourceId);
        }
        return new DocumentData(document.Id, gold.Length, snapshot.CandidateBlocks.Count, covered.Count, ranked,
            coveredCandidates, covered);
    }

    private static CurveRow BuildCurveRow(int requestedK, IReadOnlyList<DocumentData> documents, int reviewed, int baselineSelected)
    {
        var k = requestedK == int.MaxValue ? int.MaxValue : requestedK;
        var selected = documents.Sum(document => Math.Min(k, document.CandidateCount));
        var selectedHeadings = documents.Sum(document => document.GoldRankById.Values.Count(rank => rank <= k));
        var previousK = PreviousCutoff(k);
        var previousHeadings = documents.Sum(document => document.GoldRankById.Values.Count(rank => rank <= previousK));
        var previousSelected = documents.Sum(document => Math.Min(previousK, document.CandidateCount));
        var noise = documents.Sum(document =>
        {
            var before = document.Ranked.Take(Math.Min(previousK, document.CandidateCount)).Count(candidate => !document.GoldCoveredCandidateIds.Contains(candidate.SourceId));
            var after = document.Ranked.Take(Math.Min(k, document.CandidateCount)).Count(candidate => !document.GoldCoveredCandidateIds.Contains(candidate.SourceId));
            return after - before;
        });
        var denominator = Math.Max(1, selectedHeadings);
        var ratio = baselineSelected == 0 ? 0 : (double)selected / baselineSelected;
        var incrementalCandidates = selected - previousSelected;
        var incrementalGain = selectedHeadings - previousHeadings;
        return new CurveRow(k == int.MaxValue ? "All" : k.ToString(), selected, selectedHeadings, $"{selectedHeadings}/{reviewed}",
            (double)selectedHeadings / Math.Max(1, documents.Sum(x => x.FullCandidate)),
            (double)selectedHeadings / Math.Max(1, reviewed), incrementalGain, incrementalCandidates,
            incrementalGain == 0 ? 0 : (double)incrementalCandidates / incrementalGain,
            noise, selectedHeadings, selected,
            (double)selected / denominator, (selected + SemanticBatchSize - 1) / SemanticBatchSize, ratio);
    }

    private static int PreviousCutoff(int k) => k switch
    {
        40 => 0, 80 => 40, 160 => 80, 320 => 160, 640 => 320, 1024 => 640,
        1600 => 1024, 2048 => 1600, 2400 => 2048, 3200 => 2400, int.MaxValue => 3200, _ => 0
    };

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
}
