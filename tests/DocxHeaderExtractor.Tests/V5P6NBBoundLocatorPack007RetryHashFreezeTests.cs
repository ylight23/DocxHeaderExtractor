using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes the separately authorized PACK_007 repeat without loading Gold.</summary>
public sealed class V5P6NBBoundLocatorPack007RetryHashFreezeTests
{
    private const string Root = "artifacts/v5-p6nb-full31-reasoning-lane";

    [Fact]
    public void Freeze_pack007_repeat_request_and_raw_response_hashes_without_Gold()
    {
        using var manifestDoc = Read("execution-manifest.v1.json");
        using var resultDoc = Read("result.v1.json");
        using var cohortFreezeDoc = Read("response-hash-freeze.v1.json");
        using var retryDoc = Read("pack-007-authorized-repeat.v1.json");
        var manifest = manifestDoc.RootElement; var result = resultDoc.RootElement;
        var cohortFreeze = cohortFreezeDoc.RootElement; var retry = retryDoc.RootElement;
        Assert.Equal(31, result.GetProperty("providerCalls").GetInt32());
        Assert.False(result.GetProperty("goldRead").GetBoolean());
        Assert.False(result.GetProperty("repair").GetBoolean()); Assert.False(result.GetProperty("fallback").GetBoolean());
        Assert.Equal(0, result.GetProperty("retry").GetInt32());
        Assert.Equal(31, manifest.GetProperty("rows").GetArrayLength());
        Assert.Equal("v5-p6nb-full31-response-hash-freeze-v1", cohortFreeze.GetProperty("schemaVersion").GetString());
        Assert.False(cohortFreeze.GetProperty("goldRead").GetBoolean());
        Assert.Equal("v5-p6nb-full31-pack007-authorized-repeat-v1", retry.GetProperty("schemaVersion").GetString());
        Assert.Equal(1, retry.GetProperty("providerCalls").GetInt32());
        Assert.False(retry.GetProperty("goldRead").GetBoolean());
        Assert.False(retry.GetProperty("repair").GetBoolean()); Assert.False(retry.GetProperty("fallback").GetBoolean());
        Assert.False(retry.GetProperty("productionPromotion").GetBoolean());
        Assert.True(retry.GetProperty("treatmentUnchanged").GetBoolean());

        var request = manifest.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-089" && row.GetProperty("parentOrdinal").GetInt32() == 7);
        var original = result.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-089" && row.GetProperty("parentOrdinal").GetInt32() == 7);
        var frozenOriginal = cohortFreeze.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-089" && row.GetProperty("parentOrdinal").GetInt32() == 7);
        Assert.Equal("length", original.GetProperty("finishReason").GetString());
        Assert.Equal("RESPONSE_OVERFLOW", original.GetProperty("analysis").GetProperty("classification").GetString());
        Assert.Equal(Hash(original.GetProperty("rawSse").GetString()!), frozenOriginal.GetProperty("rawSseSha256").GetString());
        Assert.Equal(Hash(original.GetProperty("rawResponse").GetString()!), frozenOriginal.GetProperty("rawResponseSha256").GetString());
        Assert.Equal(request.GetProperty("providerRequestHash").GetString(), retry.GetProperty("providerRequestHash").GetString());
        Assert.Equal(request.GetProperty("semanticRequestHash").GetString(), retry.GetProperty("semanticRequestHash").GetString());
        Assert.Equal(request.GetProperty("registryFingerprint").GetString(), retry.GetProperty("registryFingerprint").GetString());
        Assert.Equal("stop", retry.GetProperty("finishReason").GetString());
        Assert.Equal("PARSER_BINDER_VALID", retry.GetProperty("analysis").GetProperty("classification").GetString());
        Assert.Equal(0, retry.GetProperty("analysis").GetProperty("quarantinedHeadings").GetInt32());
        Assert.Equal(Hash(retry.GetProperty("rawSse").GetString()!), retry.GetProperty("rawSseSha256").GetString());
        Assert.Equal(Hash(retry.GetProperty("rawResponse").GetString()!), retry.GetProperty("rawResponseSha256").GetString());
        Assert.True(retry.GetProperty("sseEventCount").GetInt32() > 0);

        FreezeArtifact.AssertJson(Root, "pack-007-repeat-hash-freeze.v1.json", new
        {
            schemaVersion = "v5-p6nb-full31-pack007-repeat-hash-freeze-v1",
            sourceManifest = $"{Root}/execution-manifest.v1.json", originalResult = $"{Root}/result.v1.json",
            originalHashFreeze = $"{Root}/response-hash-freeze.v1.json", retryArtifact = $"{Root}/pack-007-authorized-repeat.v1.json",
            providerCallsDuringFreeze = 0, goldRead = false, goldMutation = "NONE", repair = false, fallback = false,
            rawEvidenceImmutable = true, exactRequestHashMatched = true, originalAttemptRetained = true,
            retryClassification = retry.GetProperty("analysis").GetProperty("classification").GetString(),
            rows = new[] { new
            {
                documentId = "SRC-089", parentOrdinal = 7, packId = retry.GetProperty("packId").GetString(),
                providerRequestSha256 = retry.GetProperty("providerRequestHash").GetString(),
                rawSseSha256 = Hash(retry.GetProperty("rawSse").GetString()!), rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(retry.GetProperty("rawSse").GetString()!),
                rawResponseSha256 = Hash(retry.GetProperty("rawResponse").GetString()!), rawResponseUtf8Bytes = retry.GetProperty("rawResponseBytes").GetInt32(),
                finishReason = retry.GetProperty("finishReason").GetString(), classification = retry.GetProperty("analysis").GetProperty("classification").GetString(),
                parserAccepted = retry.GetProperty("analysis").GetProperty("parserAccepted").GetBoolean(),
                boundHeadings = retry.GetProperty("analysis").GetProperty("boundHeadings").GetInt32(),
                quarantinedHeadings = retry.GetProperty("analysis").GetProperty("quarantinedHeadings").GetInt32(),
            } },
        });
    }

    private static JsonDocument Read(string file) => JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Path.Combine(Root.Replace('/', Path.DirectorySeparatorChar), file))));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
