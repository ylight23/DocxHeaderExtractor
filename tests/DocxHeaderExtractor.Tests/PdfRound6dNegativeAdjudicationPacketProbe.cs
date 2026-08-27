using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>Materializes the blind source-only adjudication packet; it does not assign labels.</summary>
public sealed class PdfRound6dNegativeAdjudicationPacketProbe
{
    [Fact]
    public void WriteBlindNegativeAdjudicationPacket()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "accuracy-round6");
        var sourcePath = Path.Combine(directory, "selected-cohort-negative-authority.v1.json");
        var source = JsonNode.Parse(File.ReadAllText(sourcePath))!;
        var occurrences = source["documents"]!.AsArray()
            .SelectMany(document => document!["occurrences"]!.AsArray())
            .Where(item => item!["label"]?.GetValue<string>() == "UNCERTAIN")
            .Select(item =>
            {
                var copy = (JsonObject)item!.DeepClone();
                copy.Remove("label");
                copy["adjudicatedLabel"] = null;
                copy["reviewNote"] = null;
                return copy;
            })
            .ToArray();
        var packet = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy_round6d_blind_negative_adjudication_packet",
            ["phase"] = "round6d-d",
            ["sourcePacket"] = "selected-cohort-negative-authority.v1.json",
            ["sourcePacketSha256"] = Sha256(File.ReadAllText(sourcePath)),
            ["blindSourceFirst"] = true,
            ["semanticJoined"] = false,
            ["labelsFrozen"] = false,
            ["modelCalls"] = false,
            ["productionChanges"] = false,
            ["labelSpace"] = new JsonArray("REVIEWED_HEADING", "REVIEWED_NON_HEADING", "UNCERTAIN"),
            ["occurrenceAuthority"] = "documentSha256 + page + sourceLineIds + sourceSpan; reviewKey is opaque and candidateId is absent",
            ["occurrenceCount"] = occurrences.Length,
            ["reviewerInstruction"] = "Judge only the source text, source/layout facts and local context. Do not infer a label from any pipeline output. Preserve UNCERTAIN when evidence is insufficient.",
            ["occurrences"] = new JsonArray(occurrences),
            ["freezeStatus"] = "PENDING_HUMAN_ADJUDICATION"
        };
        File.WriteAllText(Path.Combine(directory, "selected-cohort-negative-adjudication.v1.json"), packet.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
