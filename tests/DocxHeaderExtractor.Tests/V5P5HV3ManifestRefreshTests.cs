using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Freezes the exact post-P5G bodies for a possible future four-pack canary. This test has no
/// provider client and its artifacts are compared, not rewritten, in an ordinary test run.
/// </summary>
public sealed class V5P5HV3ManifestRefreshTests
{
    private const string ArtifactRoot = "artifacts/v5-p5h-v3-manifest-refresh";
    private static readonly DocumentTaskContract Contract =
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };

    private sealed record Selection(string Role, string Focus, string DocumentId, string Pdf, string PackId);

    private static readonly Selection[] Selections =
    [
        new("MAX_OWNED_AND_MULTIPART", "96 positional decisions; historical multi-atom subject evidence", "SRC-089",
            SourcePdfCorpus.Src089, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        new("L1472_OWNER_OMISSION", "historical L1472 owner omission", "SRC-095",
            SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017"),
        new("L1710_RETYPING", "historical L1710 whole-atom retyping", "SRC-095",
            SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020"),
        new("MULTIPART_RELATION", "historical multi-atom relation endpoint", "SRC-095",
            SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011"),
    ];

    [Fact]
    public void Freeze_post_p5g_four_pack_manifest_without_provider_or_gold()
    {
        Assert.Equal(4, Selections.Length);
        using var p5f = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-p5f-v3-canary/selection.v1.json")));
        var historicalSelections = p5f.RootElement.GetProperty("selectedPacks").EnumerateArray()
            .ToDictionary(row => row.GetProperty("Role").GetString()!, StringComparer.Ordinal);
        var builtByDocument = new Dictionary<string, IReadOnlyList<V5PackedDecisionRequestV3>>(StringComparer.Ordinal);
        var rows = new List<object>();

        foreach (var selection in Selections)
        {
            if (!builtByDocument.TryGetValue(selection.DocumentId, out var built))
            {
                built = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(selection.Pdf), selection.DocumentId, Contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
                builtByDocument.Add(selection.DocumentId, built);
            }

            var pack = Assert.Single(built, candidate => candidate.PackId == selection.PackId);
            var historical = historicalSelections[selection.Role];
            Assert.Equal(96, pack.OwnedAliases.Count);
            Assert.Equal(96, pack.Request.ResponseBounds.MaxDecisions);
            Assert.Equal(129, pack.Request.ResponseBounds.MaxClaimsTotal);
            Assert.Equal(49152, pack.Request.ResponseBounds.MaxResponseUtf8Bytes);
            Assert.Equal(25088, pack.MaxCompletionTokens);
            Assert.NotEqual(historical.GetProperty("semanticRequestHash").GetString(), pack.Request.RequestHash);
            Assert.NotEqual(historical.GetProperty("providerRequestHash").GetString(), pack.ProviderRequestHash);

            var bodyFile = $"wire-bodies/{selection.DocumentId}_{selection.PackId.Replace(':', '_')}.json";
            var bodyText = Encoding.UTF8.GetString(pack.ProviderBody);
            FreezeArtifact.AssertText(ArtifactRoot, bodyFile, bodyText);
            var frozenBody = File.ReadAllBytes(TestRepository.Path($"{ArtifactRoot}/{bodyFile}"));
            Assert.Equal(pack.ProviderRequestHash, Sha256(frozenBody));
            Assert.True(frozenBody.AsSpan().SequenceEqual(pack.ProviderBody));

            rows.Add(new
            {
                role = selection.Role,
                focus = selection.Focus,
                documentId = selection.DocumentId,
                packId = selection.PackId,
                semanticRequestHash = pack.Request.RequestHash,
                providerRequestHash = pack.ProviderRequestHash,
                providerRequestBytes = pack.ProviderRequestBytes,
                providerBodyFile = bodyFile,
                providerBodySha256 = Sha256(frozenBody),
                maxCompletionTokens = pack.MaxCompletionTokens,
                decisionCountExpected = pack.OwnedAliases.Count,
                maxClaimsPerDecision = pack.Request.ResponseBounds.MaxClaimsPerDecision,
                maxClaimsTotal = pack.Request.ResponseBounds.MaxClaimsTotal,
                maxResponseUtf8Bytes = pack.Request.ResponseBounds.MaxResponseUtf8Bytes,
                differsFromHistoricalP5F = true,
            });
        }

        FreezeArtifact.AssertJson(ArtifactRoot, "execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p5h-v3-provider-execution-manifest-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            protocol = V5SemanticDecisionContractV3.SchemaVersion,
            route = new
            {
                gateway = "openrouter",
                api = "chat-completions",
                model = Envelope.Model,
                providerPin = Envelope.Provider,
                temperature = 0,
                reasoning = Envelope.Reasoning,
                streaming = Envelope.Streaming,
                responseFormat = Envelope.ResponseFormat,
            },
            providerRequestBodyFormat = "OpenRouter chat-completions, stream=true, json_object; exact post-P5G UTF-8 bodies stored under wire-bodies/ and hashed per pack",
            responseContract = new
            {
                exactOwnedDecisions = 96,
                maxClaimsPerDecision = 10,
                maxClaimsTotal = 129,
                maxSerializedResponseUtf8Bytes = 49152,
                maxCompletionTokens = 25088,
                byteAndTokenBounds = "independent; no bytes-per-token equivalence is asserted",
            },
            requestCount = Selections.Length,
            rows,
            historicalArtifacts = new
            {
                p5d = "PRESERVED_UNCHANGED",
                p5f = "PRESERVED_UNCHANGED; hash identities deliberately differ from P5H",
            },
            executionGate = new
            {
                providerExecutionAuthorized = false,
                freshExplicitAuthorizationRequired = true,
                maximumProviderCallsIfAuthorized = 4,
                full31PackCohortAuthorized = false,
                goldRead = false,
                semanticScore = "NOT_RUN",
            },
            providerCalls = 0,
            goldRead = false,
        });
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
