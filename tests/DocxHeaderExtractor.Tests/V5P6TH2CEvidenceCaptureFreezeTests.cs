using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Verifies and hashes both H2-C evidence arms without reading Gold.</summary>
public sealed class V5P6TH2CEvidenceCaptureFreezeTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string PreflightRoot = Root + "/p6th2c-evidence-complete-preflight";
    private const string CaptureRoot = Root + "/p6th2c-evidence-complete-capture-20261005";

    private sealed record FrozenCall(int CallOrdinal, string Arm, string DocumentId, string PackId, string Anchor,
        string ProviderBodySha256, string RawFileSha256, string? RawSseSha256, string? RawResponseSha256,
        string? FinishReason, int RetryCount, string ContractStatus, string? TransportError);

    [Fact]
    public void H2C_evidence_capture_is_hash_frozen_before_any_gold_access()
    {
        var repo = TestRepository.Root();
        var preflightPath = Path.Combine(repo, PreflightRoot.Replace('/', Path.DirectorySeparatorChar), "h2c-evidence-complete-preflight.v1.json");
        var executionManifestPath = Path.Combine(repo, PreflightRoot.Replace('/', Path.DirectorySeparatorChar), "execution-manifest.v1.json");
        var capturePath = Path.Combine(repo, CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(capturePath, "result.v1.json");
        Assert.True(File.Exists(preflightPath), "h2c-evidence-preflight-missing");
        Assert.True(File.Exists(executionManifestPath), "h2c-evidence-execution-manifest-missing");
        Assert.True(File.Exists(resultPath), "h2c-evidence-result-missing");

        using var preflight = JsonDocument.Parse(File.ReadAllBytes(preflightPath));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(executionManifestPath));
        using var result = JsonDocument.Parse(File.ReadAllBytes(resultPath));
        var manifestRoot = manifest.RootElement;
        var resultRoot = result.RootElement;
        Assert.Equal("PREPARED_NOT_AUTHORIZED_PROVIDER_CALLS_ZERO_GOLD_CLOSED", manifestRoot.GetProperty("status").GetString());
        Assert.Equal("ALL_PRIMARY_ATTEMPTS_RAW_FROZEN_GOLD_NOT_READ", resultRoot.GetProperty("status").GetString());
        Assert.Equal(62, manifestRoot.GetProperty("primaryCalls").GetInt32());
        Assert.Equal(62, resultRoot.GetProperty("providerCallsAttempted").GetInt32());
        Assert.False(resultRoot.GetProperty("goldRead").GetBoolean());
        Assert.Equal(62, manifestRoot.GetProperty("requests").GetArrayLength());
        Assert.Equal(62, resultRoot.GetProperty("rows").GetArrayLength());
        Assert.Equal(Hash(File.ReadAllBytes(preflightPath)), manifestRoot.GetProperty("preflightSha256").GetString());

        var files = new List<FrozenCall>(62);
        var plannedRows = manifestRoot.GetProperty("requests").EnumerateArray().ToArray();
        var resultRows = resultRoot.GetProperty("rows").EnumerateArray().ToArray();
        for (var index = 0; index < 62; index++)
        {
            var plan = plannedRows[index];
            var summary = resultRows[index];
            var arm = plan.GetProperty("Arm").GetString()!;
            var documentId = plan.GetProperty("DocumentId").GetString()!;
            var packId = plan.GetProperty("PackId").GetString()!;
            var anchor = plan.GetProperty("Anchor").GetString()!;
            var rawPath = Path.Combine(capturePath, arm == "A" ? "arm-a" : "arm-b", $"{documentId}_{anchor}.raw-capture.v1.json");
            Assert.True(File.Exists(rawPath), $"h2c-evidence-raw-missing:{arm}:{documentId}:{anchor}");

            using var raw = JsonDocument.Parse(File.ReadAllBytes(rawPath));
            var rawRoot = raw.RootElement;
            Assert.Equal(index + 1, plan.GetProperty("callOrdinal").GetInt32());
            Assert.Equal(index + 1, rawRoot.GetProperty("providerCallOrdinal").GetInt32());
            Assert.Equal(arm, rawRoot.GetProperty("arm").GetString());
            Assert.Equal(documentId, rawRoot.GetProperty("DocumentId").GetString());
            Assert.Equal(packId, rawRoot.GetProperty("PackId").GetString());
            Assert.Equal(anchor, rawRoot.GetProperty("Anchor").GetString());
            Assert.Equal(plan.GetProperty("providerBodySha256").GetString(), rawRoot.GetProperty("providerBodySha256").GetString());
            Assert.Equal(plan.GetProperty("userMessageSha256").GetString(), rawRoot.GetProperty("userMessageSha256").GetString());
            Assert.Equal(plan.GetProperty("systemPromptSha256").GetString(), rawRoot.GetProperty("systemPromptSha256").GetString());
            Assert.Equal(0, rawRoot.GetProperty("retryCount").GetInt32());
            Assert.False(rawRoot.GetProperty("goldReadDuringCapture").GetBoolean());

            var rawSse = rawRoot.GetProperty("rawSse").ValueKind == JsonValueKind.String ? rawRoot.GetProperty("rawSse").GetString() : null;
            var response = rawRoot.GetProperty("rawResponse").ValueKind == JsonValueKind.String ? rawRoot.GetProperty("rawResponse").GetString() : null;
            var rawSseSha = rawSse is null ? null : Hash(rawSse);
            var responseSha = response is null ? null : Hash(response);
            Assert.Equal(rawSseSha, rawRoot.GetProperty("rawSseSha256").ValueKind == JsonValueKind.String ? rawRoot.GetProperty("rawSseSha256").GetString() : null);
            Assert.Equal(responseSha, rawRoot.GetProperty("rawResponseSha256").ValueKind == JsonValueKind.String ? rawRoot.GetProperty("rawResponseSha256").GetString() : null);

            Assert.Equal(arm, summary.GetProperty("arm").GetString());
            Assert.Equal(documentId, summary.GetProperty("DocumentId").GetString());
            Assert.Equal(anchor, summary.GetProperty("Anchor").GetString());
            Assert.Equal(rawRoot.GetProperty("finishReason").ValueKind == JsonValueKind.String ? rawRoot.GetProperty("finishReason").GetString() : null,
                summary.GetProperty("finishReason").ValueKind == JsonValueKind.String ? summary.GetProperty("finishReason").GetString() : null);
            Assert.Equal(rawRoot.GetProperty("contractStatus").GetString(), summary.GetProperty("contractStatus").GetString());

            files.Add(new FrozenCall(index + 1, arm, documentId, packId, anchor,
                rawRoot.GetProperty("providerBodySha256").GetString()!, Hash(File.ReadAllBytes(rawPath)), rawSseSha, responseSha,
                rawRoot.GetProperty("finishReason").ValueKind == JsonValueKind.String ? rawRoot.GetProperty("finishReason").GetString() : null,
                rawRoot.GetProperty("retryCount").GetInt32(), rawRoot.GetProperty("contractStatus").GetString()!,
                rawRoot.GetProperty("transportError").ValueKind == JsonValueKind.String ? rawRoot.GetProperty("transportError").GetString() : null));
        }

        Assert.Equal(31, files.Count(row => row.Arm == "A"));
        Assert.Equal(31, files.Count(row => row.Arm == "B"));
        Assert.Equal(0, files.Sum(row => row.RetryCount));
        Assert.Equal(1, files.Count(row => row.ContractStatus == "NO_RESPONSE"));
        Assert.Equal(61, files.Count(row => row.ContractStatus == "VALID"));
        Assert.Equal(1, files.Count(row => row.FinishReason is null));
        Assert.Equal(1, files.Count(row => row.TransportError?.Contains("429", StringComparison.Ordinal) == true));

        FreezeArtifact.AssertJson(CaptureRoot, "capture-freeze.v1.json", new
        {
            schemaVersion = "v5-p6th2c-evidence-paired-capture-freeze-v1",
            status = "RAW_HASH_VERIFIED_61_RESPONSES_1_UPSTREAM_429_GOLD_STILL_CLOSED",
            preflightSha256 = Hash(File.ReadAllBytes(preflightPath)),
            executionManifestSha256 = Hash(File.ReadAllBytes(executionManifestPath)),
            resultSha256 = Hash(File.ReadAllBytes(resultPath)),
            attemptedPrimaryCalls = files.Count,
            armA = new
            {
                calls = files.Count(row => row.Arm == "A"),
                validResponses = files.Count(row => row.Arm == "A" && row.ContractStatus == "VALID"),
                noResponse = files.Count(row => row.Arm == "A" && row.ContractStatus == "NO_RESPONSE"),
                rawFiles = files.Where(row => row.Arm == "A").ToArray(),
            },
            armB = new
            {
                calls = files.Count(row => row.Arm == "B"),
                validResponses = files.Count(row => row.Arm == "B" && row.ContractStatus == "VALID"),
                noResponse = files.Count(row => row.Arm == "B" && row.ContractStatus == "NO_RESPONSE"),
                rawFiles = files.Where(row => row.Arm == "B").ToArray(),
            },
            providerCalls = 62,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            runtimeChanged = false,
            failureClassification = "ARM_B_DOC-0252_O66_UPSTREAM_SHARED_POOL_429_INSUFFICIENT_QUOTA; NO_RETRY_AUTHORIZED",
        });
    }

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
