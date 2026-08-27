using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Round 2A/2B: model-free ranking baseline for occurrences already in the pool.</summary>
public sealed class PdfRound2RankingBaselineProbe
{
    private const int Budget = 160;
    private static readonly int[] Cutoffs = [40, 80, 160, 320, 640];
    private static readonly (string DocumentId, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    private sealed record Gold(string Id, string DocumentSha256, int Page, string[] LineIds, string SourceText);
    private sealed record RankedRow(
        string DocumentId, string GoldStableId, int Page, string[] SourceLineIds, string SourceText,
        string CandidateId, string CandidateText, int Rank, double Score, double EscalationScore,
        string Tier, string Scope, string RepresentationKind, IReadOnlyList<string> PositiveSignals,
        IReadOnlyList<string> NegativeSignals, IReadOnlyList<string> AmbiguitySignals,
        IReadOnlyList<object> NearestCandidates);

    [Fact]
    public void WriteRankingBaseline()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R2_RANKING_BASELINE");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var allRows = new List<RankedRow>();
        var perDocument = new List<object>();
        var totalCandidateCount = 0;
        foreach (var document in Documents)
        {
            var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
            var gold = ReadGold(root, document.DocumentId);
            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
            totalCandidateCount += snapshot.CandidateBlocks.Count;
            var ranked = snapshot.Audit.Candidates;
            var rankById = ranked.Select((candidate, index) => (candidate.SourceId, Rank: index + 1))
                .ToDictionary(item => item.SourceId, item => item.Rank, StringComparer.Ordinal);
            var lineIndex = snapshot.Lines.Select((line, index) => (Id: PdfCandidateProvenance.LineId(line), Index: index))
                .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
            var rows = new List<RankedRow>();
            foreach (var item in gold)
            {
                var required = item.LineIds.Select(id => lineIndex.TryGetValue(id, out var index) ? index : -1).ToArray();
                if (required.Any(index => index < 0)) continue;
                var covering = ranked
                    .Where(candidate => snapshot.Provenance.TryGetValue(candidate.SourceId, out var provenance) &&
                        provenance.Covers(required))
                    .OrderBy(candidate => rankById[candidate.SourceId])
                    .ToArray();
                if (covering.Length == 0) continue;
                var selected = covering[0];
                var rank = rankById[selected.SourceId];
                var nearest = ranked
                    .Select((candidate, index) => (candidate, Rank: index + 1))
                    .Where(item2 => item2.Rank >= Math.Max(1, rank - 2) && item2.Rank <= rank + 2 &&
                        item2.candidate.SourceId != selected.SourceId)
                    .Select(item2 => (object)new
                    {
                        candidateId = item2.candidate.SourceId,
                        rank = item2.Rank,
                        text = item2.candidate.Text,
                        score = item2.candidate.CandidateScore,
                        scope = item2.candidate.Scope,
                        tier = item2.candidate.Tier.ToString()
                    }).ToArray();
                rows.Add(new RankedRow(document.DocumentId, item.Id, item.Page, item.LineIds, item.SourceText,
                    selected.SourceId, selected.Text, rank, selected.CandidateScore, selected.EscalationScore,
                    selected.Tier.ToString(), selected.Scope,
                    snapshot.Provenance[selected.SourceId].RepresentationKind.ToString(), selected.PositiveSignals,
                    selected.NegativeSignals, selected.AmbiguitySignals, nearest));
            }
            allRows.AddRange(rows);
            perDocument.Add(BuildSummary(document.DocumentId, snapshot.CandidateBlocks.Count, gold.Length, rows));
        }

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round2_ranking_baseline",
            phase = "round2a-2b",
            modelCalls = 0,
            productionChanges = false,
            candidateGenerationChanged = false,
            rankingChanged = false,
            selectionBudget = Budget,
            sourceAuthority = "Round1A b4685c8 + current source snapshots",
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only",
            cohort = new
            {
                reviewed = 422,
                candidateMisses = 47,
                fullCandidate = allRows.Count,
                rankingDenominator = allRows.Count,
                rule = "candidate provenance covers every reviewed sourceLineId"
            },
            aggregate = BuildSummary("all", totalCandidateCount, 422, allRows),
            perDocument,
            lossTaxonomy = new[]
            {
                "SCORE_SEPARATION_FAILURE", "BUDGET_LIMITED", "FEATURE_FACT_MISSING",
                "REPRESENTATION_SCORE_DISTORTION", "UNRESOLVED"
            },
            firstLossPolicy = "ranking loss is measured only after exact full candidate coverage; no scorer or budget change",
            rows = allRows
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void RankingBaselineHasExactFullCandidateAccounting()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R2_RANKING_BASELINE");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        var cohort = report.RootElement.GetProperty("cohort");
        Assert.Equal(422, cohort.GetProperty("reviewed").GetInt32());
        Assert.Equal(375, cohort.GetProperty("fullCandidate").GetInt32());
        Assert.Equal(375, report.RootElement.GetProperty("rows").GetArrayLength());
    }

    private static Gold[] ReadGold(string root, string documentId)
    {
        var path = Path.Combine(root, "eval", "benchmark-n3", "silver-labels",
            $"{documentId}-n3.2-silver-model-assisted.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var packetReference = document.RootElement.GetProperty("sourcePacket");
        var packetRelativePath = packetReference.GetProperty("sourcePacketPath").GetString()!;
        var packetPath = Path.Combine(root, packetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var sourcePacket = JsonDocument.Parse(File.ReadAllText(packetPath));
        var sha = sourcePacket.RootElement.GetProperty("documentSha256").GetString()!;
        return document.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .Select(item => new Gold(
                item.GetProperty("goldStableId").GetString()!, sha,
                item.GetProperty("page").GetInt32(),
                item.GetProperty("sourceLineIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
                item.GetProperty("sourceText").GetString()!))
            .ToArray();
    }

    private static object BuildSummary(string documentId, int candidateCount, int reviewed, IReadOnlyList<RankedRow> rows)
    {
        var ranks = rows.Select(row => row.Rank).OrderBy(rank => rank).ToArray();
        var recall = Cutoffs.ToDictionary(cutoff => $"recallAt{cutoff}", cutoff => $"{rows.Count(row => row.Rank <= cutoff)}/{rows.Count}");
        return new
        {
            documentId,
            candidateCount,
            reviewed,
            fullCandidate = rows.Count,
            recall,
            rankP50 = Percentile(ranks, 0.50),
            rankP90 = Percentile(ranks, 0.90),
            rankMax = ranks.DefaultIfEmpty(0).Max(),
            outsideBudget = rows.Count(row => row.Rank > Budget),
            rankLosses = rows.Where(row => row.Rank > Budget)
                .Select(row => new { row.GoldStableId, row.Rank, row.Score, row.CandidateId, row.PositiveSignals, row.NegativeSignals })
                .ToArray()
        };
    }

    private static int Percentile(IReadOnlyList<int> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1);
        return values[index];
    }
}
