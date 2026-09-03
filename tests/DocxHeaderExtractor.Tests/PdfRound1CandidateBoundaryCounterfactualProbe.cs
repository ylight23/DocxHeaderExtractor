using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Round 1C: offline counterfactual for the 18 frozen boundary mismatches. It does not alter
/// production construction or ranking. An existing touching candidate is hypothetically widened
/// to the reviewed source-line set while retaining its observed rank and score.
/// </summary>
public sealed class PdfRound1CandidateBoundaryCounterfactualProbe
{
    private static readonly (string DocumentId, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    private sealed record CounterfactualRow(
        string DocumentId,
        string DocumentSha256,
        string GoldStableId,
        string[] ReviewedSourceLineIds,
        string SourceText,
        string[] TouchingCandidateIds,
        string? SelectedCandidateId,
        int? ObservedRank,
        string Shape,
        string ProducerOwner,
        string RepresentationKind,
        string StructuralScope,
        string[] MissingLineIds,
        string[] ExtraLineIds,
        bool RecoveredByBoundaryRepair,
        bool RecoveredAt160,
        bool RecoveredAt320,
        bool RecoveredAt640,
        string Evidence);

    private sealed record CandidateView(string Id, int Rank, int[] LineIndexes, string Kind);

    [Fact]
    public void WriteBoundaryCounterfactual()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1_BOUNDARY_COUNTERFACTUAL");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var rows = Documents.SelectMany(document => AnalyzeDocument(root, document)).ToArray();
        Assert.Equal(18, rows.Length);

        var shapes = rows.GroupBy(row => row.Shape, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new
            {
                count = group.Count(),
                documents = group.Select(row => row.DocumentId).Distinct().OrderBy(x => x).ToArray(),
                producerOwner = group.Select(row => row.ProducerOwner).Distinct().OrderBy(x => x).ToArray(),
                recovered = group.Count(row => row.RecoveredByBoundaryRepair),
                newCandidates = 0,
                inflation = 0,
                recallAt160Delta = group.Count(row => row.RecoveredAt160),
                recallAt320Delta = group.Count(row => row.RecoveredAt320),
                recallAt640Delta = group.Count(row => row.RecoveredAt640)
            }, StringComparer.Ordinal);

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round1_candidate_boundary_counterfactual",
            phase = "round1c",
            modelCalls = 0,
            productionChanges = false,
            sourceAuthority = "Round1A b4685c8 + Round1B 4d7c814",
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is run-local diagnostics",
            boundaryMismatchCount = 18,
            input = new { reviewed = 422, candidatePresent = 375, frozenMisses = 47 },
            simulation = new
            {
                repair = "widen one existing touching candidate to cover the reviewed source lines",
                candidateSelection = "highest-ranked touching candidate",
                scorePolicy = "retain observed candidate score and rank; score recomputation not performed",
                newCandidates = 0,
                candidateInflation = 0,
                rankingInterpretation = "recovered-at-K uses retained observed rank; this is an upper-bound structural simulation, not a rerun of the ranker"
            },
            baseline = new
            {
                candidateRecall = "375/422",
                recallAt40 = "not available from frozen census",
                recallAt80 = "not available from frozen census",
                recallAt160 = "not available from frozen census",
                recallAt320 = "not available from frozen census",
                recallAt640 = "not available from frozen census",
                recallAtAll = "375/422"
            },
            counterfactual = new
            {
                recoveredTrueHeadings = rows.Count(row => row.RecoveredByBoundaryRepair),
                newExtraCandidates = 0,
                duplicateCandidates = 0,
                candidateInflation = 0,
                recallAt40 = "not measurable from frozen candidate census",
                recallAt80 = "not measurable from frozen candidate census",
                recallAt160 = $"{375 + rows.Count(row => row.RecoveredAt160)}/422",
                recallAt320 = $"{375 + rows.Count(row => row.RecoveredAt320)}/422",
                recallAt640 = $"{375 + rows.Count(row => row.RecoveredAt640)}/422",
                recallAtAll = $"{375 + rows.Count(row => row.RecoveredByBoundaryRepair)}/422"
            },
            negativeControls = new
            {
                candidatePresentOccurrences = 375,
                changed = 0,
                status = "PASS for simulation scope; only the frozen 18 miss identities are eligible",
                unavailableControls = new[] { "nearby body paragraphs", "unreviewed wrapped headings", "table/TOC non-heading population" }
            },
            shapes,
            occurrences = rows,
            status = rows.All(row => row.RecoveredByBoundaryRepair)
                ? "BOUNDARY_REMEDIATION_CANDIDATE"
                : "BOUNDARY_REMEDIATION_NOT_JUSTIFIED"
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void FrozenBoundaryCounterfactualHas18Rows()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1_BOUNDARY_COUNTERFACTUAL");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(18, report.RootElement.GetProperty("boundaryMismatchCount").GetInt32());
        Assert.Equal(18, report.RootElement.GetProperty("occurrences").GetArrayLength());
        Assert.Equal(0, report.RootElement.GetProperty("simulation").GetProperty("newCandidates").GetInt32());
    }

    private static IEnumerable<CounterfactualRow> AnalyzeDocument(string root,
        (string DocumentId, string RelativePath) document)
    {
        var causalPath = Path.Combine(root, "eval", "accuracy-round1", "candidate-loss-causal-classification.v1.json");
        using var causalJson = JsonDocument.Parse(File.ReadAllText(causalPath));
        var causalRows = causalJson.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(row => row.GetProperty("Owner").GetString() == "CANDIDATE_BOUNDARY_MISMATCH" &&
                          row.GetProperty("DocumentId").GetString() == document.DocumentId)
            .ToArray();

        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels",
            document.DocumentId + "-n3.2-silver-model-assisted.v1.json");
        var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        var gold = silver.RootElement.GetProperty("headingOccurrences")
            .EnumerateArray().ToDictionary(item => item.GetProperty("goldStableId").GetString()!, StringComparer.Ordinal);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        var lineIndex = snapshot.Lines.Select((line, index) => (id: PdfCandidateProvenance.LineId(line), index))
            .GroupBy(item => item.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        var rankById = snapshot.Audit.Candidates.Select((candidate, index) => (candidate.SourceId, rank: index + 1))
            .ToDictionary(item => item.SourceId, item => item.rank, StringComparer.Ordinal);
        var views = snapshot.Provenance.Values.Select(provenance => new CandidateView(
            provenance.CandidateSourceId,
            rankById.TryGetValue(provenance.CandidateSourceId, out var rank) ? rank : int.MaxValue,
            provenance.LineIndexes.ToArray(),
            provenance.RepresentationKind.ToString())).ToArray();

        foreach (var causalRow in causalRows)
        {
            var stableId = causalRow.GetProperty("GoldStableId").GetString()!;
            var documentSha256 = causalRow.GetProperty("DocumentSha256").GetString()!;
            var heading = gold[stableId];
            var lineIds = heading.GetProperty("sourceLineIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var indexes = lineIds.Select(id => lineIndex[id]).ToArray();
            var touching = views.Where(view => view.LineIndexes.Any(indexes.Contains)).ToArray();
            var selected = touching.OrderBy(view => view.Rank).FirstOrDefault();
            var union = touching.SelectMany(view => view.LineIndexes).Distinct().OrderBy(index => index).ToArray();
            var missing = indexes.Where(index => !union.Contains(index))
                .Select(index => lineIds[Array.IndexOf(indexes, index)]).ToArray();
            var extras = union.Where(index => !indexes.Contains(index))
                .Select(index => PdfCandidateProvenance.LineId(snapshot.Lines[index])).ToArray();
            var shape = Shape(indexes, selected?.LineIndexes ?? [], union);
            // The counterfactual is specifically a boundary repair of the selected existing
            // candidate. Its pre-repair coverage is intentionally incomplete; touching is the
            // eligibility condition and the repaired span is assumed to cover all reviewed lines.
            var recovered = selected is not null;
            yield return new CounterfactualRow(
                document.DocumentId,
                documentSha256,
                stableId,
                lineIds,
                heading.GetProperty("sourceText").GetString()!,
                touching.Select(view => view.Id).ToArray(),
                selected?.Id,
                selected?.Rank,
                shape,
                "not_exposed_by_snapshot",
                selected?.Kind ?? "unknown",
                "observed_scope_not_exposed_by_snapshot",
                missing,
                extras,
                recovered,
                recovered && selected!.Rank <= 160,
                recovered && selected.Rank <= 320,
                recovered && selected.Rank <= 640,
                recovered ? "existing candidate widened; no new source fact; observed rank retained" : "no touching candidate exists");
        }
    }

    private static string Shape(int[] required, int[] selected, int[] union)
    {
        if (selected.Length == 0) return "OTHER";
        if (selected.All(required.Contains))
        {
            if (selected.Length < required.Length)
            {
                var left = selected.Min() > required.Min();
                var right = selected.Max() < required.Max();
                return left && right ? "WINDOW_FRAGMENT_SPLIT" : left ? "LEFT_TRUNCATION" : "RIGHT_TRUNCATION";
            }
            return "SUBSET";
        }
        if (union.Length > selected.Length) return "SUPERSET";
        return "OTHER";
    }
}
