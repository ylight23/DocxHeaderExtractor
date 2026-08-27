using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Records the bounded Round 1F producer counterfactual gate. It intentionally does not invent a
/// relaxed predicate: without a selective invariant, a simulated admission would be gold-tuning.
/// </summary>
public sealed class PdfProcurementProducerCounterfactualProbe
{
    [Fact]
    public void WriteProducerCounterfactualGate()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var inputPath = Path.Combine(root, "eval", "accuracy-procurement-recurrence", "procurement-recurrence.v1.json");
        if (!File.Exists(inputPath)) return;
        var input = JsonNode.Parse(File.ReadAllText(inputPath))!;
        var documents = input["documents"]!.AsArray();
        var broadMisses = documents.Sum(d => d!["firstLossCounts"]?["BuildBroadCandidates"]?.GetValue<int>() ?? 0);
        var supplementMisses = documents.Sum(d => d!["firstLossCounts"]?["BuildSupplementCandidates"]?.GetValue<int>() ?? 0);

        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy_round1_procurement_producer_counterfactual",
            ["phase"] = "round1f-a-round1f-b",
            ["modelCalls"] = false,
            ["productionChanges"] = false,
            ["rankingChanged"] = false,
            ["candidateGenerationChanged"] = false,
            ["authority"] = "procurement-recurrence.v1.json @ 64e214f",
            ["identity"] = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostic-only",
            ["producerLanes"] = new JsonArray(
                new JsonObject
                {
                    ["producer"] = "BuildBroadCandidates",
                    ["recurringMissesObserved"] = broadMisses,
                    ["knownTrigger"] = "CandidateStyles.Contains(block.PrimaryStyle) AND LooksLikeBroadCandidateBlock(block)",
                    ["sourceResponsibility"] = "style gate plus broad shape gate",
                    ["counterfactual"] = "not applied: no source-selective invariant proven; relaxing either predicate would be gold-tuning",
                    ["recoveredTrueHeadings"] = null,
                    ["newCandidates"] = null,
                    ["duplicates"] = null,
                    ["candidateInflation"] = null,
                    ["recallAllDelta"] = null,
                    ["recall160Delta"] = null,
                    ["existingTrueHeadingsDisplaced"] = null,
                    ["negativeControls"] = "not measured because no safe predicate relaxation was defined",
                    ["status"] = "PRODUCER_REMEDIATION_NOT_JUSTIFIED"
                },
                new JsonObject
                {
                    ["producer"] = "BuildSupplementCandidates",
                    ["recurringMissesObserved"] = supplementMisses,
                    ["knownTrigger"] = "non-excluded atomic/loose/fragment block AND LooksLikeSupplementBlock AND canonical dedup",
                    ["sourceResponsibility"] = "supplement path selection, shape gate and canonical dedup",
                    ["counterfactual"] = "not applied: atomic, loose and adjacent-fragment paths have different collateral surfaces; no shared invariant proven",
                    ["recoveredTrueHeadings"] = null,
                    ["newCandidates"] = null,
                    ["duplicates"] = null,
                    ["candidateInflation"] = null,
                    ["recallAllDelta"] = null,
                    ["recall160Delta"] = null,
                    ["existingTrueHeadingsDisplaced"] = null,
                    ["negativeControls"] = "not measured because no safe predicate relaxation was defined",
                    ["status"] = "PRODUCER_REMEDIATION_NOT_JUSTIFIED"
                }),
            ["observedMissCounts"] = new JsonObject
            {
                ["030"] = new JsonObject { ["BuildBroadCandidates"] = 14, ["BuildSupplementCandidates"] = 12 },
                ["028"] = new JsonObject { ["BuildBroadCandidates"] = 10, ["BuildSupplementCandidates"] = 15 },
                ["029"] = new JsonObject { ["BuildBroadCandidates"] = 7, ["BuildSupplementCandidates"] = 2 }
            },
            ["decision"] = new JsonObject
            {
                ["BuildBroadCandidates"] = "PRODUCER_REMEDIATION_NOT_JUSTIFIED",
                ["BuildSupplementCandidates"] = "PRODUCER_REMEDIATION_NOT_JUSTIFIED",
                ["nextStep"] = "freeze recurrence evidence; do not implement a producer relaxation without a selective invariant and occurrence-safe negative controls"
            }
        };

        var directory = Path.Combine(root, "eval", "accuracy-round1f");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "procurement-producer-counterfactual.v1.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
