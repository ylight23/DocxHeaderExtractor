using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free recomposition of the two frozen P05 source universes under protocol v2.</summary>
public sealed class V5ProtocolV2PreflightTests
{
    private const string OutputRoot = "artifacts/v5-provider-qualification-v2";
    private const string OldRoot = "artifacts/v5-provider-qualification-v1";

    [Fact]
    public void Recompose_src089_and_src095_without_provider_or_gold()
    {
        var rows = new List<object>();
        var totalPacks = 0;
        foreach (var (id, path) in new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) })
        {
            var old = LoadOld(id);
            var built = V5PdfPreflightBuilder.BuildV2(
                TestRepository.Path(path),
                id,
                DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create(),
                SemanticEvidencePackingPolicies.PdfResourceBoundedP05.PolicyId,
                new V5ProviderEnvelope("qwen/qwen3.7-flash", "Alibaba", "none", true, "json_object", 300));
            built.Preflight.Validate();
            Assert.Equal(0, built.Preflight.ProviderCalls);
            Assert.False(built.Preflight.GoldRead);
            Assert.Equal(old.GetProperty("preflight").GetProperty("packCount").GetInt32(), built.Preflight.PackCount);
            var oldRequests = old.GetProperty("requests").EnumerateArray().ToArray();
            Assert.Equal(oldRequests.Length, built.Requests.Count);
            totalPacks += built.Requests.Count;
            var ownedAliases = built.Requests.SelectMany(item => item.OwnedAliases).ToArray();
            Assert.Equal(ownedAliases.Length, ownedAliases.Distinct(StringComparer.Ordinal).Count());
            Assert.True(built.Requests.Zip(oldRequests).All(pair =>
                pair.First.OwnedAliases.SequenceEqual(pair.Second.GetProperty("ownedAliases").EnumerateArray().Select(item => item.GetString()!), StringComparer.Ordinal) &&
                pair.First.VisibleAliases.SequenceEqual(pair.Second.GetProperty("visibleAliases").EnumerateArray().Select(item => item.GetString()!), StringComparer.Ordinal)));
            rows.Add(new
            {
                documentId = id,
                packCount = built.Requests.Count,
                providerCalls = built.Preflight.ProviderCalls,
                goldRead = built.Preflight.GoldRead,
                packingPolicy = built.Preflight.PackingPolicy,
                oldPreflightHash = Sha256(File.ReadAllText(TestRepository.Path($"{OldRoot}/{id}.preflight.v1.json"))),
                newPreflightHash = built.Preflight.Hash(),
                oldClaimSchemaHash = old.GetProperty("preflight").GetProperty("claimSchemaHash").GetString(),
                newClaimSchemaHash = built.Preflight.ClaimSchemaHash,
                oldSourceUniverseHash = old.GetProperty("preflight").GetProperty("sourceUniverseSha").GetString(),
                newSourceUniverseHash = built.Preflight.SourceUniverseSha,
                sourceUniverseUnchanged = old.GetProperty("preflight").GetProperty("sourceUniverseSha").GetString() == built.Preflight.SourceUniverseSha,
                oldTaskContractHash = old.GetProperty("preflight").GetProperty("taskContractHash").GetString(),
                newTaskContractHash = built.Preflight.TaskContractHash,
                taskContractUnchanged = old.GetProperty("preflight").GetProperty("taskContractHash").GetString() == built.Preflight.TaskContractHash,
                oldPromptHash = old.GetProperty("preflight").GetProperty("promptHash").GetString(),
                newPromptHash = built.Preflight.PromptHash,
                oldComposerVersion = V5SemanticRequestComposer.Version,
                newComposerVersion = V5SemanticRequestComposerV2.Version,
                oldProviderEnvelopeHash = JsonElementHash(old.GetProperty("preflight").GetProperty("providerEnvelope")),
                newProviderEnvelopeHash = ProviderEnvelopeHash(built.Preflight.ProviderEnvelope),
                providerEnvelopeUnchanged = JsonElementHash(old.GetProperty("preflight").GetProperty("providerEnvelope")) == ProviderEnvelopeHash(built.Preflight.ProviderEnvelope),
                oldRequestHashes = old.GetProperty("preflight").GetProperty("requestHashes").EnumerateArray().Select(item => item.GetString()).ToArray(),
                newRequestHashes = built.Preflight.RequestHashes,
                semanticRequestHashesChangedByProtocolVersion = true,
                ownershipUnchanged = true,
            });
        }
        var artifact = new
        {
            schemaVersion = "v5-provider-qualification-preflight-v2",
            protocolVersion = V5Protocol.ClaimSchemaVersionV2,
            composerVersion = V5SemanticRequestComposerV2.Version,
            providerCalls = 0,
            goldRead = false,
            providerExecutionAuthorized = false,
            totalSemanticRequests = totalPacks,
            packing = SemanticEvidencePackingPolicies.PdfResourceBoundedP05.PolicyId,
            structuredOutput = new
            {
                mode = "json_object",
                strictSchemaSupported = ProviderStructuredOutputRegistry.QwenFlashAlibaba.JsonSchemaStrictSupported,
                capabilityEvidence = ProviderStructuredOutputRegistry.QwenFlashAlibaba.EvidenceSource,
            },
            documents = rows,
        };
        var root = TestRepository.Path(OutputRoot);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "preflight.v2.json"), JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "preflight.v2.md"), "# V5 protocol v2 provider-free preflight\n\nProviderCalls: `0`. GoldRead: `false`. Ownership matches frozen P05; only composer/schema hashes change. Recommended next step: a 2–3 pack provider canary after explicit authorization.\n", new UTF8Encoding(false));
    }

    private static JsonElement LoadOld(string id) =>
        JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{OldRoot}/{id}.preflight.v1.json"))).RootElement.Clone();

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string JsonElementHash(JsonElement element) => Sha256(JsonSerializer.Serialize(element));

    private static string ProviderEnvelopeHash(V5ProviderEnvelope envelope) => Sha256(JsonSerializer.Serialize(new
    {
        model = envelope.Model,
        provider = envelope.Provider,
        reasoning = envelope.Reasoning,
        streaming = envelope.Streaming,
        responseFormat = envelope.ResponseFormat,
        timeoutSeconds = envelope.TimeoutSeconds,
        usageInclude = envelope.UsageInclude,
        openRouterResponseCacheDisabled = envelope.OpenRouterResponseCacheDisabled,
        explicitCacheControl = envelope.ExplicitCacheControl,
    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
}
