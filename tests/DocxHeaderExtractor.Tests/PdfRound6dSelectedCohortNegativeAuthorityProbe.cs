using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Builds a source-first selected-cohort packet before any semantic join.</summary>
public sealed class PdfRound6dSelectedCohortNegativeAuthorityProbe
{
    private static readonly (string Id, string Relative, string GoldFile)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx", "eval/benchmark-n3/silver-labels/004-n3.2-silver-model-assisted.v1.json"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx", "eval/benchmark-n3/silver-labels/030-n3.2-silver-model-assisted.v1.json"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx", "eval/benchmark-n3/silver-labels/043-n3.2-silver-model-assisted.v1.json"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx", "eval/benchmark-n3/silver-labels/058-n3.2-silver-model-assisted.v1.json")
    ];

    [Fact]
    public void WriteSelectedCohortSourceFirstPacket()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "accuracy-round6");
        var runPath = Path.Combine(directory, "k160-semantic-run.v1.json");
        var checkpointPath = Path.Combine(directory, "k160-role-span.jsonl");
        var run = JsonNode.Parse(File.ReadAllText(runPath))!;
        var checkpoints = File.ReadLines(checkpointPath).Select(lineText => JsonNode.Parse(lineText)!).ToArray();
        var documents = Documents.Select(document => BuildDocumentPacket(root, run, checkpoints, document)).ToArray();
        var allItems = documents.SelectMany(document => document["occurrences"]!.AsArray()).ToArray();
        var labels = allItems.GroupBy(item => item!["label"]!.GetValue<string>()).ToDictionary(group => group.Key, group => group.Count());
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy_round6d_selected_cohort_negative_authority",
            ["phase"] = "round6d-c",
            ["sourceAuthority"] = "documentSha256 + page + sourceLineIds + sourceSpan",
            ["selectionAuthority"] = "Round 6B committed K=160 selection snapshot",
            ["blindSourceFirst"] = true,
            ["semanticJoined"] = false,
            ["labelsFrozenBeforeSemanticJoin"] = true,
            ["modelCalls"] = false,
            ["productionChanges"] = false,
            ["selectedOccurrenceCount"] = allItems.Length,
            ["labelCounts"] = new JsonObject(labels.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))),
            ["selectionSnapshotSha256"] = Sha256(File.ReadAllText(checkpointPath)),
            ["runArtifactSha256"] = Sha256(File.ReadAllText(runPath)),
            ["labelFreezeSha256"] = Sha256(string.Join("\n", allItems.Select(item => string.Join("|", item!["documentSha256"], item["page"], item["sourceLineIds"], item["label"])))),
            ["labelPolicy"] = "Existing positive silver may establish REVIEWED_HEADING only on exact source occurrence identity. All other selected occurrences remain UNCERTAIN; no selected candidate is inferred to be REVIEWED_NON_HEADING.",
            ["prohibitedFields"] = new JsonArray("candidateId", "candidateScore", "rank", "selectedAtK", "semanticRole", "semanticReason", "spanProposal", "validatorResult", "groundingResult", "outputResult", "existingDiagnosis"),
            ["documents"] = new JsonArray(documents),
            ["joinStatus"] = "FROZEN_PRE_SEMANTIC_JOIN"
        };
        File.WriteAllText(Path.Combine(directory, "selected-cohort-negative-authority.v1.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonObject BuildDocumentPacket(string root, JsonNode run, JsonNode[] checkpoints, (string Id, string Relative, string GoldFile) document)
    {
        var row = run["rows"]!.AsArray().Single(item => item!["file"]!.GetValue<string>().StartsWith(document.Id + "_", StringComparison.Ordinal));
        var file = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.Relative);
        var pdfName = Path.ChangeExtension(row["file"]!.GetValue<string>(), ".pdf");
        var selection = checkpoints.Single(item => item!["lane"]?.GetValue<string>() == "selection" && item["identity"]!.GetValue<string>() == pdfName + ":selected");
        var selected = selection["payload"]!["selected"]!.AsArray().OfType<JsonObject>().ToArray();
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(file);
        var blocks = snapshot.CandidateBlocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var gold = JsonNode.Parse(File.ReadAllText(Path.Combine(root, document.GoldFile.Replace('/', Path.DirectorySeparatorChar))))!;
        var goldKeys = gold["headingOccurrences"]!.AsArray()
            .Where(item => item!["label"]?.GetValue<string>() == "REVIEWED_HEADING")
            .Select(item => string.Join("\u001f", item!["sourceLineIds"]!.AsArray().Select(line => line!.GetValue<string>())))
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new JsonArray();
        foreach (var selectedItem in selected)
        {
            var candidateId = selectedItem["CandidateIdDiagnostic"]!.GetValue<string>();
            if (!blocks.TryGetValue(candidateId, out var block)) continue;
            var lineIds = block.Lines.Select(PdfCandidateProvenance.LineId).ToArray();
            var sourceSpan = new JsonObject { ["start"] = 0, ["end"] = block.Text.Length };
            var identity = string.Join("\u001f", row["fingerprints"]!["sourceDocumentSha256"]!.GetValue<string>(), block.Page, string.Join("\u001e", lineIds), "0:" + block.Text.Length);
            if (!seen.Add(identity)) continue;
            var lineIndex = snapshot.Lines.Select((line, index) => (line, index)).Where(pair => lineIds.Contains(PdfCandidateProvenance.LineId(pair.line), StringComparer.Ordinal)).Select(pair => pair.index).DefaultIfEmpty(0).Min();
            var preceding = snapshot.Lines.Skip(Math.Max(0, lineIndex - 2)).Take(2).Select(line => line.Text).ToArray();
            var following = snapshot.Lines.Skip(lineIndex + block.Lines.Count).Take(2).Select(line => line.Text).ToArray();
            var key = string.Join("\u001f", lineIds);
            occurrences.Add(new JsonObject
            {
                ["reviewKey"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant(),
                ["documentSha256"] = row["fingerprints"]!["sourceDocumentSha256"]!.DeepClone(),
                ["page"] = block.Page,
                ["sourceLineIds"] = new JsonArray(lineIds.Select(line => JsonValue.Create(line)).ToArray()),
                ["sourceText"] = block.Text,
                ["sourceSpan"] = sourceSpan,
                ["precedingSourceLines"] = new JsonArray(preceding.Select(line => JsonValue.Create(line)).ToArray()),
                ["followingSourceLines"] = new JsonArray(following.Select(line => JsonValue.Create(line)).ToArray()),
                ["layoutFacts"] = new JsonObject
                {
                    ["lineCount"] = block.Lines.Count,
                    ["topY"] = block.TopY,
                    ["bottomY"] = block.BottomY,
                    ["left"] = block.Left,
                    ["right"] = block.Right,
                    ["fontSizeBucket"] = block.PrimaryStyle.FontSizeBucket,
                    ["fontName"] = block.PrimaryStyle.FontName
                },
                ["label"] = goldKeys.Contains(key) ? "REVIEWED_HEADING" : "UNCERTAIN"
            });
        }
        return new JsonObject
        {
            ["documentId"] = document.Id,
            ["documentSha256"] = row["fingerprints"]!["sourceDocumentSha256"]!.DeepClone(),
            ["selectedSourceOccurrences"] = occurrences.Count,
            ["occurrences"] = occurrences
        };
    }
}
