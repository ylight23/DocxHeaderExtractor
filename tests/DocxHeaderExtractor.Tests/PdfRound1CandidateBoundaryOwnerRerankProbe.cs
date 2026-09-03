using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Round 1C.1: materializes the boundary counterfactual and runs the real deterministic ranker.
/// This remains an evaluation-only probe; it does not change the production candidate builder.
/// </summary>
public sealed class PdfRound1CandidateBoundaryOwnerRerankProbe
{
    private static readonly (string DocumentId, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    private sealed record Gold(string Id, string[] LineIds, string Text);

    private sealed record Row(
        string DocumentId,
        string DocumentSha256,
        string GoldStableId,
        string[] ReviewedSourceLineIds,
        string SourceText,
        string[] CandidateIdsBeforeRepair,
        string? RepairedCandidateId,
        string[] CandidateLineIdsBeforeRepair,
        string[] MissingLineIds,
        string[] ExtraLineIds,
        string Shape,
        string ProductionOwner,
        string Classification,
        string RepresentationKind,
        int? RankBefore,
        int? RankAfter,
        bool RecoveredBeforeRanker,
        bool RecoveredAfterRanker,
        string Evidence);

    private sealed record DocumentResult(
        string DocumentId,
        string DocumentSha256,
        int CandidateCountBefore,
        int CandidateCountAfter,
        int[] BeforeCoverage,
        int[] AfterCoverage,
        int[] BeforeBestRanks,
        int[] AfterBestRanks,
        IReadOnlyList<Row> Rows,
        int ExistingGoldDisplacedFrom160,
        int BaselinePresentChanged);

    [Fact]
    public void WriteOwnerRerankCounterfactual()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1_BOUNDARY_OWNER_RERANK");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var results = Documents.Select(document => AnalyzeDocument(root, document)).ToArray();
        var rows = results.SelectMany(result => result.Rows).ToArray();
        Assert.Equal(18, rows.Length);

        var before = results.SelectMany(result => result.BeforeCoverage).ToArray();
        var after = results.SelectMany(result => result.AfterCoverage).ToArray();
        var beforeRanks = results.SelectMany(result => result.BeforeBestRanks).Where(rank => rank > 0).ToArray();
        var afterRanks = results.SelectMany(result => result.AfterBestRanks).Where(rank => rank > 0).ToArray();
        var classification = rows.GroupBy(row => row.Classification, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round1_candidate_boundary_owner_rerank",
            phase = "round1c1",
            modelCalls = 0,
            productionChanges = false,
            sourceAuthority = "Round1A b4685c8 + Round1B 4d7c814 + Round1C f9a7452",
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is run-local diagnostics",
            boundaryMismatchCount = 18,
            classification,
            owner = new
            {
                productionOwner = "UNRESOLVED",
                reason = "current snapshot exposes candidate provenance but not producer/transform lineage",
                productionBoundaryDefect = classification.GetValueOrDefault("REPRESENTATION_LOSS", 0),
                evaluatorJoinIssue = classification.GetValueOrDefault("EVALUATOR_EXACT_JOIN_TOO_STRICT", 0),
                representationLoss = classification.GetValueOrDefault("REPRESENTATION_LOSS", 0),
                unresolved = classification.GetValueOrDefault("UNRESOLVED", 0)
            },
            baseline = new
            {
                candidateCount = results.Sum(result => result.CandidateCountBefore),
                recallAt40 = Recall(beforeRanks, 40, 422),
                recallAt80 = Recall(beforeRanks, 80, 422),
                recallAt160 = Recall(beforeRanks, 160, 422),
                recallAt320 = Recall(beforeRanks, 320, 422),
                recallAt640 = Recall(beforeRanks, 640, 422),
                recallAtAll = $"{before.Count(rank => rank > 0)}/422"
            },
            counterfactual = new
            {
                candidateCount = results.Sum(result => result.CandidateCountAfter),
                candidateCountDelta = results.Sum(result => result.CandidateCountAfter - result.CandidateCountBefore),
                recoveredTrueHeadings = rows.Count(row => row.RecoveredAfterRanker),
                recallAt40 = Recall(afterRanks, 40, 422),
                recallAt80 = Recall(afterRanks, 80, 422),
                recallAt160 = Recall(afterRanks, 160, 422),
                recallAt320 = Recall(afterRanks, 320, 422),
                recallAt640 = Recall(afterRanks, 640, 422),
                recallAtAll = $"{after.Count(rank => rank > 0)}/422",
                rankScoreRecomputed = true,
                candidatesAdded = 0,
                duplicateCandidates = 0,
                candidateInflation = 0,
                existingGoldDisplacedFrom160 = results.Sum(result => result.ExistingGoldDisplacedFrom160),
                negativeControlFailures = results.Sum(result => result.BaselinePresentChanged)
            },
            perDocument = results.Select(result => new
            {
                result.DocumentId,
                result.CandidateCountBefore,
                result.CandidateCountAfter,
                candidateCountDelta = result.CandidateCountAfter - result.CandidateCountBefore,
                recallAt160Before = Recall(result.BeforeBestRanks, 160, result.BeforeBestRanks.Length),
                recallAt160After = Recall(result.AfterBestRanks, 160, result.AfterBestRanks.Length),
                recallAtAllBefore = $"{result.BeforeCoverage.Count(rank => rank > 0)}/{result.BeforeCoverage.Length}",
                recallAtAllAfter = $"{result.AfterCoverage.Count(rank => rank > 0)}/{result.AfterCoverage.Length}"
            }).ToArray(),
            shapes = rows.GroupBy(row => row.Shape, StringComparer.Ordinal).Select(group => new
            {
                shape = group.Key,
                count = group.Count(),
                documents = group.Select(row => row.DocumentId).Distinct().OrderBy(x => x).ToArray(),
                recovered = group.Count(row => row.RecoveredAfterRanker),
                recall160Delta = group.Count(row => row.RankAfter is <= 160)
            }).ToArray(),
            occurrences = rows,
            status = "BOUNDARY_OWNER_UNRESOLVED"
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void FrozenOwnerRerankHas18Rows()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1_BOUNDARY_OWNER_RERANK");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(18, report.RootElement.GetProperty("boundaryMismatchCount").GetInt32());
        Assert.Equal(18, report.RootElement.GetProperty("occurrences").GetArrayLength());
    }

    private static DocumentResult AnalyzeDocument(string root,
        (string DocumentId, string RelativePath) document)
    {
        var causalPath = Path.Combine(root, "eval", "accuracy-round1", "candidate-loss-causal-classification.v1.json");
        using var causal = JsonDocument.Parse(File.ReadAllText(causalPath));
        var causalRows = causal.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(row => row.GetProperty("Owner").GetString() == "CANDIDATE_BOUNDARY_MISMATCH" &&
                          row.GetProperty("DocumentId").GetString() == document.DocumentId).ToArray();
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels",
            document.DocumentId + "-n3.2-silver-model-assisted.v1.json");
        var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        var gold = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .Select(item => new Gold(item.GetProperty("goldStableId").GetString()!,
                item.GetProperty("sourceLineIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
                item.GetProperty("sourceText").GetString()!)).ToArray();
        var goldById = gold.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        var lineIndex = snapshot.Lines.Select((line, index) => (id: PdfCandidateProvenance.LineId(line), index))
            .GroupBy(item => item.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        var allGold = gold.Select(item => item.LineIds.Select(id => lineIndex[id]).ToArray()).ToArray();
        var beforeRanks = RankGold(snapshot.Lines, snapshot.CandidateBlocks, snapshot.Audit.Candidates, allGold);
        var beforeCoverage = beforeRanks.Select(rank => rank > 0 ? 1 : 0).ToArray();
        var rankById = snapshot.Audit.Candidates.Select((candidate, index) => (candidate.SourceId, rank: index + 1))
            .ToDictionary(item => item.SourceId, item => item.rank, StringComparer.Ordinal);
        var provenance = snapshot.Provenance.Values.ToDictionary(item => item.CandidateSourceId, item => item);
        var repaired = snapshot.CandidateBlocks.ToList();
        var rows = new List<Row>();

        foreach (var causalRow in causalRows)
        {
            var goldItem = goldById[causalRow.GetProperty("GoldStableId").GetString()!];
            var required = goldItem.LineIds.Select(id => lineIndex[id]).ToArray();
            var touching = provenance.Values.Where(candidate => candidate.LineIndexes.Any(required.Contains)).ToArray();
            var selected = touching.OrderBy(candidate => rankById[candidate.CandidateSourceId]).FirstOrDefault();
            if (selected is null) continue;
            var selectedBlock = repaired.First(block => block.Id == selected.CandidateSourceId);
            var selectedIndexes = selected.LineIndexes.ToHashSet();
            foreach (var index in required) selectedIndexes.Add(index);
            var repairedLines = selectedIndexes.OrderBy(index => index).Select(index => snapshot.Lines[index]).ToArray();
            repaired[repaired.IndexOf(selectedBlock)] = RebuildBlock(selectedBlock, repairedLines);
            var missing = required.Where(index => !selected.LineIndexes.Contains(index))
                .Select(index => PdfCandidateProvenance.LineId(snapshot.Lines[index])).ToArray();
            var extras = selected.LineIndexes.Where(index => !required.Contains(index))
                .Select(index => PdfCandidateProvenance.LineId(snapshot.Lines[index])).ToArray();
            var shape = Shape(required, selected.LineIndexes);
            var classification = missing.All(id => Canonical(id).Length == 0)
                ? "EVALUATOR_EXACT_JOIN_TOO_STRICT"
                : "REPRESENTATION_LOSS";
            rows.Add(new Row(document.DocumentId, causalRow.GetProperty("DocumentSha256").GetString()!,
                goldItem.Id, goldItem.LineIds, goldItem.Text,
                touching.Select(item => item.CandidateSourceId).ToArray(), selected.CandidateSourceId,
                selected.LineIds.ToArray(), missing, extras, shape, "UNRESOLVED", classification,
                selectedBlock.GetType().Name, rankById[selected.CandidateSourceId], null, false, false,
                "existing candidate widened in-memory; producer lineage unavailable"));
        }

        var contexts = PdfCandidateContextBuilder.Build(repaired, snapshot.Annotations);
        var reranked = PdfCandidateRanker.Rank(repaired, contexts);
        var afterRankById = reranked.Select((candidate, index) => (candidate.SourceId, rank: index + 1))
            .ToDictionary(item => item.SourceId, item => item.rank, StringComparer.Ordinal);
        foreach (var rowIndex in Enumerable.Range(0, rows.Count))
        {
            var row = rows[rowIndex];
            var afterRank = afterRankById[row.RepairedCandidateId!];
            rows[rowIndex] = row with
            {
                RankAfter = afterRank,
                RecoveredAfterRanker = afterRank > 0,
                RecoveredBeforeRanker = row.RepairedCandidateId is not null
            };
        }

        var afterRanks = RankGold(snapshot.Lines, repaired, reranked, allGold);
        var afterCoverage = afterRanks.Select(rank => rank > 0 ? 1 : 0).ToArray();
        var baselinePresentChanged = gold.Select((_, index) => beforeCoverage[index] == 1 && afterCoverage[index] == 0)
            .Count(changed => changed);
        var displaced = gold.Select((_, index) => beforeRanks[index] is > 0 and <= 160 && afterRanks[index] > 160)
            .Count(wasDisplaced => wasDisplaced);
        return new DocumentResult(document.DocumentId, causalRows.Length == 0 ? "" :
            causalRows[0].GetProperty("DocumentSha256").GetString() ?? "",
            snapshot.CandidateBlocks.Count, repaired.Count, beforeCoverage, afterCoverage, beforeRanks, afterRanks,
            rows, displaced, baselinePresentChanged);
    }

    private static PdfSemanticBlock RebuildBlock(PdfSemanticBlock original, IReadOnlyList<PdfLine> lines)
    {
        var style = lines.GroupBy(line => PdfStyleClusterProfile.StyleOf(line))
            .OrderByDescending(group => group.Sum(line => PdfTextUtilities.Readable(line.Text).Length))
            .Select(group => group.Key).FirstOrDefault(original.PrimaryStyle);
        return new PdfSemanticBlock(original.Id, lines, style, lines[0].Page, lines.Max(line => line.Y),
            lines.Min(line => line.Y), lines.Min(line => line.Left), lines.Max(line => line.Right),
            PdfTextUtilities.Readable(string.Join(" ", lines.Select(line => line.Text))));
    }

    private static int[] RankGold(IReadOnlyList<PdfLine> sourceLines,
        IReadOnlyList<PdfSemanticBlock> blocks, IReadOnlyList<RankedCandidate> ranked, int[][] gold)
    {
        var provenance = blocks.Select(block => (block.Id, Lines: block.Lines.Select(PdfCandidateProvenance.LineId).ToHashSet(StringComparer.Ordinal)))
            .ToDictionary(item => item.Id, item => item.Lines, StringComparer.Ordinal);
        var rank = ranked.Select((candidate, index) => (candidate.SourceId, rank: index + 1))
            .ToDictionary(item => item.SourceId, item => item.rank, StringComparer.Ordinal);
        return gold.Select(lines =>
        {
            var ids = lines.Select(index => PdfCandidateProvenance.LineId(sourceLines[index]))
                .ToHashSet(StringComparer.Ordinal);
            return rank.Where(item => provenance[item.Key].IsSupersetOf(ids)).Select(item => item.Value)
                .DefaultIfEmpty(0).Min();
        }).ToArray();
    }

    private static string Shape(int[] required, IReadOnlyList<int> selected)
    {
        var left = selected.Count > 0 && selected.Min() > required.Min();
        var right = selected.Count > 0 && selected.Max() < required.Max();
        return left && right ? "WINDOW_FRAGMENT_SPLIT" : left ? "LEFT_TRUNCATION" :
            right ? "RIGHT_TRUNCATION" : "OTHER";
    }

    private static string Canonical(string lineId) =>
        new string(lineId.Split('|').Last().Where(char.IsLetterOrDigit).ToArray());

    private static string Recall(IEnumerable<int> ranks, int cutoff, int denominator) =>
        $"{ranks.Count(rank => rank > 0 && rank <= cutoff)}/{denominator}";
}
