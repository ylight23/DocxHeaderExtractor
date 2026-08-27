using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Freezes the Round 5 architecture comparison from committed Round 3/4 artifacts. This is an
/// inventory and feasibility report only: no gold-dependent score is promoted into production and
/// no provider call is made.
/// </summary>
public sealed class PdfRound5LightweightRerankerFeasibilityProbe
{
    [Fact]
    public void WriteFeasibilityArtifact()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var round3Path = Path.Combine(root, "eval", "accuracy-round3", "selection-architecture-feasibility.v1.json");
        var round4Path = Path.Combine(root, "eval", "accuracy-round4", "semantic-pool-feasibility.v1.json");
        if (!File.Exists(round3Path) || !File.Exists(round4Path)) return;

        var round3 = JsonNode.Parse(File.ReadAllText(round3Path))!;
        var round4 = JsonNode.Parse(File.ReadAllText(round4Path))!;
        var table = new JsonArray();
        foreach (var k in new[] { 80, 160, 320 })
        {
            var curve = round3["curve"]!.AsArray().Single(c => c!["K"]!.GetValue<string>() == k.ToString(CultureInfo.InvariantCulture));
            table.Add(new JsonObject
            {
                ["sourcePool"] = "unchanged_deterministic_ranked_pool",
                ["shortlistK"] = k,
                ["reviewedHeadingsInShortlist"] = curve["SelectedReviewedHeadings"]!.DeepClone(),
                ["selectionCoverageOfFullCandidate"] = curve["SelectionCoverage"]!.DeepClone(),
                ["overallPreSemanticRecall"] = curve["OverallPreSemanticRecall"]!.DeepClone(),
                ["expensiveSemanticBatches"] = curve["EstimatedRequestCount"]!.DeepClone(),
                ["relativeCostVs160"] = curve["RelativeCostVsK160"]!.DeepClone(),
                ["rerankerApplied"] = false,
                ["status"] = "baseline_only"
            });
        }

        var k640 = round4["runs"]!.AsArray().Single(r => r!["K"]!.GetValue<int>() == 640);
        var k1024 = round4["runs"]!.AsArray().Single(r => r!["K"]!.GetValue<int>() == 1024);
        var result = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy_round5_lightweight_reranker_feasibility",
            ["phase"] = "round5a-5b-5c-5d",
            ["reference"] = new JsonObject
            {
                ["round2"] = "d51cf62 / 45d47ad",
                ["round3"] = "4a38f0f",
                ["round4"] = "d4bea07"
            },
            ["modelCalls"] = false,
            ["productionChanges"] = false,
            ["candidateGenerationChanged"] = false,
            ["rankingChanged"] = false,
            ["developmentPopulation"] = new JsonArray("004", "030", "043", "058"),
            ["frozenInputs"] = new JsonObject
            {
                ["deterministicPoolSizes"] = new JsonArray(640, 1024),
                ["expensiveSemanticShortlists"] = new JsonArray(80, 160, 320),
                ["fullCandidate"] = 375,
                ["reviewed"] = 422,
                ["baselineTop160ReviewedHeadings"] = 80
            },
            ["availablePreSemanticFeatures"] = new JsonArray(
                "source/layout facts",
                "marker facts",
                "representation kind",
                "deterministic score and signal lists",
                "neighbor/context facts",
                "structural scope",
                "document regime",
                "cheap lexical/structural facts"),
            ["forbiddenRuntimeInputs"] = new JsonArray("goldStableId", "silver labels", "validated outputs", "future semantic decisions"),
            ["baselineShortlistTable"] = table,
            ["observedLargerPoolExecution"] = new JsonObject
            {
                ["K640"] = new JsonObject
                {
                    ["poolHeadingCoverage"] = "184/422",
                    ["validated"] = k640["totals"]!["validated"]!.DeepClone(),
                    ["grounded"] = k640["totals"]!["grounded"]!.DeepClone(),
                    ["spanLaneStatus"] = "partial_timeout",
                    ["providerRequests"] = k640["execution"]!["providerRequestCount"]!.DeepClone()
                },
                ["K1024"] = new JsonObject
                {
                    ["poolHeadingCoverage"] = "266/422",
                    ["validated"] = k1024["totals"]!["validated"]!.DeepClone(),
                    ["grounded"] = k1024["totals"]!["grounded"]!.DeepClone(),
                    ["spanLaneStatus"] = "partial_timeout",
                    ["providerRequests"] = k1024["execution"]!["providerRequestCount"]!.DeepClone()
                }
            },
            ["architectureOptions"] = new JsonArray(
                Option("A_existing_deterministic_recombination", "not_measured", "low", "none", "high", "yes", "not_justified_without_proven_score_responsibility"),
                Option("B_cheap_embedding_similarity", "not_measured", "medium", "likely held-out calibration", "medium", "possible", "investigation_only"),
                Option("C_small_classifier_reranker", "not_measured", "medium", "representative labels required", "medium", "possible", "investigation_only"),
                Option("D_9B_rank_only_pass", "not_measured", "higher than cheap reranker; lower than full role-span pass", "none, but provider-dependent", "high after source grounding", "possible", "not_run")),
            ["decision"] = new JsonObject
            {
                ["status"] = "RERANKER_FEASIBILITY_UNRESOLVED",
                ["reason"] = "Round 3 proves a larger pool contains more reviewed headings, while Round 4 proves the unchanged full semantic pipeline cannot process K=640/1024 under the frozen deadline. No pre-semantic causal score responsibility is proven yet, so a gold-optimized rerank counterfactual would be tuning rather than feasibility evidence.",
                ["nextAllowedStep"] = "freeze one non-gold feature contract and run one bounded offline counterfactual before any production change",
                ["productionImplementation"] = "not_started"
            }
        };

        var directory = Path.Combine(root, "eval", "accuracy-round5");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "lightweight-reranker-feasibility.v1.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject Option(string option, string recall, string cost, string labels, string auditability, string failClosed, string decision) => new()
    {
        ["option"] = option,
        ["expectedShortlistRecall"] = recall,
        ["relativeCost"] = cost,
        ["trainingOrCalibration"] = labels,
        ["auditability"] = auditability,
        ["failClosedCompatibility"] = failClosed,
        ["decision"] = decision
    };
}
