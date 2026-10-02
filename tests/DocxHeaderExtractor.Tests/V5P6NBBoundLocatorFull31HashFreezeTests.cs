using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes P6N-B full-31 request/SSE/response hashes before the separate Gold scorer may read Gold.</summary>
public sealed class V5P6NBBoundLocatorFull31HashFreezeTests
{
    private const string Root = "artifacts/v5-p6nb-full31-reasoning-lane";

    [Fact]
    public void Freeze_full31_raw_request_sse_and_response_hashes_without_Gold()
    {
        var repo = TestRepository.Root();
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/execution-manifest.v1.json")));
        using var resultDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json")));
        var manifest = manifestDoc.RootElement;
        var result = resultDoc.RootElement;

        Assert.Equal("v5-p6nb-full31-reasoning-lane-manifest-v1", manifest.GetProperty("schemaVersion").GetString());
        Assert.Equal("PREPARED_NOT_AUTHORIZED", manifest.GetProperty("status").GetString());
        Assert.Equal(0, manifest.GetProperty("providerCalls").GetInt32());
        Assert.False(manifest.GetProperty("goldRead").GetBoolean());
        Assert.Equal(31, manifest.GetProperty("rows").GetArrayLength());
        Assert.True(manifest.GetProperty("treatment").GetProperty("reasoningEnabled").GetBoolean());
        Assert.Equal("OMITTED", manifest.GetProperty("treatment").GetProperty("reasoningEffort").GetString());
        Assert.False(manifest.GetProperty("treatment").GetProperty("ontologyPrompt").GetBoolean());
        Assert.False(manifest.GetProperty("treatment").GetProperty("goldDuringRun").GetBoolean());
        Assert.False(manifest.GetProperty("treatment").GetProperty("repair").GetBoolean());
        Assert.False(manifest.GetProperty("treatment").GetProperty("fallback").GetBoolean());
        Assert.Equal("v5-p6nb-full31-reasoning-lane-result-v1", result.GetProperty("schemaVersion").GetString());
        Assert.Equal(31, result.GetProperty("providerCalls").GetInt32());
        Assert.Equal(31, result.GetProperty("completedPrimaryAttempts").GetInt32());
        Assert.Equal(31, result.GetProperty("rows").GetArrayLength());
        Assert.False(result.GetProperty("goldRead").GetBoolean());
        Assert.False(result.GetProperty("repair").GetBoolean());
        Assert.False(result.GetProperty("fallback").GetBoolean());
        Assert.Equal(0, result.GetProperty("retry").GetInt32());
        Assert.False(result.GetProperty("productionPromotion").GetBoolean());
        Assert.Equal("CLOSED_AFTER_31_PRIMARY_ATTEMPTS", result.GetProperty("stopGate").GetString());

        var manifestRows = manifest.GetProperty("rows").EnumerateArray().ToArray();
        var resultRows = result.GetProperty("rows").EnumerateArray().ToArray();
        var frozenRows = new List<object>();
        foreach (var row in resultRows.OrderBy(row => row.GetProperty("documentId").GetString(), StringComparer.Ordinal)
                     .ThenBy(row => row.GetProperty("parentOrdinal").GetInt32()))
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var ordinal = row.GetProperty("parentOrdinal").GetInt32();
            var request = manifestRows.Single(item => item.GetProperty("documentId").GetString() == documentId &&
                item.GetProperty("parentOrdinal").GetInt32() == ordinal);
            var rawSse = row.GetProperty("rawSse").GetString()!;
            var rawResponse = row.GetProperty("rawResponse").GetString()!;
            var rawSseHash = Hash(rawSse);
            var rawResponseHash = Hash(rawResponse);
            Assert.Equal(request.GetProperty("providerRequestHash").GetString(), row.GetProperty("providerRequestHash").GetString());
            Assert.Equal(request.GetProperty("semanticRequestHash").GetString(), row.GetProperty("semanticRequestHash").GetString());
            Assert.Equal(request.GetProperty("sourceEvidenceHash").GetString(), row.GetProperty("sourceEvidenceHash").GetString());
            Assert.Equal(request.GetProperty("registryFingerprint").GetString(), row.GetProperty("registryFingerprint").GetString());
            Assert.Equal(rawSseHash, row.GetProperty("rawSseSha256").GetString());
            Assert.Equal(rawResponseHash, row.GetProperty("rawResponseSha256").GetString());
            Assert.NotEmpty(rawSse);
            Assert.Equal(Encoding.UTF8.GetByteCount(rawResponse), row.GetProperty("rawResponseBytes").GetInt32());
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            Assert.True(row.GetProperty("sseEventCount").GetInt32() > 0);
            frozenRows.Add(new
            {
                documentId, parentOrdinal = ordinal, packId = row.GetProperty("packId").GetString(),
                providerRequestSha256 = row.GetProperty("providerRequestHash").GetString(),
                rawSseSha256 = rawSseHash, rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(rawSse),
                rawResponseSha256 = rawResponseHash, rawResponseUtf8Bytes = Encoding.UTF8.GetByteCount(rawResponse),
                finishReason = row.GetProperty("finishReason").GetString(), retryCount = row.GetProperty("retryCount").GetInt32(),
                classification = row.GetProperty("analysis").GetProperty("classification").GetString(),
                parserAccepted = row.GetProperty("analysis").GetProperty("parserAccepted").GetBoolean(),
                boundHeadings = row.GetProperty("analysis").GetProperty("boundHeadings").GetInt32(),
                quarantinedHeadings = row.GetProperty("analysis").GetProperty("quarantinedHeadings").GetInt32(),
            });
        }

        var classifications = resultRows.GroupBy(row => row.GetProperty("analysis").GetProperty("classification").GetString()!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(30, classifications["PARSER_BINDER_VALID"]);
        Assert.Equal(1, classifications["RESPONSE_OVERFLOW"]);
        Assert.All(resultRows, row => Assert.Equal(0, row.GetProperty("analysis").GetProperty("quarantinedHeadings").GetInt32()));

        FreezeArtifact.AssertJson(Root, "response-hash-freeze.v1.json", new
        {
            schemaVersion = "v5-p6nb-full31-response-hash-freeze-v1",
            sourceManifest = $"{Root}/execution-manifest.v1.json", sourceResult = $"{Root}/result.v1.json",
            providerCallsDuringFreeze = 0, goldRead = false, goldMutation = "NONE", repair = false, fallback = false, retry = 0,
            rawEvidenceImmutable = true, requestSseAndAssembledResponseHashesReverified = true,
            full31ExecutionStatus = "31_PRIMARY_ATTEMPTS_PERSISTED",
            responseClassifications = classifications,
            notEvaluableRowsRemainInCohort = true,
            rows = frozenRows,
        });
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
