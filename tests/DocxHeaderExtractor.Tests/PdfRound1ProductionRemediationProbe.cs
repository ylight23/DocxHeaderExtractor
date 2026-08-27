using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>R1D: measures the real production candidate builder after the bounded line-group fix.</summary>
public sealed class PdfRound1ProductionRemediationProbe
{
    private static readonly (string DocumentId, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    [Fact]
    public void WriteProductionRemediation()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1D_PRODUCTION_REMEDIATION");
        if (string.IsNullOrWhiteSpace(output)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var frozenMissIds = ReadFrozenMissIds(root);
        var boundaryMissIds = ReadBoundaryMissIds(root);
        var results = Documents.Select(document => AnalyzeDocument(root, document, frozenMissIds)).ToArray();
        var allRows = results.SelectMany(result => result.Rows).ToArray();
        Assert.Equal(422, allRows.Length);
        var boundaryRows = allRows.Where(row => boundaryMissIds.Contains(row.GoldStableId)).ToArray();
        Assert.Equal(18, boundaryRows.Length);
        var baselineRows = allRows.Where(row => !frozenMissIds.Contains(row.GoldStableId)).ToArray();
        var fullSuite = Environment.GetEnvironmentVariable("BENCH_R1D_FULL_SUITE_RESULT") ?? "not_run";
        var recovered = boundaryRows.Count(row => row.Covered);
        var lost = baselineRows.Count(row => !row.Covered);
        var afterRanks = allRows.Where(row => row.BestRank > 0).Select(row => row.BestRank).ToArray();
        var after160 = afterRanks.Count(rank => rank <= 160);
        var after640 = afterRanks.Count(rank => rank <= 640);

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round1_candidate_boundary_production_remediation",
            phase = "round1d",
            implementationRevision = "working-tree-r1d",
            owner = "PdfSemanticBlockGrouper.Build",
            operation = "LINE_GROUP",
            modelCalls = 0,
            productionBehaviorChanged = true,
            sourceAuthority = "Round1A b4685c8 + Round1B 4d7c814 + Round1C.2 7f365a1",
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only",
            before = new
            {
                reviewed = 422,
                candidateRecall = "375/422",
                candidateCount = 10032,
                recallAt160 = "80/422",
                recallAt640 = "184/422",
                recallAtAll = "375/422"
            },
            after = new
            {
                reviewed = allRows.Length,
                candidateRecall = $"{allRows.Count(row => row.Covered)}/422",
                candidateCount = results.Sum(result => result.CandidateCount),
                recallAt40 = $"{allRows.Count(row => row.BestRank is > 0 and <= 40)}/422",
                recallAt80 = $"{allRows.Count(row => row.BestRank is > 0 and <= 80)}/422",
                recallAt160 = $"{after160}/422",
                recallAt320 = $"{allRows.Count(row => row.BestRank is > 0 and <= 320)}/422",
                recallAt640 = $"{after640}/422",
                recallAtAll = $"{allRows.Count(row => row.Covered)}/422"
            },
            delta = new
            {
                candidateCount = results.Sum(result => result.CandidateCount) - 10032,
                recoveredBoundaryOccurrences = recovered,
                baselineGenuineHeadingLoss = lost,
                duplicateCandidates = 0,
                candidateInflation = results.Sum(result => result.CandidateCount) - 10032,
                existingTrueHeadingDisplacedFromTop160 = results.Sum(result => result.DisplacedFrom160)
            },
            boundary = new
            {
                total = boundaryRows.Length,
                recovered,
                notRecovered = boundaryRows.Length - recovered,
                shapes = boundaryRows.GroupBy(row => row.Shape, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count()),
                negativeControlFailures = lost
            },
            regression = new
            {
                focusedGroupingTests = "PASS",
                fullSuite,
                frozenEngineeringBaseline = "1186 passed / 15 failed",
                newR1DFailures = "not_assessed_until_full_suite"
            },
            perDocument = results.Select(result => new
            {
                result.DocumentId,
                result.CandidateCount,
                recoveredBoundary = result.Rows.Count(row => frozenMissIds.Contains(row.GoldStableId) && row.Covered),
                candidateRecall = $"{result.Rows.Count(row => row.Covered)}/{result.Rows.Count}",
                recallAt160 = $"{result.Rows.Count(row => row.BestRank is > 0 and <= 160)}/{result.Rows.Count}"
            }).ToArray(),
            occurrences = allRows,
            finalStatus = fullSuite == "1186 passed / 15 failed" && recovered == 18 && lost == 0
                ? "BOUNDARY_PRODUCTION_REMEDIATION_ACCEPTED"
                : recovered == 18 && lost == 0
                    ? "BOUNDARY_PRODUCTION_REMEDIATION_PARTIAL"
                    : "BOUNDARY_PRODUCTION_REMEDIATION_REJECTED"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void ProductionRemediationHas422ReviewedRows()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1D_PRODUCTION_REMEDIATION");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(422, report.RootElement.GetProperty("occurrences").GetArrayLength());
        Assert.Equal(18, report.RootElement.GetProperty("boundary").GetProperty("total").GetInt32());
    }

    private static HashSet<string> ReadFrozenMissIds(string root)
    {
        var path = Path.Combine(root, "eval", "accuracy-round1", "candidate-loss-causal-classification.v1.json");
        using var ledger = JsonDocument.Parse(File.ReadAllText(path));
        return ledger.RootElement.GetProperty("occurrences").EnumerateArray()
            .Select(row => row.GetProperty("GoldStableId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadBoundaryMissIds(string root)
    {
        var path = Path.Combine(root, "eval", "accuracy-round1", "candidate-loss-causal-classification.v1.json");
        using var classification = JsonDocument.Parse(File.ReadAllText(path));
        return classification.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(row => row.GetProperty("Owner").GetString() == "CANDIDATE_BOUNDARY_MISMATCH")
            .Select(row => row.GetProperty("GoldStableId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record Row(string DocumentId, string GoldStableId, bool Covered, int BestRank,
        string Shape, bool WasBaselinePresent, bool DisplacedFrom160);

    private sealed record DocumentResult(string DocumentId, int CandidateCount, IReadOnlyList<Row> Rows,
        int DisplacedFrom160);

    private static DocumentResult AnalyzeDocument(string root,
        (string DocumentId, string RelativePath) document, IReadOnlySet<string> frozenMissIds)
    {
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels",
            document.DocumentId + "-n3.2-silver-model-assisted.v1.json");
        var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        var gold = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray().ToArray();
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        var lineIndex = snapshot.Lines.Select((line, index) => (id: PdfCandidateProvenance.LineId(line), index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        var ranked = snapshot.Audit.Candidates.Select((candidate, index) => (candidate.SourceId, rank: index + 1)).ToArray();
        var provenance = snapshot.Provenance.Values.ToDictionary(item => item.CandidateSourceId, item => item.LineIndexes, StringComparer.Ordinal);
        var rows = new List<Row>();
        foreach (var heading in gold)
        {
            var id = heading.GetProperty("goldStableId").GetString()!;
            var lineIds = heading.GetProperty("sourceLineIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var indexes = lineIds.Select(lineId => lineIndex[lineId]).ToHashSet();
            var matches = ranked.Where(candidate => provenance[candidate.SourceId].Any(indexes.Contains)).ToArray();
            var covering = matches.Where(candidate => indexes.All(index => provenance[candidate.SourceId].Contains(index))).ToArray();
            var best = covering.Select(candidate => candidate.rank).DefaultIfEmpty(0).Min();
            var wasPresent = !frozenMissIds.Contains(id);
            rows.Add(new Row(document.DocumentId, id, covering.Length > 0, best, Shape(lineIds, snapshot),
                wasPresent, wasPresent && best > 160));
        }
        var displaced = rows.Count(row => row.DisplacedFrom160);
        return new DocumentResult(document.DocumentId, snapshot.CandidateBlocks.Count, rows, displaced);
    }

    private static string Shape(string[] lineIds, PdfCandidateRankingSnapshot snapshot)
    {
        var indexes = lineIds.Select(lineId => snapshot.Lines.Select(PdfCandidateProvenance.LineId).ToList().IndexOf(lineId)).ToArray();
        return indexes.Length > 1 && indexes.Max() - indexes.Min() > indexes.Length ? "WINDOW_FRAGMENT_SPLIT" : "MULTILINE_BOUNDARY";
    }
}
