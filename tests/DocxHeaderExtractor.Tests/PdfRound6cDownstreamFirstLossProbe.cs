using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Model-free replay of the frozen K=160 role/span decisions through downstream stages.</summary>
public sealed class PdfRound6cDownstreamFirstLossProbe
{
    private static readonly (string Id, string Relative, string GoldFile)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx", "eval/benchmark-n3/silver-labels/004-n3.2-silver-model-assisted.v1.json"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx", "eval/benchmark-n3/silver-labels/030-n3.2-silver-model-assisted.v1.json"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx", "eval/benchmark-n3/silver-labels/043-n3.2-silver-model-assisted.v1.json"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx", "eval/benchmark-n3/silver-labels/058-n3.2-silver-model-assisted.v1.json")
    ];

    [Fact]
    public void WriteDownstreamFirstLossLedger()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "accuracy-round6");
        var run = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "k160-semantic-run.v1.json")))!;
        var checkpoints = File.ReadLines(Path.Combine(directory, "k160-role-span.jsonl"))
            .Select(line => JsonNode.Parse(line)!).ToArray();
        var results = Documents.Select(document => ReplayDocument(root, run, checkpoints, document)).ToArray();
        var trueTotals = results.Aggregate(new Totals(), (total, result) => total + result.Totals);
        var report = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["artifactKind"] = "accuracy_round6c_semantic_first_loss_ledger",
            ["phase"] = "round6c",
            ["modelCalls"] = false,
            ["semanticRerun"] = false,
            ["productionChanges"] = false,
            ["sourceAuthority"] = "Round 6B K=160 selection/role/span checkpoint + canonical source facts + frozen silver heading occurrences",
            ["identity"] = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostic only",
            ["trueHeading"] = new JsonObject
            {
                ["selected"] = trueTotals.Selected,
                ["roleSurvival"] = trueTotals.Role,
                ["spanResolution"] = trueTotals.Span,
                ["exactSpan"] = null,
                ["validated"] = trueTotals.Validated,
                ["grounded"] = null,
                ["emittedExact"] = null,
                ["firstLossCounts"] = trueTotals.Losses,
                ["semanticPositiveClassRecall"] = null,
                ["spanResolutionConditionalAccuracy"] = Ratio(trueTotals.Span, trueTotals.Role),
                ["exactSpanConditionalAccuracy"] = null,
                ["validatorSurvival"] = null,
                ["groundingSurvival"] = null,
                ["outputPrecisionProxy"] = null,
                ["supportedOutputCount"] = null,
                ["unmatchedOutputCount"] = null
            },
            ["nonHeading"] = new JsonObject
            {
                ["selected"] = null,
                ["roleHeadingTopicFalsePositive"] = null,
                ["spanResolvedFalsePositive"] = null,
                ["validatedFalsePositive"] = null,
                ["emittedUnmatched"] = null,
                ["notMeasured"] = "The frozen silver artifact contains heading occurrences only; the blind source packet has no reviewed labels. Non-heading identity/labels were not retained."
            },
            ["perDocument"] = new JsonArray(results.Select(result => result.Json).ToArray()),
            ["missingRetainedFields"] = new JsonArray("semantic_cluster_decisions", "validatorStatus", "validatorReason", "groundingStatus", "groundedSourceLineIds", "outputStatus", "emittedSourceLineIds", "reviewed_non_heading_labels"),
            ["decision"] = new JsonObject
            {
                ["status"] = "DOWNSTREAM_IDENTITY_EVIDENCE_INSUFFICIENT",
                ["reason"] = "Validator results are replay-derived from reconstructed source facts, not retained per-candidate run outcomes. Semantic cluster decisions, grounding/output statuses and reviewed non-heading labels were not retained, so false-positive, grounding and emitted-identity claims cannot be completed without recreating semantic decisions.",
                ["remediationOpened"] = false
            }
        };
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "semantic-first-loss-ledger.v2.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ReplayResult ReplayDocument(string root, JsonNode run, JsonNode[] checkpoints, (string Id, string Relative, string GoldFile) document)
    {
        var row = run["rows"]!.AsArray().Single(item => item!["file"]!.GetValue<string>().StartsWith(document.Id + "_", StringComparison.Ordinal));
        var file = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.Relative);
        var fileName = row["file"]!.GetValue<string>();
        var pdfName = Path.ChangeExtension(fileName, ".pdf");
        var selectionRecord = checkpoints.Single(item => item!["lane"]?.GetValue<string>() == "selection" && item["identity"]!.GetValue<string>() == pdfName + ":selected");
        var selected = selectionRecord["payload"]!["selected"]!.AsArray().OfType<JsonObject>().ToArray();
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(file);
        var selectedIds = selected.Select(item => item["CandidateIdDiagnostic"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var blocks = snapshot.CandidateBlocks.Where(block => selectedIds.Contains(block.Id)).ToArray();
        var contexts = PdfCandidateContextBuilder.Build(blocks, snapshot.Annotations);
        var roles = checkpoints.Where(item => item!["lane"]?.GetValue<string>() == "semantic" && item["identity"]!.GetValue<string>().StartsWith(pdfName + ":", StringComparison.Ordinal)).SelectMany(item => item!["payload"]!["blocks"]!.AsArray().OfType<JsonObject>()).GroupBy(item => item["id"]!.GetValue<string>(), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var spans = checkpoints.Where(item => item!["lane"]?.GetValue<string>() == "span" && item["identity"]!.GetValue<string>().StartsWith(pdfName + ":", StringComparison.Ordinal)).SelectMany(item => item!["payload"]!["blocks"]!.AsArray().OfType<JsonObject>()).GroupBy(item => item["id"]!.GetValue<string>(), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var decisions = blocks.Select(block =>
        {
            roles.TryGetValue(block.Id, out var role);
            var parsedRole = Enum.TryParse<PdfBlockRole>(role?["role"]?.GetValue<string>(), true, out var value) ? value : PdfBlockRole.Uncertain;
            TextOffsetSpan? span = null;
            if (spans.TryGetValue(block.Id, out var spanNode) && spanNode["resolved"]?.GetValue<bool>() == true && spanNode["start"] is not null && spanNode["end"] is not null)
                span = new TextOffsetSpan(spanNode["start"]!.GetValue<int>(), spanNode["end"]!.GetValue<int>());
            return new PdfBlockDecision(block.Id, parsedRole, role?["confidence"]?.GetValue<double>() ?? 0, role?["reason"]?.GetValue<string>() ?? "checkpoint", span);
        }).ToArray();
        var traces = PdfProposalValidator.Trace(contexts, decisions);
        var validated = PdfProposalValidator.Validate(contexts, decisions);
        var excluded = snapshot.Annotations.Where(annotation => annotation.ExcludeFromSemanticSamples).Select(annotation => annotation.Line).ToHashSet();
        var profile = PdfStyleClusterProfile.Learn(snapshot.Annotations.Where(annotation => !annotation.ExcludeFromSemanticSamples).Select(annotation => annotation.Line).ToArray());
        var samples = PdfSemanticClusterAnalyst.BuildSamples(profile, snapshot.Lines, excluded);
        var grounded = PdfBlockGrounder.Ground(blocks, decisions.Where(decision => validated.Any(item => item.SourceId == decision.Id)).ToArray(), profile, samples, [], requireLearnedCandidateStyle: false);
        var groundedIds = grounded.Headings.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var headingSpans = decisions.Where(item => item.HeadingSpan is not null).ToDictionary(item => item.Id, item => item.HeadingSpan, StringComparer.Ordinal);
        var slim = new DocxSlimExtractor().Extract(file);
        var alignment = PdfLayoutEvidenceOutline.BuildBroadAlignmentForCandidateIds(file, slim, groundedIds, headingSpans);
        var emittedIds = alignment.Headings.Select(item => item.SourceId).Where(item => item is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var gold = JsonNode.Parse(File.ReadAllText(Path.Combine(root, document.GoldFile.Replace('/', Path.DirectorySeparatorChar))))!;
        var goldRows = gold["headingOccurrences"]!.AsArray().Where(item => item!["label"]?.GetValue<string>() == "REVIEWED_HEADING").ToArray();
        var itemRows = new JsonArray(); var totals = new Totals();
        foreach (var goldRow in goldRows)
        {
            var required = goldRow!["sourceLineIds"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
            var matches = blocks.Where(block => required.All(line => block.Lines.Select(PdfCandidateProvenance.LineId).Contains(line, StringComparer.Ordinal))).ToArray();
            if (matches.Length == 0) continue;
            var role = matches.Any(block => decisions.Any(decision => decision.Id == block.Id && decision.Role == PdfBlockRole.HeadingTopic));
            var span = matches.Any(block => decisions.Any(decision => decision.Id == block.Id && decision.Role == PdfBlockRole.HeadingTopic && decision.HeadingSpan is not null));
            var degenerate = matches.Any(block => decisions.Any(decision => decision.Id == block.Id && decision.HeadingSpan is { } value && value.End <= value.Start));
            var valid = matches.Any(block => validated.Any(item => item.SourceId == block.Id));
            // The frozen run did not retain semantic-cluster decisions or per-candidate downstream
            // statuses. Do not turn an inexact replay of those stages into a false zero.
            var firstLoss = !role ? "ROLE_FALSE_NEGATIVE" : degenerate ? "SPAN_DEGENERATE" : !span ? "SPAN_UNRESOLVED" : !valid ? "VALIDATOR_REJECTION" : "UNRESOLVED";
            totals = totals.Add(role, span, valid, false, false, firstLoss);
            var firstDecision = decisions.FirstOrDefault(decision => matches.Any(block => block.Id == decision.Id));
            var firstTrace = firstDecision is null ? null : traces.FirstOrDefault(trace => trace.Id == firstDecision.Id);
            var firstSpan = firstDecision?.HeadingSpan;
            itemRows.Add(new JsonObject { ["goldStableId"] = goldRow["silverStableId"]?.DeepClone() ?? goldRow["goldStableId"]?.DeepClone(), ["candidateIdsDiagnostic"] = new JsonArray(matches.Select(item => JsonValue.Create(item.Id)).ToArray()), ["sourceLineIds"] = new JsonArray(required.Select(line => JsonValue.Create(line)).ToArray()), ["sourceText"] = goldRow["sourceText"]?.GetValue<string>() ?? string.Join(" ", required.Select(line => line[(line.LastIndexOf('|') + 1)..])), ["sourceSpanAuthority"] = goldRow["sourceSpan"]?.DeepClone(), ["sourceSpan"] = firstSpan is null ? null : new JsonObject { ["start"] = firstSpan.Start, ["end"] = firstSpan.End }, ["roleSurvived"] = role, ["spanResolved"] = span, ["spanResolution"] = span ? "resolved" : "unresolved", ["validated"] = valid, ["validatorStatus"] = valid ? "eligible_replay" : role ? "rejected_replay" : "not_heading", ["validatorReason"] = firstTrace?.Reason, ["grounded"] = null, ["groundingStatus"] = null, ["emitted"] = null, ["outputStatus"] = null, ["firstLoss"] = firstLoss });
        }
        var json = new JsonObject { ["documentId"] = document.Id, ["sourceDocumentSha256"] = row["fingerprints"]?["sourceDocumentSha256"]?.DeepClone(), ["selectedCandidates"] = selected.Length, ["selectedReviewedHeadings"] = totals.Selected, ["roleSurvival"] = totals.Role, ["spanSurvival"] = totals.Span, ["validated"] = totals.Validated, ["grounded"] = null, ["emittedExact"] = null, ["firstLossCounts"] = totals.Losses, ["validatorTraceAvailable"] = true, ["groundingTraceAvailable"] = false, ["emittedIdentityReplayed"] = false, ["missingDownstreamAuthority"] = new JsonArray("semantic_cluster_decisions", "per_candidate_grounding_status", "per_candidate_output_status"), ["items"] = itemRows, ["nonHeadingFalsePositiveLedger"] = "not_available" };
        return new ReplayResult(json, totals, selected.Length);
    }

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : (double)numerator / denominator;

    private sealed record ReplayResult(JsonObject Json, Totals Totals, int SelectedCandidateCount);
    private sealed record Totals(int Selected = 0, int Role = 0, int Span = 0, int Validated = 0, int Grounded = 0, int Emitted = 0, JsonObject? Losses = null, string LastLoss = "")
    {
        public Totals Add(bool role, bool span, bool validated, bool grounded, bool emitted, string loss)
        {
            var losses = Losses is null ? new JsonObject() : (JsonObject)Losses.DeepClone();
            losses[loss] = (losses[loss]?.GetValue<int>() ?? 0) + 1;
            return this with { Selected = Selected + 1, Role = Role + (role ? 1 : 0), Span = Span + (span ? 1 : 0), Validated = Validated + (validated ? 1 : 0), Grounded = Grounded + (grounded ? 1 : 0), Emitted = Emitted + (emitted ? 1 : 0), Losses = losses, LastLoss = loss };
        }

        public static Totals operator +(Totals left, Totals right)
        {
            var losses = left.Losses is null ? new JsonObject() : (JsonObject)left.Losses.DeepClone();
            foreach (var property in right.Losses ?? new JsonObject()) losses[property.Key] = (losses[property.Key]?.GetValue<int>() ?? 0) + property.Value!.GetValue<int>();
            return new Totals(left.Selected + right.Selected, left.Role + right.Role, left.Span + right.Span, left.Validated + right.Validated, left.Grounded + right.Grounded, left.Emitted + right.Emitted, losses);
        }
    }
}
