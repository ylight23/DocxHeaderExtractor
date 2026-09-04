using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Reconciles the frozen Round 4 semantic runs without making provider calls.
/// The pdf-hierarchy-facts route does not materialize the final product projection or request
/// start times, so those values remain explicitly unmeasured rather than being inferred.
/// </summary>
public sealed class PdfRound4SemanticPoolFeasibilityProbe
{
    private static readonly (string Id, string File, string GoldFile)[] Documents =
    [
        ("004", "004_Luat_Dau_tu_61-2020-QH14_EN.docx", "004-n3.2-silver-model-assisted.v1.json"),
        ("030", "030_WB_RFP_Consulting_Services_2019.docx", "030-n3.2-silver-model-assisted.v1.json"),
        ("043", "043_IBRD_Financial_Statements_June_2024.docx", "043-n3.2-silver-model-assisted.v1.json"),
        ("058", "058_Machine_Learning_Lecture_Note.docx", "058-n3.2-silver-model-assisted.v1.json"),
    ];

    [Fact]
    public void WriteFeasibilityArtifact()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "accuracy-round4");
        var output = Path.Combine(directory, "semantic-pool-feasibility.v1.json");
        if (!File.Exists(Path.Combine(directory, "k640-semantic-run.v1.json")) ||
            !File.Exists(Path.Combine(directory, "k1024-semantic-run.v1.json"))) return;

        var result = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy_round4_semantic_pool_feasibility",
            ["phase"] = "round4b-4c-4d",
            ["modelCalls"] = true,
            ["productionChanges"] = false,
            ["candidateGenerationChanged"] = false,
            ["rankingChanged"] = false,
            ["developmentPopulation"] = new JsonArray("004", "030", "043", "058"),
            ["frozenBudgets"] = new JsonArray(640, 1024),
            ["executionContract"] = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "k640-manifest.json")))!["frozenProfile"]!.DeepClone(),
            ["baseline"] = new JsonObject
            {
                ["reference"] = "Round 3 @ 4a38f0f",
                ["K"] = 160,
                ["reviewed"] = 422,
                ["fullCandidate"] = 375,
                ["selectedReviewedHeadings"] = 80,
                ["poolCoverage"] = "80/375",
                ["overallPreSemanticRecall"] = "80/422",
                ["semanticMetrics"] = "not_measured_in_frozen_round3_artifact"
            },
            ["runs"] = new JsonArray(BuildRun(root, 640), BuildRun(root, 1024)),
            ["comparison"] = BuildComparison(root),
            ["decision"] = new JsonObject
            {
                ["status"] = "LARGER_POOL_EXISTING_SEMANTIC_NOT_FEASIBLE",
                ["reason"] = "K=640 completed semantic role work but every document ended span lane partial_timeout; K=1024 reached the same lane boundary with zero downstream facts. No material end-to-end recovery can be claimed from these runs.",
                ["productionKRecommendation"] = "not_selected",
                ["learnedRerankerInvestigation"] = "not_opened"
            },
            ["interpretation"] = new JsonArray(
                "Pool coverage is measured against 422 reviewed headings and 375 full candidates; semantic conditional metrics are separate.",
                "K=1024 zero validated/grounded items are execution outcomes under the frozen lane deadline, not evidence that the model rejected all candidates.",
                "The route does not expose final emitted product headings or request start timestamps; those fields are not fabricated."
            )
        };

        var expected = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var committed = File.ReadAllText(output);
        Assert.Equal(committed, expected);
    }

    private static JsonObject BuildRun(string root, int k)
    {
        var directory = Path.Combine(root, "eval", "accuracy-round4");
        var route = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, $"k{k}-semantic-run.v1.json")))!;
        var checkpoints = File.ReadAllLines(Path.Combine(directory, $"k{k}-role-span.jsonl"))
            .Select(line => JsonNode.Parse(line)!)
            .ToArray();
        var semanticBatchCount = checkpoints.Count(c => c["lane"]?.GetValue<string>() == "semantic");
        var spanBatchCount = checkpoints.Count(c => c["lane"]?.GetValue<string>() == "span");
            var rows = new JsonArray();
            foreach (var document in Documents)
            {
                var row = route["rows"]!.AsArray().Single(r => r!["file"]!.GetValue<string>().StartsWith(document.Id + "_", StringComparison.Ordinal));
                var gold = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eval", "benchmark-n3", "silver-labels", document.GoldFile)))!;
                var goldOccurrences = gold["headingOccurrences"]!.AsArray();
                var routeItems = row["items"]?.AsArray() ?? new JsonArray();
                var exact = goldOccurrences.Count(g => routeItems.Any(item => SameLines(g!["sourceLineIds"]!, item!["lineIds"]!)));
                var pool = LoadRound3PoolRow(root, document.Id, k);
                rows.Add(new JsonObject
                {
                ["documentId"] = document.Id,
                ["K"] = k,
                ["candidatesSentToSemantic"] = k,
                ["reviewedHeadings"] = goldOccurrences.Count,
                ["reviewedHeadingsAvailableInPool"] = pool["SelectedReviewedHeadings"]!.DeepClone(),
                ["poolCoverage"] = $"{pool["SelectedReviewedHeadings"]!.GetValue<int>()}/{goldOccurrences.Count}",
                ["roleAccepted"] = null,
                ["spanResolved"] = null,
                ["validated"] = row["counters"]?["validatedHeadings"]?.GetValue<int>() ?? 0,
                ["grounded"] = row["canonicalGroundings"]?.AsArray().Count ?? 0,
                ["emitted"] = null,
                ["exactReviewedHeadingsRecovered"] = exact,
                ["partialSameOccurrence"] = null,
                ["unmatchedOutputs"] = null,
                ["semanticLaneStatus"] = row["semanticLaneStatus"]?.GetValue<string>() ?? "unknown",
                ["spanLaneStatus"] = row["spanLaneStatus"]?.GetValue<string>() ?? "unknown",
                ["requestCount"] = null,
                ["batchCount"] = null,
                ["wallTimeSeconds"] = null,
                ["timeouts"] = row["spanLaneStatus"]?.GetValue<string>() == "partial_timeout" ? 1 : 0,
                ["providerCalls"] = null,
                ["notMeasured"] = new JsonArray("roleAccepted", "spanResolved", "emitted", "partialSameOccurrence", "unmatchedOutputs", "requestCount", "batchCount", "wallTimeSeconds")
            });
        }

        return new JsonObject
        {
            ["K"] = k,
            ["documents"] = rows,
            ["execution"] = new JsonObject
            {
                ["semanticBatchCount"] = semanticBatchCount,
                ["spanBatchCount"] = spanBatchCount,
                ["providerRequestCount"] = semanticBatchCount + spanBatchCount,
                ["checkpointRecordCount"] = checkpoints.Length,
                ["wallTimeSeconds"] = null,
                ["timeoutCount"] = rows.Count(r => r!["spanLaneStatus"]?.GetValue<string>() == "partial_timeout")
            },
            ["totals"] = Totals(rows)
        };
    }

    private static JsonObject BuildComparison(string root)
    {
        var round3 = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eval", "accuracy-round3", "selection-architecture-feasibility.v1.json")))!;
        var rows = new JsonArray();
        foreach (var k in new[] { 160, 640, 1024 })
        {
            var curve = round3["curve"]!.AsArray().Single(c => c!["K"]!.GetValue<string>() == k.ToString(CultureInfo.InvariantCulture));
            rows.Add(new JsonObject
            {
                ["K"] = k,
                ["reviewedHeadingSelected"] = curve["SelectedReviewedHeadings"]!.DeepClone(),
                ["selectionCoverage"] = curve["SelectionCoverage"]!.DeepClone(),
                ["overallPreSemanticRecall"] = curve["OverallPreSemanticRecall"]!.DeepClone(),
                ["semanticRuns"] = k == 160 ? "not_measured" : "executed",
                ["relativeCostVs160"] = curve["RelativeCostVsK160"]!.DeepClone(),
                ["estimatedSemanticBatches"] = curve["EstimatedRequestCount"]!.DeepClone()
            });
        }
        return new JsonObject
        {
            ["denominators"] = new JsonObject { ["reviewed"] = 422, ["fullCandidate"] = 375 },
            ["table"] = rows
        };
    }

    private static JsonObject LoadRound3PoolRow(string root, string id, int k)
    {
        var round3 = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eval", "accuracy-round3", "selection-architecture-feasibility.v1.json")))!;
        return round3["perDocument"]!.AsArray().Single(d => d!["documentId"]!.GetValue<string>() == id)!["curve"]!.AsArray().Single(c => c!["K"]!.GetValue<string>() == k.ToString(CultureInfo.InvariantCulture)).AsObject();
    }

    private static JsonObject Totals(JsonArray rows) => new()
    {
        ["candidatesSentToSemantic"] = rows.Sum(r => r!["candidatesSentToSemantic"]!.GetValue<int>()),
        ["reviewedHeadingsAvailableInPool"] = rows.Sum(r => r!["reviewedHeadingsAvailableInPool"]!.GetValue<int>()),
        ["validated"] = rows.Sum(r => r!["validated"]!.GetValue<int>()),
        ["grounded"] = rows.Sum(r => r!["grounded"]!.GetValue<int>()),
        ["exactReviewedHeadingsRecovered"] = rows.Sum(r => r!["exactReviewedHeadingsRecovered"]!.GetValue<int>())
    };

    private static bool SameLines(JsonNode expected, JsonNode actual) =>
        expected.AsArray().Select(x => x!.GetValue<string>()).OrderBy(x => x).SequenceEqual(
            actual.AsArray().Select(x => x!.GetValue<string>()).OrderBy(x => x));
}
