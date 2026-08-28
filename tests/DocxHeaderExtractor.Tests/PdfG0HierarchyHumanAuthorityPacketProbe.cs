using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// G0 prepares occurrence-stable hierarchy review material. Silver occurrences are only packet
/// seeds; they are never promoted to human authority by this probe.
/// </summary>
public sealed class PdfG0HierarchyHumanAuthorityPacketProbe
{
    private static readonly string[] Documents = ["004", "030", "043", "058"];

    [Fact]
    public void WriteOccurrenceLevelHumanAuthorityPacket()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var documents = new JsonArray();
        var total = 0;

        foreach (var documentId in Documents)
        {
            var sourcePath = Path.Combine(root, "eval", "benchmark-n3", "source-packets",
                $"{documentId}-blind-source-review.v1.json");
            var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels",
                $"{documentId}-n3.2-silver-model-assisted.v1.json");
            Assert.True(File.Exists(sourcePath), $"Missing source packet for {documentId}.");
            Assert.True(File.Exists(silverPath), $"Missing silver seed for {documentId}.");

            using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
            using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
            var sourceRoot = source.RootElement;
            var occurrences = new JsonArray();
            foreach (var item in silver.RootElement.GetProperty("headingOccurrences").EnumerateArray())
            {
                var sourceLineIds = new JsonArray(item.GetProperty("sourceLineIds").EnumerateArray()
                    .Select(line => JsonValue.Create(line.GetString())!).ToArray());
                var occurrenceId = item.GetProperty("goldStableId").GetString()!;
                occurrences.Add(new JsonObject
                {
                    ["occurrenceId"] = occurrenceId,
                    ["sourceLineIds"] = sourceLineIds,
                    ["page"] = item.GetProperty("page").GetInt32(),
                    ["sourceText"] = item.GetProperty("sourceText").GetString(),
                    ["seedProvenance"] = "MODEL_ASSISTED_SILVER_PROPOSAL_ONLY",
                    ["authorityStatus"] = "PENDING_HUMAN_ANNOTATION",
                    ["level"] = NotObservable("human_level_annotation_missing"),
                    ["parent"] = NotObservable("human_parent_annotation_missing"),
                    ["scope"] = NotObservable("human_scope_annotation_missing"),
                    ["typePath"] = NotObservable("human_type_path_annotation_missing")
                });
            }

            total += occurrences.Count;
            documents.Add(new JsonObject
            {
                ["documentId"] = documentId,
                ["documentSha256"] = sourceRoot.GetProperty("documentSha256").GetString(),
                ["sourcePacket"] = $"eval/benchmark-n3/source-packets/{documentId}-blind-source-review.v1.json",
                ["seedPacket"] = $"eval/benchmark-n3/silver-labels/{documentId}-n3.2-silver-model-assisted.v1.json",
                ["occurrences"] = occurrences
            });
        }

        var output = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "hierarchy_human_authority_packet",
            ["packetVersion"] = "hierarchy-human-authority-packet.v1",
            ["status"] = "READY_FOR_HUMAN_ANNOTATION",
            ["authorityStatus"] = "NO_HUMAN_HIERARCHY_AUTHORITY_RETAINED",
            ["identityRule"] = "documentSha256 + sourceLineIds + occurrenceId",
            ["occurrenceIdentityAuthority"] = "sourceLineIds and occurrenceId are preserved; seed occurrence ids remain unadjudicated",
            ["dimensions"] = new JsonArray("level", "parent", "scope", "type", "path"),
            ["notObservablePolicy"] = "Missing human annotation remains NOT_OBSERVABLE; no marker, model, silver, title, text, or positional inference is promoted.",
            ["sourceArtifacts"] = new JsonArray(
                "eval/benchmark-n3/source-packets/004-blind-source-review.v1.json",
                "eval/benchmark-n3/source-packets/030-blind-source-review.v1.json",
                "eval/benchmark-n3/source-packets/043-blind-source-review.v1.json",
                "eval/benchmark-n3/source-packets/058-blind-source-review.v1.json",
                "eval/benchmark-n3/silver-labels/004-n3.2-silver-model-assisted.v1.json",
                "eval/benchmark-n3/silver-labels/030-n3.2-silver-model-assisted.v1.json",
                "eval/benchmark-n3/silver-labels/043-n3.2-silver-model-assisted.v1.json",
                "eval/benchmark-n3/silver-labels/058-n3.2-silver-model-assisted.v1.json"),
            ["documents"] = documents,
            ["summary"] = new JsonObject
            {
                ["documents"] = Documents.Length,
                ["occurrenceSeeds"] = total,
                ["humanAnnotatedOccurrences"] = 0,
                ["joinableHumanAuthorityOccurrences"] = 0,
                ["hierarchyAccuracyStatus"] = "NOT_OBSERVABLE_UNTIL_HUMAN_ANNOTATION"
            },
            ["PROVIDER_CALLS"] = 0,
            ["PRODUCTION_CODE_CHANGED"] = false
        };

        var directory = Path.Combine(root, "eval", "accuracy");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "hierarchy-human-authority-packet.v1.json"),
            output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var docsDirectory = Path.Combine(root, "docs", "accuracy");
        Directory.CreateDirectory(docsDirectory);
        File.WriteAllText(Path.Combine(docsDirectory, "hierarchy-human-authority-packet.md"),
            $"# Hierarchy Human Authority Packet\n\n" +
            "Status: `READY_FOR_HUMAN_ANNOTATION`\n\n" +
            $"The packet preserves {total} occurrence-level seeds across {Documents.Length} documents. Identity is\n" +
            "`documentSha256 + sourceLineIds + occurrenceId`; duplicate text is not an identity key.\n\n" +
            "The seeds come from model-assisted silver only and are explicitly `PENDING_HUMAN_ANNOTATION`. No\n" +
            "level, parent, scope, type, or path value is inferred. Every unannotated field is `NOT_OBSERVABLE`.\n" +
            "Consequently, `joinableHumanAuthorityOccurrences=0` and hierarchy accuracy remains\n" +
            "`NOT_OBSERVABLE` until a human annotates the packet.\n\n" +
            "`PROVIDER_CALLS=0` and `PRODUCTION_CODE_CHANGED=false`.\n\n" +
            "Output artifact: `eval/accuracy/hierarchy-human-authority-packet.v1.json`.\n");
    }

    [Fact]
    public void PacketKeepsHumanAuthorityDistinctFromSilver()
    {
        var path = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "eval", "accuracy",
            "hierarchy-human-authority-packet.v1.json");
        Assert.True(File.Exists(path), "Run the packet writer before checking the frozen artifact.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal("NO_HUMAN_HIERARCHY_AUTHORITY_RETAINED", root.GetProperty("authorityStatus").GetString());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("joinableHumanAuthorityOccurrences").GetInt32());
        Assert.Equal(0, root.GetProperty("PROVIDER_CALLS").GetInt32());
        Assert.False(root.GetProperty("PRODUCTION_CODE_CHANGED").GetBoolean());
        foreach (var row in root.GetProperty("documents").EnumerateArray())
        foreach (var occurrence in row.GetProperty("occurrences").EnumerateArray())
        {
            Assert.Equal("PENDING_HUMAN_ANNOTATION", occurrence.GetProperty("authorityStatus").GetString());
            foreach (var dimension in new[] { "level", "parent", "scope", "typePath" })
                Assert.Equal("NOT_OBSERVABLE", occurrence.GetProperty(dimension).GetProperty("status").GetString());
        }
    }

    private static JsonObject NotObservable(string reason) => new()
    {
        ["value"] = null,
        ["status"] = "NOT_OBSERVABLE",
        ["reason"] = reason,
        ["authority"] = "HUMAN_REVIEW_REQUIRED"
    };
}
