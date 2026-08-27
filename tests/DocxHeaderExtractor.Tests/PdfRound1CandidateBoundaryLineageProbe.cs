using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>R1C.2: one-pass, behavior-neutral lineage report for the frozen 18 cases.</summary>
public sealed class PdfRound1CandidateBoundaryLineageProbe
{
    private static readonly (string DocumentId, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    [Fact]
    public void WriteBoundaryLineage()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1_BOUNDARY_LINEAGE");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var inputPath = Path.Combine(root, "eval", "accuracy-round1",
            "candidate-loss-causal-classification.v1.json");
        using var input = JsonDocument.Parse(File.ReadAllText(inputPath));
        var allRows = input.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(row => row.GetProperty("Owner").GetString() == "CANDIDATE_BOUNDARY_MISMATCH")
            .ToArray();
        Assert.Equal(18, allRows.Length);
        var shapePath = Path.Combine(root, "eval", "accuracy-round1", "candidate-boundary-counterfactual.v1.json");
        using var shapeArtifact = JsonDocument.Parse(File.ReadAllText(shapePath));
        var shapeById = shapeArtifact.RootElement.GetProperty("occurrences").EnumerateArray()
            .ToDictionary(row => row.GetProperty("GoldStableId").GetString()!,
                row => row.GetProperty("Shape").GetString()!, StringComparer.Ordinal);

        var rows = new List<object>();
        foreach (var document in Documents)
        {
            var documentRows = allRows.Where(row => row.GetProperty("DocumentId").GetString() == document.DocumentId).ToArray();
            if (documentRows.Length == 0) continue;
            var requests = documentRows.ToDictionary(
                row => row.GetProperty("GoldStableId").GetString()!,
                row => (IReadOnlyList<string>)row.GetProperty("SourceLineIds").EnumerateArray()
                    .Select(value => value.GetString()!).ToArray(),
                StringComparer.Ordinal);
            var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
            var lineage = PdfLayoutEvidenceOutline.TraceCandidateBoundaryLineage(docx, requests);
            Assert.Equal(documentRows.Length, lineage.Count);
            foreach (var item in lineage)
            {
                var original = documentRows.Single(row => row.GetProperty("GoldStableId").GetString() == item.OccurrenceId);
                var finalStage = item.Stages.Last();
                var classification = IsBoundaryGap(item)
                    ? "EVALUATOR_EXACT_JOIN_TOO_STRICT"
                    : "PRODUCTION_BOUNDARY_DEFECT";
                rows.Add(new
                {
                    documentId = document.DocumentId,
                    documentSha256 = original.GetProperty("DocumentSha256").GetString(),
                    goldStableId = item.OccurrenceId,
                    sourceLineIds = item.SourceLineIds,
                    sourceText = original.GetProperty("SourceText").GetString(),
                    shape = shapeById[item.OccurrenceId],
                    firstLossComponent = item.FirstLossComponent,
                    firstLossOperation = item.FirstLossOperation,
                    firstLossReason = item.FirstLossReason,
                    productionOwner = item.FirstLossComponent == "NONE" ? "UNRESOLVED" : item.FirstLossComponent,
                    classification,
                    finalCandidateIds = finalStage.CandidateLineIds.Keys,
                    stages = item.Stages
                });
            }
        }

        var ownerGroups = rows.Select(row => JsonSerializer.SerializeToElement(row))
            .GroupBy(row => row.GetProperty("firstLossComponent").GetString()!, StringComparer.Ordinal)
            .Select(group => new
            {
                component = group.Key,
                operation = group.Select(row => row.GetProperty("firstLossOperation").GetString()).Distinct().ToArray(),
                count = group.Count(),
                documents = group.Select(row => row.GetProperty("documentId").GetString()).Distinct().OrderBy(x => x).ToArray(),
                shapes = group.Select(row => row.GetProperty("shape").GetString()!).GroupBy(x => x)
                    .ToDictionary(g => g.Key, g => g.Count()),
                firstLossEvidence = group.Select(row => row.GetProperty("firstLossReason").GetString()).Distinct().ToArray(),
                repairInvariantAvailable = false
            }).ToArray();
        var classificationCounts = rows.Select(row => JsonSerializer.SerializeToElement(row))
            .GroupBy(row => row.GetProperty("classification").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round1_candidate_boundary_lineage",
            phase = "round1c2",
            modelCalls = 0,
            productionBehaviorChanged = false,
            rankingChanged = false,
            sourceAuthority = "Round1A b4685c8 + Round1B 4d7c814 + Round1C fecc835",
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is run-local diagnostics",
            total = 18,
            ownerResolved = rows.Count(row => JsonSerializer.SerializeToElement(row)
                .GetProperty("firstLossComponent").GetString() is not "NONE"),
            ownerUnresolved = rows.Count(row => JsonSerializer.SerializeToElement(row)
                .GetProperty("firstLossComponent").GetString() is "NONE"),
            classification = classificationCounts,
            owners = ownerGroups,
            occurrences = rows,
            finalStatus = rows.All(row => JsonSerializer.SerializeToElement(row)
                .GetProperty("firstLossComponent").GetString() is not "NONE")
                ? "BOUNDARY_OWNER_PROVEN"
                : "BOUNDARY_REMEDIATION_NOT_YET_JUSTIFIED"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void FrozenLineageHas18Rows()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1_BOUNDARY_LINEAGE");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(18, report.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(18, report.RootElement.GetProperty("occurrences").GetArrayLength());
        Assert.Equal(18, report.RootElement.GetProperty("ownerResolved").GetInt32());
    }

    private static bool IsBoundaryGap(PdfCandidateBoundaryLineage item) =>
        item.Stages.Last().CandidateLineIds.Values.Any(lines =>
            item.SourceLineIds.All(line => lines.Contains(line, StringComparer.Ordinal)));

    private static string Shape(JsonElement row)
    {
        var lines = row.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var stages = row.GetProperty("stages");
        var final = stages[stages.GetArrayLength() - 1];
        var candidate = final.GetProperty("CandidateLineIds").EnumerateObject().FirstOrDefault();
        if (candidate.Value.ValueKind == JsonValueKind.Undefined) return "OTHER";
        var candidateLines = candidate.Value.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        var first = lines.FirstOrDefault(line => candidateLines.Contains(line));
        var last = lines.LastOrDefault(line => candidateLines.Contains(line));
        return first is null ? "LEFT_TRUNCATION" : last is null ? "RIGHT_TRUNCATION" : "WINDOW_FRAGMENT_SPLIT";
    }
}
