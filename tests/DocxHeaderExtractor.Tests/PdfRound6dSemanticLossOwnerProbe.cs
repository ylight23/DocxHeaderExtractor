using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>Model-free owner partition for the seven retained positive losses from Round 6C.</summary>
public sealed class PdfRound6dSemanticLossOwnerProbe
{
    [Fact]
    public void WriteSemanticLossOwnerPartition()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "accuracy-round6");
        var inputPath = Path.Combine(directory, "semantic-first-loss-ledger.v2.json");
        var outputPath = Path.Combine(directory, "semantic-loss-owner-partition.v1.json");
        var input = JsonNode.Parse(File.ReadAllText(inputPath))!;
        var items = input["perDocument"]!.AsArray()
            .SelectMany(document => document!["items"]!.AsArray().Select(item =>
            {
                var copy = (JsonObject)item!.DeepClone();
                copy["documentId"] = document["documentId"]?.DeepClone();
                return copy;
            }))
            .Where(item => item["firstLoss"]?.GetValue<string>() is "SPAN_UNRESOLVED" or "SPAN_DEGENERATE" or "VALIDATOR_REJECTION")
            .ToArray();

        var spanUnresolved = items.Where(item => item["firstLoss"]?.GetValue<string>() == "SPAN_UNRESOLVED").ToArray();
        var validatorLosses = items.Where(item => item["firstLoss"]?.GetValue<string>() is "SPAN_DEGENERATE" or "VALIDATOR_REJECTION").ToArray();
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy_round6d_semantic_loss_owner_partition",
            ["phase"] = "round6d-a",
            ["modelCalls"] = false,
            ["semanticRerun"] = false,
            ["productionChanges"] = false,
            ["inputArtifact"] = "semantic-first-loss-ledger.v2.json",
            ["inputPositiveLosses"] = items.Length,
            ["partition"] = new JsonObject
            {
                ["SPAN_UNRESOLVED"] = spanUnresolved.Length,
                ["VALIDATOR_REJECTION_OR_DEGENERATE"] = validatorLosses.Length
            },
            ["spanUnresolved"] = new JsonObject
            {
                ["count"] = spanUnresolved.Length,
                ["status"] = "OWNER_UNRESOLVED",
                ["firstLossEvidence"] = "All four retained items have missing-pointer-span in replay, but the frozen checkpoint does not distinguish timeout, empty/no-proposal, reconciliation, boundary, or incomplete checkpoint. No causal owner is assigned.",
                ["items"] = new JsonArray(spanUnresolved.Select(item => item.DeepClone()).ToArray())
            },
            ["validatorLosses"] = new JsonObject
            {
                ["count"] = validatorLosses.Length,
                ["status"] = "OWNER_MIXED",
                ["owner"] = "PdfProposalValidator replay predicates",
                ["reasonDistribution"] = new JsonObject(validatorLosses.GroupBy(item => item["validatorReason"]?.GetValue<string>() ?? "missing-reason").ToDictionary(group => group.Key, group => (JsonNode)JsonValue.Create(group.Count())!)),
                ["firstLossEvidence"] = "Three span-bearing decisions fail replay validation: two invalid-pointer-span and one scope-conflict. The predicate-level rejection is observable, but the upstream reason the pointer was produced that way is not retained.",
                ["items"] = new JsonArray(validatorLosses.Select(item => item.DeepClone()).ToArray())
            },
            ["decision"] = new JsonObject
            {
                ["status"] = "OWNER_MIXED",
                ["reason"] = "The four unresolved spans remain causally unresolved; the three validator losses share an observable validator boundary but have mixed predicate reasons. No remediation is opened.",
                ["remediationOpened"] = false
            }
        };
        File.WriteAllText(outputPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
