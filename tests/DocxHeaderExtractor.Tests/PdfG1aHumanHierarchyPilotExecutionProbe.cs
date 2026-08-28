using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// G1A publishes a source-context-only pilot form. It intentionally does not populate human labels.
/// </summary>
public sealed class PdfG1aHumanHierarchyPilotExecutionProbe
{
    [Fact]
    public void WriteBlindTwentyFourRowPilotForm()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var g1Path = Path.Combine(root, "eval", "accuracy", "hierarchy-human-authority.v1.json");
        Assert.True(File.Exists(g1Path), "G1 artifact is required before G1A.");
        using var g1 = JsonDocument.Parse(File.ReadAllText(g1Path));
        var rows = new JsonArray();

        foreach (var document in g1.RootElement.GetProperty("documents").EnumerateArray())
        {
            var documentId = document.GetProperty("documentId").GetString()!;
            var sourcePath = Path.Combine(root, "eval", "benchmark-n3", "source-packets",
                $"{documentId}-blind-source-review.v1.json");
            using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
            var sourceByLine = source.RootElement.GetProperty("items").EnumerateArray()
                .ToDictionary(item => item.GetProperty("lineId").GetString()!, StringComparer.Ordinal);
            var pilotIds = g1.RootElement.GetProperty("pilot").GetProperty("rows").EnumerateArray()
                .Where(item => item.GetProperty("documentId").GetString() == documentId)
                .Select(item => item.GetProperty("occurrenceId").GetString()!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var occurrence in document.GetProperty("occurrences").EnumerateArray()
                .Where(item => pilotIds.Contains(item.GetProperty("occurrenceId").GetString()!)))
            {
                var lineIds = occurrence.GetProperty("sourceLineIds").EnumerateArray()
                    .Select(line => line.GetString()!).ToArray();
                var context = new JsonArray();
                foreach (var lineId in lineIds)
                {
                    if (!sourceByLine.TryGetValue(lineId, out var sourceItem)) continue;
                    context.Add(new JsonObject
                    {
                        ["page"] = sourceItem.GetProperty("page").GetInt32(),
                        ["lineId"] = lineId,
                        ["text"] = sourceItem.GetProperty("text").GetString(),
                        ["previousLines"] = JsonNode.Parse(sourceItem.GetProperty("previousLines").GetRawText()),
                        ["nextLines"] = JsonNode.Parse(sourceItem.GetProperty("nextLines").GetRawText())
                    });
                }

                rows.Add(new JsonObject
                {
                    ["documentId"] = documentId,
                    ["documentSha256"] = document.GetProperty("documentSha256").GetString(),
                    ["occurrenceId"] = occurrence.GetProperty("occurrenceId").GetString(),
                    ["sourceLineIds"] = JsonNode.Parse(occurrence.GetProperty("sourceLineIds").GetRawText()),
                    ["sourceText"] = occurrence.GetProperty("sourceText").GetString(),
                    ["page"] = occurrence.GetProperty("page").GetInt32(),
                    ["sourceContext"] = context,
                    ["predictionVisible"] = false,
                    ["levelStatus"] = "PENDING_HUMAN_REVIEW",
                    ["level"] = null,
                    ["parentStatus"] = "PENDING_HUMAN_REVIEW",
                    ["parentOccurrenceId"] = null,
                    ["scopeStatus"] = "PENDING_HUMAN_REVIEW",
                    ["scope"] = null,
                    ["typePathStatus"] = "PENDING_HUMAN_REVIEW",
                    ["typePath"] = null,
                    ["annotatorId"] = null,
                    ["annotatedAt"] = null,
                    ["annotationVersion"] = null,
                    ["evidenceSourceLineIds"] = new JsonArray(),
                    ["note"] = null
                });
            }
        }

        Assert.Equal(24, rows.Count);
        var output = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "hierarchy_human_pilot_annotation",
            ["annotationVersion"] = "g1a-human-hierarchy-pilot.v1",
            ["status"] = "READY_FOR_BLIND_HUMAN_ANNOTATION",
            ["PILOT_SEEDS"] = 24,
            ["PILOT_REVIEWED"] = 0,
            ["LEVEL_REVIEWED"] = 0,
            ["PARENT_REVIEWED"] = 0,
            ["SCOPE_REVIEWED"] = 0,
            ["TYPE_PATH_REVIEWED"] = 0,
            ["PREDICTION_VISIBLE_DURING_ANNOTATION"] = false,
            ["SECOND_ANNOTATOR_AVAILABLE"] = false,
            ["INTER_ANNOTATOR_AGREEMENT"] = "NOT_MEASURED",
            ["TEXT_BASED_JOIN"] = false,
            ["DUPLICATE_OCCURRENCE_COLLAPSE"] = false,
            ["identityRule"] = "documentSha256 + sourceLineIds + occurrenceId",
            ["allowedStatuses"] = new JsonArray("OBSERVABLE", "NOT_OBSERVABLE", "AMBIGUOUS"),
            ["rows"] = rows,
            ["PROVIDER_CALLS"] = 0,
            ["PRODUCTION_CODE_CHANGED"] = false
        };
        File.WriteAllText(Path.Combine(root, "eval", "accuracy", "hierarchy-human-pilot-annotation.v1.json"),
            output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, "docs", "accuracy", "hierarchy-human-pilot-annotation-execution.md"),
            "# G1A Human Hierarchy Pilot Annotation\n\n" +
            "Status: `READY_FOR_BLIND_HUMAN_ANNOTATION`.\n\n" +
            "This execution packet contains 24 occurrence-level rows across documents 004, 030, 043, and 058. " +
            "Each row includes source context and neighboring lines, but no prediction, confidence, validator, or output fields.\n\n" +
            "A human annotator must fill level, parent, scope, and typePath independently with `OBSERVABLE`, `NOT_OBSERVABLE`, or `AMBIGUOUS`, then add annotatorId, annotatedAt, and annotationVersion. " +
            "Until those fields are persisted, `PILOT_REVIEWED=0` and hierarchy authority is not created.\n\n" +
            "`PROVIDER_CALLS=0` and `PRODUCTION_CODE_CHANGED=false`.\n\n" +
            "Output artifact: `eval/accuracy/hierarchy-human-pilot-annotation.v1.json`.\n");
    }

    [Fact]
    public void PilotFormIsPredictionBlindAndUnannotated()
    {
        var path = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "eval", "accuracy", "hierarchy-human-pilot-annotation.v1.json");
        Assert.True(File.Exists(path), "Run the G1A packet writer before checking the form.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(24, root.GetProperty("PILOT_SEEDS").GetInt32());
        Assert.Equal(0, root.GetProperty("PILOT_REVIEWED").GetInt32());
        Assert.False(root.GetProperty("PREDICTION_VISIBLE_DURING_ANNOTATION").GetBoolean());
        Assert.Equal("NOT_MEASURED", root.GetProperty("INTER_ANNOTATOR_AGREEMENT").GetString());
        Assert.Equal(24, root.GetProperty("rows").GetArrayLength());
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            Assert.False(row.GetProperty("predictionVisible").GetBoolean());
            Assert.Equal("PENDING_HUMAN_REVIEW", row.GetProperty("levelStatus").GetString());
            Assert.Equal("PENDING_HUMAN_REVIEW", row.GetProperty("parentStatus").GetString());
            Assert.Equal("PENDING_HUMAN_REVIEW", row.GetProperty("scopeStatus").GetString());
            Assert.Equal("PENDING_HUMAN_REVIEW", row.GetProperty("typePathStatus").GetString());
        }
    }
}
