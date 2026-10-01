using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free freeze of the only four full-pack V3.1 sparse P5M bodies.</summary>
public sealed class V5P5MSparseCanaryManifestTests
{
    private const string Root = "artifacts/v5-p5m-v31-sparse-canary-manifest";
    private static readonly (string Role, string DocumentId, string Pdf, string PackId)[] Selection =
    [
        ("MAX_OWNED_AND_MULTIPART", "SRC-089", SourcePdfCorpus.Src089, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        ("L1472_OWNER_OMISSION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017"),
        ("L1710_RETYPING", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020"),
        ("MULTIPART_RELATION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011"),
    ];

    [Fact]
    public void Freeze_exactly_four_full_pack_sparse_v31_bodies()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var cache = new Dictionary<string, IReadOnlyList<V5PackedDecisionRequestV3>>(StringComparer.Ordinal);
        var rows = new List<object>();
        foreach (var selected in Selection)
        {
            if (!cache.TryGetValue(selected.DocumentId, out var packs))
            {
                packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(selected.Pdf), selected.DocumentId, contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
                cache[selected.DocumentId] = packs;
            }
            var pack = packs.Single(item => item.PackId == selected.PackId);
            var sparse = V5SemanticSparseDecisionComposerV3_1.Compose(contract, pack.Packet);
            var body = OpenRouterQwen37JsonObjectCarrierV3.Build(sparse, pack.MaxCompletionTokens, envelope);
            using var prompt = JsonDocument.Parse(sparse.Prompt);
            var decisions = prompt.RootElement.GetProperty("responseSchema").GetProperty("properties").GetProperty("decisions");
            Assert.Equal("v5-source-backed-decision-3.1", prompt.RootElement.GetProperty("protocolVersion").GetString());
            Assert.Equal(0, decisions.GetProperty("minItems").GetInt32());
            Assert.Equal(pack.OwnedAliases.Count, decisions.GetProperty("maxItems").GetInt32());
            Assert.Equal(96, pack.OwnedAliases.Count);
            FreezeArtifact.AssertText(Root, $"wire-bodies/{selected.Role}.json", Encoding.UTF8.GetString(body.PayloadBytes));
            rows.Add(new { selected.Role, selected.DocumentId, selected.PackId, semanticRequestHash = sparse.RequestHash,
                providerRequestHash = body.Hash, providerRequestBytes = body.Bytes, providerBodyFile = $"wire-bodies/{selected.Role}.json",
                providerBodySha256 = body.Hash, ownedCount = pack.OwnedAliases.Count, visibleCount = pack.VisibleAliases.Count,
                maxCompletionTokens = pack.MaxCompletionTokens, maxResponseUtf8Bytes = sparse.ResponseBounds.MaxResponseUtf8Bytes });
        }
        Assert.Equal(4, rows.Count);
        FreezeArtifact.AssertJson(Root, "execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p5m-v31-sparse-provider-execution-manifest-v1", status = "PREPARED_AUTHORIZED",
            protocol = "v5-source-backed-decision-3.1", composer = "v5-semantic-decision-composer-3.1", providerCalls = 0,
            goldRead = false, semanticScore = "NOT_RUN", requestCount = 4,
            route = new { gateway = "openrouter", model = envelope.Model, providerPin = envelope.Provider, temperature = 0, reasoning = "none", streaming = true, responseFormat = "json_object" },
            executionGate = new { maximumProviderCalls = 4, retry = 0, repair = false, fallback = false, goldRead = false }, rows,
        });
    }
}
