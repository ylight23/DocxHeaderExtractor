using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// G1 freezes the blind annotation contract and creates a human-fillable authority artifact. It
/// deliberately refuses to manufacture hierarchy labels from silver, markers, or predictions.
/// </summary>
public sealed class PdfG1HumanHierarchyAnnotationProbe
{
    [Fact]
    public void WriteBlindAnnotationArtifactAndGuideline()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var inputPath = Path.Combine(root, "eval", "accuracy", "hierarchy-human-authority-packet.v1.json");
        Assert.True(File.Exists(inputPath), "G0 packet is required before G1.");

        using var input = JsonDocument.Parse(File.ReadAllText(inputPath));
        var documents = new JsonArray();
        var pilot = new JsonArray();
        var total = 0;

        foreach (var document in input.RootElement.GetProperty("documents").EnumerateArray())
        {
            var documentId = document.GetProperty("documentId").GetString()!;
            var occurrences = new JsonArray();
            var sourceOccurrences = document.GetProperty("occurrences").EnumerateArray().ToArray();
            var pilotIndexes = PilotIndexes(sourceOccurrences);
            foreach (var (source, index) in sourceOccurrences.Select((value, index) => (value, index)))
            {
                var occurrenceId = source.GetProperty("occurrenceId").GetString()!;
                var row = new JsonObject
                {
                    ["occurrenceId"] = occurrenceId,
                    ["sourceLineIds"] = JsonNode.Parse(source.GetProperty("sourceLineIds").GetRawText()),
                    ["page"] = source.GetProperty("page").GetInt32(),
                    ["sourceText"] = source.GetProperty("sourceText").GetString(),
                    ["annotationStatus"] = "PENDING_HUMAN_ANNOTATION",
                    ["annotatorId"] = null,
                    ["annotatedAt"] = null,
                    ["annotationVersion"] = null,
                    ["evidenceSourceLineIds"] = new JsonArray(),
                    ["note"] = null,
                    ["level"] = Field("humanLevel"),
                    ["parent"] = Field("parentOccurrenceId"),
                    ["scope"] = Field("scopeStartOccurrenceId", "scopeEndOccurrenceId"),
                    ["typePath"] = Field("typePath")
                };
                occurrences.Add(row);
                if (pilotIndexes.Contains(index))
                {
                    pilot.Add(new JsonObject
                    {
                        ["documentId"] = documentId,
                        ["occurrenceId"] = occurrenceId,
                        ["pilotRole"] = PilotRole(source.GetProperty("sourceText").GetString()!),
                        ["predictionVisible"] = false
                    });
                }
            }

            total += occurrences.Count;
            documents.Add(new JsonObject
            {
                ["documentId"] = documentId,
                ["documentSha256"] = document.GetProperty("documentSha256").GetString(),
                ["occurrences"] = occurrences
            });
        }

        Assert.Equal(422, total);
        Assert.InRange(pilot.Count, 20, 30);
        var output = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "hierarchy_human_authority",
            ["annotationVersion"] = "g1-human-hierarchy-annotation.v1",
            ["status"] = "PENDING_HUMAN_ANNOTATION",
            ["HUMAN_AUTHORITY_CREATED"] = false,
            ["HUMAN_AUTHORITY_BLIND_TO_PREDICTION"] = true,
            ["PREDICTION_VISIBLE_DURING_ANNOTATION"] = false,
            ["TEXT_BASED_JOIN"] = false,
            ["DUPLICATE_OCCURRENCE_COLLAPSE"] = false,
            ["identityRule"] = "documentSha256 + sourceLineIds + occurrenceId",
            ["dimensions"] = new JsonArray("level", "parent", "scope", "typePath"),
            ["allowedStatuses"] = new JsonArray("OBSERVABLE", "NOT_OBSERVABLE", "AMBIGUOUS"),
            ["documents"] = documents,
            ["pilot"] = new JsonObject
            {
                ["targetCount"] = 24,
                ["actualCount"] = pilot.Count,
                ["rows"] = pilot,
                ["status"] = "READY_FOR_BLIND_HUMAN_LABELING"
            },
            ["summary"] = new JsonObject
            {
                ["TOTAL_SEEDS"] = total,
                ["LEVEL_AUTHORITY_COUNT"] = 0,
                ["PARENT_AUTHORITY_COUNT"] = 0,
                ["SCOPE_AUTHORITY_COUNT"] = 0,
                ["TYPE_PATH_AUTHORITY_COUNT"] = 0,
                ["UNRESOLVED_IDENTITY"] = 0,
                ["LEVEL_DENOMINATOR"] = 0,
                ["PARENT_DENOMINATOR"] = 0,
                ["SCOPE_DENOMINATOR"] = 0,
                ["TYPE_PATH_DENOMINATOR"] = 0
            },
            ["PROVIDER_CALLS"] = 0,
            ["PRODUCTION_CODE_CHANGED"] = false
        };

        File.WriteAllText(Path.Combine(root, "eval", "accuracy", "hierarchy-human-authority.v1.json"),
            output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, "docs", "accuracy", "hierarchy-human-annotation-guideline.md"), Guideline(total, pilot.Count));
    }

    [Fact]
    public void AnnotationArtifactCannotClaimHumanAuthorityBeforeLabels()
    {
        var path = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "eval", "accuracy", "hierarchy-human-authority-packet.v1.json");
        Assert.True(File.Exists(path), "G0 packet is required before checking the G1 protocol.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal("READY_FOR_HUMAN_ANNOTATION", root.GetProperty("status").GetString());
        Assert.Equal("NO_HUMAN_HIERARCHY_AUTHORITY_RETAINED", root.GetProperty("authorityStatus").GetString());
        Assert.Equal("documentSha256 + sourceLineIds + occurrenceId", root.GetProperty("identityRule").GetString());
        Assert.Equal(422, root.GetProperty("summary").GetProperty("occurrenceSeeds").GetInt32());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("humanAnnotatedOccurrences").GetInt32());
    }

    private static JsonObject Field(params string[] valueNames) => new()
    {
        ["status"] = "NOT_OBSERVABLE",
        ["value"] = null,
        ["valueNames"] = new JsonArray(valueNames.Select(value => JsonValue.Create(value)).ToArray()),
        ["annotatorId"] = null,
        ["annotatedAt"] = null,
        ["annotationVersion"] = null
    };

    private static HashSet<int> PilotIndexes(JsonElement[] rows)
    {
        var indexes = new HashSet<int>();
        if (rows.Length > 0) indexes.Add(0);
        if (rows.Length > 1) indexes.Add(Math.Max(0, rows.Length / 2));
        if (rows.Length > 2) indexes.Add(rows.Length - 1);
        var appendix = rows.Select((row, index) => (row, index))
            .FirstOrDefault(pair => (pair.row.GetProperty("sourceText").GetString() ?? string.Empty)
                .Contains("appendix", StringComparison.OrdinalIgnoreCase));
        if (appendix.row.ValueKind != JsonValueKind.Undefined) indexes.Add(appendix.index);
        for (var index = 0; indexes.Count < 6 && index < rows.Length; index++) indexes.Add(index);
        while (indexes.Count > 6) indexes.Remove(indexes.Max());
        return indexes;
    }

    private static string PilotRole(string text) =>
        text.Contains("appendix", StringComparison.OrdinalIgnoreCase) ? "appendix_or_ambiguous" : "ordinary_or_nested";

    private static string Guideline(int total, int pilot) =>
        "# Human Hierarchy Annotation Guideline\n\n" +
        "## Status\n\n" +
        $"G1 freezes a blind protocol for {total} occurrence seeds. The pilot contains {pilot} rows across four documents.\n" +
        "No labels are fabricated by the harness. The generated authority artifact remains `PENDING_HUMAN_ANNOTATION` until a human annotator persists labels and provenance.\n\n" +
        "## Blindness\n\n" +
        "Annotators may see only the document, source occurrence, and neighboring source lines needed to understand structure. Do not expose predicted level, parent, scope, type/path, confidence, validator output, or emitted output. Set `PREDICTION_VISIBLE_DURING_ANNOTATION=false`.\n\n" +
        "## Identity\n\n" +
        "Join only by `documentSha256 + sourceLineIds + occurrenceId`. Never join by text, title, array position, candidate id, or rank. Duplicate text remains separate occurrences.\n\n" +
        "## Dimensions\n\n" +
        "Annotate each dimension independently with `OBSERVABLE`, `NOT_OBSERVABLE`, or `AMBIGUOUS`.\n\n" +
        "- `level`: integer semantic hierarchy level when supported by document structure; do not use font size as authority.\n" +
        "- `parentOccurrenceId`: exact occurrence id, or null only when the occurrence is demonstrably a root. Do not use parent text or array index.\n" +
        "- `scope`: deterministic `scopeStartOccurrenceId` and `scopeEndOccurrenceId`; use `NOT_OBSERVABLE` when boundaries are unclear.\n" +
        "- `typePath`: annotate only against an already frozen ontology. If no frozen ontology applies, use `NOT_OBSERVABLE`.\n\n" +
        "Document title, running headers, TOC entries, appendices, and table-only text must be considered as context. G1 does not change heading labels.\n\n" +
        "## Provenance\n\n" +
        "Every completed row requires `annotatorId`, `annotatedAt`, `annotationVersion`, and optional evidence source line ids. Disagreement is recorded per dimension and adjudicated before full annotation.\n\n" +
        "## Closure gate\n\n" +
        "Only after human labels exist may the artifact change to `HUMAN_AUTHORITY_CREATED=true`. Then report dimension counts independently; denominators are the counts with that dimension status `OBSERVABLE`.\n\n" +
        "`PROVIDER_CALLS=0` and `PRODUCTION_CODE_CHANGED=false`.\n";
}
