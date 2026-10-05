using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free raw/hash freeze for the authorized four-call typography-only diagnostic screen.</summary>
public sealed class V5P6TH2CTypographyOnlyScreenCaptureFreezeTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string PreflightRoot = Root + "/p6th2c-typography-only-screen-preflight";
    private const string CaptureRoot = Root + "/p6th2c-typography-only-screen-capture-20261005";

    [Fact]
    public void Four_typography_only_responses_are_hash_frozen_before_gold_access()
    {
        var repo = TestRepository.Root();
        var capturePath = TestRepository.Path(CaptureRoot);
        if (!Directory.Exists(capturePath)) return; // No local raw archive on a clean clone.

        var preflightPath = TestRepository.Path(PreflightRoot + "/typography-only-screen-preflight.v1.json");
        var manifestPath = TestRepository.Path(PreflightRoot + "/execution-manifest.v1.json");
        var resultPath = Path.Combine(capturePath, "result.v1.json");
        Assert.True(File.Exists(preflightPath));
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(resultPath));
        var preflightBytes = File.ReadAllBytes(preflightPath);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var resultBytes = File.ReadAllBytes(resultPath);
        using var manifest = JsonDocument.Parse(manifestBytes);
        using var result = JsonDocument.Parse(resultBytes);
        Assert.Equal("PREPARED_NOT_AUTHORIZED_PROVIDER_CALLS_ZERO_GOLD_CLOSED", manifest.RootElement.GetProperty("status").GetString());
        Assert.Equal(Hash(preflightBytes), manifest.RootElement.GetProperty("preflightSha256").GetString());
        Assert.Equal("COMPLETE_RAW_CAPTURE_ALL_FOUR_CONTRACT_VALID_GOLD_CLOSED", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(4, result.RootElement.GetProperty("providerCallsAttempted").GetInt32());
        Assert.Equal(4, result.RootElement.GetProperty("authorizedPrimaryCalls").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("retry").GetInt32());
        Assert.False(result.RootElement.GetProperty("repair").GetBoolean());
        Assert.False(result.RootElement.GetProperty("fallback").GetBoolean());
        Assert.False(result.RootElement.GetProperty("goldRead").GetBoolean());

        var plans = manifest.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var summaries = result.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(4, plans.Length);
        Assert.Equal(4, summaries.Length);
        var frozenRows = new List<object>(4);
        for (var index = 0; index < 4; index++)
        {
            var plan = plans[index];
            var row = plan.GetProperty("row");
            var treatment = row.GetProperty("typographyOnlyTreatment");
            var documentId = row.GetProperty("documentId").GetString()!;
            var anchor = row.GetProperty("anchor").GetString()!;
            var rawPath = Path.Combine(capturePath, $"{documentId}_{anchor}.raw-capture.v1.json");
            Assert.True(File.Exists(rawPath), $"h2c-typography-only-raw-missing:{documentId}:{anchor}");
            var rawBytes = File.ReadAllBytes(rawPath);
            using var raw = JsonDocument.Parse(rawBytes);
            var root = raw.RootElement;
            var summary = summaries[index];
            Assert.Equal(index + 1, plan.GetProperty("callOrdinal").GetInt32());
            Assert.Equal(index + 1, root.GetProperty("providerCallOrdinal").GetInt32());
            Assert.Equal(index + 1, summary.GetProperty("callOrdinal").GetInt32());
            Assert.Equal(documentId, root.GetProperty("DocumentId").GetString());
            Assert.Equal(documentId, summary.GetProperty("DocumentId").GetString());
            Assert.Equal(anchor, root.GetProperty("Anchor").GetString());
            Assert.Equal(anchor, summary.GetProperty("Anchor").GetString());
            Assert.Equal(row.GetProperty("packId").GetString(), root.GetProperty("PackId").GetString());
            Assert.Equal(treatment.GetProperty("providerBodySha256").GetString(), root.GetProperty("providerBodySha256").GetString());
            Assert.Equal(treatment.GetProperty("userMessageSha256").GetString(), root.GetProperty("userMessageSha256").GetString());
            Assert.Equal(treatment.GetProperty("systemPromptSha256").GetString(), root.GetProperty("systemPromptSha256").GetString());
            Assert.Equal("stop", root.GetProperty("finishReason").GetString());
            Assert.Equal(0, root.GetProperty("retryCount").GetInt32());
            Assert.Equal("VALID", root.GetProperty("contractStatus").GetString());
            Assert.False(root.GetProperty("goldReadDuringCapture").GetBoolean());
            var rawSse = root.GetProperty("rawSse").GetString()!;
            var response = root.GetProperty("rawResponse").GetString()!;
            Assert.Equal(Hash(rawSse), root.GetProperty("rawSseSha256").GetString());
            Assert.Equal(Hash(response), root.GetProperty("rawResponseSha256").GetString());
            Assert.Equal(root.GetProperty("rawSseSha256").GetString(), summary.GetProperty("rawSseSha256").GetString());
            Assert.Equal(root.GetProperty("rawResponseSha256").GetString(), summary.GetProperty("rawResponseSha256").GetString());
            Assert.Equal("stop", summary.GetProperty("finishReason").GetString());
            Assert.Equal(0, summary.GetProperty("retryCount").GetInt32());
            Assert.Equal("VALID", summary.GetProperty("contractStatus").GetString());

            frozenRows.Add(new
            {
                callOrdinal = index + 1,
                documentId,
                packId = row.GetProperty("packId").GetString(),
                anchor,
                providerBodySha256 = root.GetProperty("providerBodySha256").GetString(),
                userMessageSha256 = root.GetProperty("userMessageSha256").GetString(),
                systemPromptSha256 = root.GetProperty("systemPromptSha256").GetString(),
                rawFileSha256 = Hash(rawBytes),
                rawSseSha256 = root.GetProperty("rawSseSha256").GetString(),
                rawResponseSha256 = root.GetProperty("rawResponseSha256").GetString(),
                finishReason = root.GetProperty("finishReason").GetString(),
                retryCount = root.GetProperty("retryCount").GetInt32(),
                contractStatus = root.GetProperty("contractStatus").GetString(),
                promptTokens = NullableInt(root, "promptTokens"),
                completionTokens = NullableInt(root, "completionTokens"),
                reasoningTokens = NullableInt(root, "reasoningTokens"),
                latencyMs = root.GetProperty("latencyMs").GetDouble(),
            });
        }

        FreezeArtifact.AssertJson(CaptureRoot, "capture-freeze.v1.json", new
        {
            schemaVersion = "v5-p6th2c-typography-only-screen-capture-freeze-v1",
            status = "RAW_HASH_VERIFIED_FOUR_CONTRACT_VALID_RESPONSES_GOLD_STILL_CLOSED",
            preflightCommit = "148a3de",
            preflightSha256 = Hash(preflightBytes),
            executionManifestSha256 = Hash(manifestBytes),
            resultSha256 = Hash(resultBytes),
            attemptedPrimaryCalls = 4,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            rawResponses = "LOCAL_IMMUTABLE_ARCHIVE_NOT_COMMITTED; HASHES_AND_PROVIDER_USAGE_ONLY_IN_THIS_RECEIPT",
            calls = frozenRows,
        });
    }

    private static int? NullableInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
