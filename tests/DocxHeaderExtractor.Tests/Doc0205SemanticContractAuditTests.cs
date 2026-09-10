using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class Doc0205SemanticContractAuditTests
{
    private const string RootRelative = "eval/a99-closed-loop/doc0205-semantic-contract-audit";
    private const string SourceSha = "b145e31a58e76cad1c77967c639884d1c566020d344d725ca145fafb5de86878";

    [Fact]
    public void Offline_audit_is_frozen_and_gold_firewalled()
    {
        using var summary = LoadFromRoot("summary.v1.json");
        var root = summary.RootElement;

        Assert.Equal("a99-doc0205-semantic-contract-reconciliation-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal("SEMANTIC_DISCOVERY_GOOD_SPAN_CONTRACT_BAD", root.GetProperty("primaryClassification").GetString());
        Assert.True(root.GetProperty("sourceAuthority").GetProperty("matches").GetBoolean());
        Assert.Equal(SourceSha, root.GetProperty("sourceAuthority").GetProperty("sourceSha256").GetString());

        var scores = root.GetProperty("runTable").EnumerateArray().ToArray();
        Assert.Equal(7, scores.Length);
        Assert.Equal(0, scores[0].GetProperty("tp").GetInt32());
        Assert.Equal(15, scores[0].GetProperty("fp").GetInt32());
        Assert.Equal(71, scores[0].GetProperty("fn").GetInt32());
        Assert.Equal(104, scores[6].GetProperty("fp").GetInt32());
    }

    [Fact]
    public void Cross_model_map_has_all_gold_occurrences_and_span_audit_passes()
    {
        using var map = LoadFromRoot("gold-cross-model-map.v1.json");
        var rows = map.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(71, rows.Length);
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("headingOccurrenceId").GetString())));

        using var classifications = LoadFromRoot("prediction-classification.v1.json");
        var classificationRows = classifications.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(451, classificationRows.Length);
        Assert.All(classificationRows, row => Assert.True(row.GetProperty("sourceLookupValid").GetBoolean()));
    }

    [Fact]
    public void Frozen_prediction_and_result_hashes_still_match_their_freeze_authorities()
    {
        AssertFrozenPair(
            "eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/prediction.v1.json",
            "eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/result.v1.json",
            "eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/freeze.v1.json");
        AssertFrozenPair(
            "eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling/prediction.v1.json",
            "eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling/result.v1.json",
            "eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling/freeze.v1.json");
        AssertFrozenPair(
            "eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2/prediction.v2.json",
            "eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2/result.v2.json",
            "eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2/freeze.v2.json");
    }

    private static void AssertFrozenPair(string prediction, string result, string freeze)
    {
        using var authority = LoadAbsolute(freeze);
        Assert.Equal(authority.RootElement.GetProperty("predictionSha256").GetString(), Sha256(AbsolutePath(prediction)));
        Assert.Equal(authority.RootElement.GetProperty("resultSha256").GetString(), Sha256(AbsolutePath(result)));
        Assert.False(authority.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
    }

    private static JsonDocument LoadFromRoot(string name) => Load($"{RootRelative}/{name}");

    private static JsonDocument Load(string relativePath) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), relativePath)));

    private static JsonDocument LoadAbsolute(string relativePath) => JsonDocument.Parse(File.ReadAllText(AbsolutePath(relativePath)));

    private static string AbsolutePath(string relativePath) => Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
