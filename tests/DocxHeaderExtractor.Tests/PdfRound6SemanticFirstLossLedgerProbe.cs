using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Builds the Round 6 first-loss ledger from frozen Round 4 artifacts. It is deliberately
/// source-line based and does not rerun semantic inference or invent final output labels.
/// </summary>
public sealed class PdfRound6SemanticFirstLossLedgerProbe
{
    private static readonly (string Id, string Relative, string GoldFile)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx", "eval/benchmark-n3/silver-labels/004-n3.2-silver-model-assisted.v1.json"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx", "eval/benchmark-n3/silver-labels/030-n3.2-silver-model-assisted.v1.json"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx", "eval/benchmark-n3/silver-labels/043-n3.2-silver-model-assisted.v1.json"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx", "eval/benchmark-n3/silver-labels/058-n3.2-silver-model-assisted.v1.json")
    ];

    [Fact]
    public void WriteSemanticFirstLossLedger()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "accuracy-round6");
        var run = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eval", "accuracy-round4", "k640-semantic-run.v1.json")))!;
        var checkpoints = File.ReadAllLines(Path.Combine(root, "eval", "accuracy-round4", "k640-role-span.jsonl"))
            .Select(line => JsonNode.Parse(line)!).ToArray();
        var ledgers = new JsonArray();
        foreach (var document in Documents)
            ledgers.Add(BuildDocument(root, run, checkpoints, document));

        var aggregate = new JsonObject
        {
            ["selectedReviewedHeadings"] = 184,
            ["roleCorrect"] = null,
            ["spanCorrect"] = null,
            ["validated"] = null,
            ["grounded"] = null,
            ["emitted"] = null,
            ["semanticPrecisionProxy"] = null,
            ["semanticConditionalRecall"] = null,
            ["exactSpanAccuracy"] = null,
            ["notMeasured"] = new JsonArray("roleCorrect", "spanCorrect", "validated", "grounded", "emitted", "semanticPrecisionProxy", "semanticConditionalRecall", "exactSpanAccuracy", "per_occurrence_selected_identity")
        };
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy_round6_semantic_first_loss_ledger",
            ["phase"] = "round6a",
            ["modelCalls"] = false,
            ["productionChanges"] = false,
            ["promptOrModelTuned"] = false,
            ["sourceAuthority"] = "Round 4 K=640 route/checkpoint + consumed silver source-line packets",
            ["identity"] = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is run-local diagnostics only",
            ["selectedCohort"] = "deterministic K=640 selected cohort; 184 reviewed headings according to Round 3 selection artifact",
            ["allowedOwners"] = new JsonArray("ROLE_FALSE_NEGATIVE", "ROLE_FALSE_POSITIVE", "SPAN_UNRESOLVED", "SPAN_DEGENERATE", "VALIDATOR_REJECTION", "GROUNDING_FAILURE", "OUTPUT_POLICY_WITHHELD", "UNRESOLVED"),
            ["aggregate"] = aggregate,
            ["perDocument"] = ledgers,
            ["executionAvailability"] = new JsonObject
            {
                ["K640"] = "checkpoint and route available; span lane partial_timeout on all four documents",
                ["K1024"] = "route/checkpoint available; zero downstream facts after frozen lane deadline; excluded from semantic quality denominator",
                ["newProviderCalls"] = 0
            },
            ["decision"] = new JsonObject
            {
                ["status"] = "EVIDENCE_INSUFFICIENT_FOR_OCCURRENCE_LEDGER",
                ["reason"] = "Round 4 persists the aggregate K=640 selected count and route/checkpoint data, but not the selected source occurrence identities. Re-running extraction to reconstruct them would be a new census and is not used as historical authority.",
                ["nextStep"] = "freeze an occurrence-retaining semantic execution contract before any new provider call; persist sourceLineIds/sourceSpan and per-candidate role, span, validator, grounding, and output outcomes"
            }
        };
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "semantic-first-loss-ledger.v1.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject BuildDocument(string root, JsonNode run, JsonNode[] checkpoints, (string Id, string Relative, string GoldFile) document)
    {
        var row = run["rows"]!.AsArray().Single(r => r!["file"]!.GetValue<string>().StartsWith(document.Id + "_", StringComparison.Ordinal));
        var gold = JsonNode.Parse(File.ReadAllText(Path.Combine(root, document.GoldFile.Replace('/', Path.DirectorySeparatorChar))))!;
        var goldRows = gold["headingOccurrences"]!.AsArray();
        var routeItems = row["items"]?.AsArray() ?? new JsonArray();
        var round3 = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eval", "accuracy-round3", "selection-architecture-feasibility.v1.json")))!;
        var curve = round3["perDocument"]!.AsArray()
            .Single(d => d!["documentId"]!.GetValue<string>() == document.Id)["curve"]!.AsArray()
            .Single(c => c!["K"]!.GetValue<string>() == "640");
        var pdfName = row["file"]!.GetValue<string>().Replace(".docx", ".pdf", StringComparison.OrdinalIgnoreCase);
        var semanticBlocks = checkpoints
            .Where(c => c!["lane"]?.GetValue<string>() == "semantic" && c["identity"]!.GetValue<string>().StartsWith(pdfName, StringComparison.Ordinal))
            .SelectMany(c => c!["payload"]!["blocks"]!.AsArray()).OfType<JsonObject>().ToArray();
        // Round 4 persisted aggregate selection counts but not the selected occurrence IDs. Do not
        // reconstruct a gold-dependent per-item ledger from an aggregate; mark this artifact as an
        // availability result until an occurrence-retaining run exists.
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var items = new JsonArray();
        foreach (var goldRow in goldRows)
        {
            var lines = goldRow!["sourceLineIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
            var semantic = semanticBlocks.Where(b => ContainsLines(b["lineIds"], lines)).ToArray();
            var role = semantic.Any(b => b["role"]?.GetValue<string>()?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true);
            var route = routeItems.FirstOrDefault(i => ContainsLines(i!["lineIds"], lines));
            var span = route is not null;
            var degenerate = route?["headingSpan"] is JsonObject spanNode &&
                             spanNode["start"]?.GetValue<int>() == spanNode["end"]?.GetValue<int>();
            var grounded = route is not null && row["canonicalGroundings"]?.AsArray().Any(g =>
                string.Equals(g!["sourceFactId"]?.GetValue<string>(), route["sourceFactId"]?.GetValue<string>(), StringComparison.Ordinal)) == true;
            var selectedHere = selected.Contains(goldRow["silverStableId"]?.GetValue<string>() ?? goldRow["goldStableId"]?.GetValue<string>() ?? "");
            if (!selectedHere) continue;
            var firstLoss = !role ? "ROLE_FALSE_NEGATIVE" : !span ? "SPAN_UNRESOLVED" : degenerate ? "SPAN_DEGENERATE" : !grounded ? "GROUNDING_FAILURE" : "VALIDATED";
            items.Add(new JsonObject
            {
                ["goldStableId"] = goldRow["silverStableId"]?.GetValue<string>() ?? goldRow["goldStableId"]?.GetValue<string>(),
                ["page"] = goldRow["page"]!.DeepClone(),
                ["sourceLineIds"] = goldRow["sourceLineIds"]!.DeepClone(),
                ["roleObserved"] = role,
                ["spanResolved"] = span,
                ["spanDegenerate"] = degenerate,
                ["validated"] = route is not null,
                ["grounded"] = grounded,
                ["firstLoss"] = firstLoss
            });
        }
        return new JsonObject
        {
            ["documentId"] = document.Id,
            ["reviewedHeadings"] = goldRows.Count,
            ["selectedReviewedHeadings"] = curve["SelectedReviewedHeadings"]?.DeepClone(),
            ["firstLossLedgerStatus"] = "selected_identity_not_persisted_in_round4",
            ["items"] = items,
            ["counts"] = new JsonObject
            {
                ["roleFalseNegative"] = items.Count(i => i!["firstLoss"]!.GetValue<string>() == "ROLE_FALSE_NEGATIVE"),
                ["spanUnresolved"] = items.Count(i => i!["firstLoss"]!.GetValue<string>() == "SPAN_UNRESOLVED"),
                ["spanDegenerate"] = items.Count(i => i!["firstLoss"]!.GetValue<string>() == "SPAN_DEGENERATE"),
                ["groundingFailure"] = items.Count(i => i!["firstLoss"]!.GetValue<string>() == "GROUNDING_FAILURE"),
                ["validated"] = items.Count(i => i!["validated"]?.GetValue<bool>() == true),
                ["grounded"] = items.Count(i => i!["grounded"]?.GetValue<bool>() == true)
            }
        };
    }

    private static bool ContainsLines(JsonNode? node, IReadOnlyList<string> required)
    {
        if (node is null) return false;
        var actual = node.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        return required.All(actual.Contains);
    }
}
