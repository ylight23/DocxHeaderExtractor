using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>Builds the Round 6B ledger from the single frozen K=160 run.</summary>
public sealed class PdfRound6SemanticFirstLossLedgerProbe
{
    private static readonly (string Id, string GoldFile)[] Documents =
    [
        ("004", "eval/benchmark-n3/silver-labels/004-n3.2-silver-model-assisted.v1.json"),
        ("030", "eval/benchmark-n3/silver-labels/030-n3.2-silver-model-assisted.v1.json"),
        ("043", "eval/benchmark-n3/silver-labels/043-n3.2-silver-model-assisted.v1.json"),
        ("058", "eval/benchmark-n3/silver-labels/058-n3.2-silver-model-assisted.v1.json")
    ];

    [Fact]
    public void WriteSemanticFirstLossLedger()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "accuracy-round6");
        var run = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "k160-semantic-run.v1.json")))!;
        var checkpoint = File.ReadLines(Path.Combine(directory, "k160-role-span.jsonl"))
            .Select(line => JsonNode.Parse(line)!).ToArray();
        var ledgers = Documents.Select(document => BuildDocument(root, run, checkpoint, document)).ToArray();
        var aggregate = new JsonObject
        {
            ["selectedReviewedHeadings"] = ledgers.Sum(item => item.SelectedReviewedHeadings),
            ["roleCorrect"] = ledgers.Sum(item => item.RoleCorrect),
            ["spanCorrect"] = ledgers.Sum(item => item.SpanCorrect),
            ["validated"] = null, ["grounded"] = null, ["emitted"] = null,
            ["semanticPrecisionProxy"] = null, ["semanticConditionalRecall"] = null,
            ["exactSpanAccuracy"] = null, ["supportedOutputCount"] = null,
            ["unmatchedOutputCount"] = null,
            ["notMeasured"] = new JsonArray("validated", "grounded", "emitted", "semanticPrecisionProxy", "semanticConditionalRecall", "exactSpanAccuracy", "supportedOutputCount", "unmatchedOutputCount", "validator_failure_reason", "validated_emitted_occurrence_identity")
        };
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy_round6b_semantic_first_loss_ledger",
            ["phase"] = "round6b",
            ["modelCalls"] = true,
            ["newProviderCallsAfterRun"] = 0,
            ["productionChanges"] = false,
            ["promptOrModelTuned"] = false,
            ["executionContract"] = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "k160-semantic-execution-manifest.v1.json")))!["frozenProfile"]!.DeepClone(),
            ["sourceAuthority"] = "K=160 route output + selection/semantic/span checkpoint + consumed silver source-line packets",
            ["identity"] = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is run-local diagnostics only",
            ["aggregate"] = aggregate,
            ["allowedOwners"] = new JsonArray("ROLE_FALSE_NEGATIVE", "ROLE_FALSE_POSITIVE", "SPAN_UNRESOLVED", "SPAN_DEGENERATE", "VALIDATOR_REJECTION", "GROUNDING_FAILURE", "OUTPUT_POLICY_WITHHELD", "EXECUTION_BOUND", "UNRESOLVED"),
            ["perDocument"] = new JsonArray(ledgers.Select(item => item.Json).ToArray()),
            ["executionAvailability"] = new JsonObject { ["K160"] = "complete route; selection, semantic role, and span checkpoint available", ["selectionIdentityPersistedBeforeProvider"] = true, ["selectedCandidateTotal"] = ledgers.Sum(item => item.SelectedCandidateCount), ["newProviderCallsAfterRun"] = 0 },
            ["instrumentationProof"] = new JsonObject { ["selectionAlgorithmChanged"] = false, ["rankingChanged"] = false, ["candidateGenerationChanged"] = false, ["selectionRecordedBeforeSemanticExecution"] = true, ["semanticPayloadChanged"] = false, ["validatorBehaviorChanged"] = false, ["finalOutputBehaviorChanged"] = false, ["proofBasis"] = "append-only checkpoint/audit projection after unchanged SelectRankedCandidates" },
            ["decision"] = new JsonObject { ["status"] = "OCCURRENCE_LEDGER_PARTIAL_DOWNSTREAM_IDENTITY_UNAVAILABLE", ["reason"] = "The run retained selected source identities plus role/span outcomes, but not per-candidate validator, grounding, or emitted identities. Those metrics are intentionally not inferred from aggregates.", ["nextStep"] = "Persist validator, grounding, and emitted source identities in the same frozen execution artifact before downstream quality claims", ["remediationOpened"] = false }
        };
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "semantic-first-loss-ledger.v1.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static LedgerDocument BuildDocument(string root, JsonNode run, JsonNode[] checkpoint, (string Id, string GoldFile) document)
    {
        var row = run["rows"]!.AsArray().Single(item => item!["file"]!.GetValue<string>().StartsWith(document.Id + "_", StringComparison.Ordinal));
        var fileName = row["file"]!.GetValue<string>();
        var pdfName = Path.ChangeExtension(fileName, ".pdf");
        var gold = JsonNode.Parse(File.ReadAllText(Path.Combine(root, document.GoldFile.Replace('/', Path.DirectorySeparatorChar))))!;
        var goldRows = gold["headingOccurrences"]!.AsArray().Where(item => item!["label"]?.GetValue<string>() == "REVIEWED_HEADING").ToArray();
        var selection = checkpoint.Single(item => item!["lane"]?.GetValue<string>() == "selection" && item["identity"]!.GetValue<string>() == pdfName + ":selected");
        var selected = selection["payload"]!["selected"]!.AsArray().OfType<JsonObject>().ToArray();
        var semantic = checkpoint.Where(item => item!["lane"]?.GetValue<string>() == "semantic" && item["identity"]!.GetValue<string>().StartsWith(pdfName + ":", StringComparison.Ordinal)).SelectMany(item => item!["payload"]!["blocks"]!.AsArray().OfType<JsonObject>()).ToDictionary(item => item["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var spans = checkpoint.Where(item => item!["lane"]?.GetValue<string>() == "span" && item["identity"]!.GetValue<string>().StartsWith(pdfName + ":", StringComparison.Ordinal)).SelectMany(item => item!["payload"]!["blocks"]!.AsArray().OfType<JsonObject>()).ToDictionary(item => item["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var items = new JsonArray(); var roleCorrect = 0; var spanCorrect = 0;
        foreach (var goldRow in goldRows)
        {
            var required = goldRow!["sourceLineIds"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
            var candidate = selected.FirstOrDefault(item => ContainsLines(item["SourceLineIds"], required));
            if (candidate is null) continue;
            var id = candidate["CandidateIdDiagnostic"]!.GetValue<string>();
            var role = semantic.TryGetValue(id, out var decision) ? decision["role"]?.GetValue<string>() : null;
            var headingRole = string.Equals(role, "HeadingTopic", StringComparison.OrdinalIgnoreCase); if (headingRole) roleCorrect++;
            var span = spans.TryGetValue(id, out var spanBlock) && spanBlock["resolved"]?.GetValue<bool>() == true; if (span) spanCorrect++;
            var degenerate = span && spanBlock!["start"]?.GetValue<int>() == spanBlock["end"]?.GetValue<int>();
            var loss = !headingRole ? "ROLE_FALSE_NEGATIVE" : !span ? "SPAN_UNRESOLVED" : degenerate ? "SPAN_DEGENERATE" : "UNRESOLVED";
            items.Add(new JsonObject { ["goldStableId"] = goldRow["silverStableId"]?.DeepClone() ?? goldRow["goldStableId"]?.DeepClone(), ["candidateIdDiagnostic"] = id, ["page"] = candidate["Page"]?.DeepClone(), ["sourceLineIds"] = candidate["SourceLineIds"]?.DeepClone(), ["sourceText"] = candidate["SourceText"]?.DeepClone(), ["role"] = role, ["roleCorrect"] = headingRole, ["spanResolved"] = span, ["spanDegenerate"] = degenerate, ["validated"] = null, ["grounded"] = null, ["emitted"] = null, ["firstLoss"] = loss });
        }
        var json = new JsonObject { ["documentId"] = document.Id, ["reviewedHeadings"] = goldRows.Length, ["selectedCandidateCount"] = selected.Length, ["selectedReviewedHeadings"] = items.Count, ["roleCorrect"] = roleCorrect, ["spanCorrect"] = spanCorrect, ["validated"] = null, ["grounded"] = null, ["emitted"] = null, ["semanticConditionalRecall"] = null, ["exactSpanAccuracy"] = null, ["firstLossLedgerStatus"] = "role_span_identity_available_downstream_identity_missing", ["items"] = items, ["counts"] = new JsonObject { ["roleFalseNegative"] = items.Count(item => item!["firstLoss"]!.GetValue<string>() == "ROLE_FALSE_NEGATIVE"), ["spanUnresolved"] = items.Count(item => item!["firstLoss"]!.GetValue<string>() == "SPAN_UNRESOLVED"), ["spanDegenerate"] = items.Count(item => item!["firstLoss"]!.GetValue<string>() == "SPAN_DEGENERATE"), ["unresolved"] = items.Count(item => item!["firstLoss"]!.GetValue<string>() == "UNRESOLVED") } };
        return new LedgerDocument(json, selected.Length, items.Count, roleCorrect, spanCorrect);
    }

    private static bool ContainsLines(JsonNode? node, IReadOnlyList<string> required)
    {
        if (node is null) return false;
        var actual = node.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        return required.All(actual.Contains);
    }

    private sealed record LedgerDocument(JsonObject Json, int SelectedCandidateCount, int SelectedReviewedHeadings, int RoleCorrect, int SpanCorrect);
}
