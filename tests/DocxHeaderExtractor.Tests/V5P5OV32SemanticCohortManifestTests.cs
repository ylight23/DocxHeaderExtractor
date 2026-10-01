using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free freeze for the V3.2 semantic-quality cohort; it never calls a provider or reads Gold.</summary>
public sealed class V5P5OV32SemanticCohortManifestTests
{
    private const string Root = "artifacts/v5-p5o-v32-semantic-cohort-manifest";
    private static readonly (string Role, string DocumentId, string Pdf, string PackId)[] Selection =
    [
        ("MAX_OWNED_AND_MULTIPART", "SRC-089", SourcePdfCorpus.Src089, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        ("L1472_OWNER_OMISSION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017"),
        ("L1710_RETYPING", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020"),
        ("MULTIPART_RELATION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011"),
    ];

    [Fact]
    public void Freeze_four_v32_bodies_and_quality_measurement_contract()
    {
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
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
            var request = V5SemanticSparseDecisionComposerV3_1.Compose(contract, pack.Packet);
            var body = OpenRouterQwen37JsonObjectCarrierV3.Build(request, pack.MaxCompletionTokens, envelope);
            FreezeArtifact.AssertText(Root, $"wire-bodies/{selected.Role}.json", Encoding.UTF8.GetString(body.PayloadBytes));
            rows.Add(new { selected.Role, selected.DocumentId, selected.PackId, semanticRequestHash = request.RequestHash,
                providerRequestHash = body.Hash, providerRequestBytes = body.Bytes, providerBodyFile = $"wire-bodies/{selected.Role}.json",
                providerBodySha256 = body.Hash, ownedCount = pack.OwnedAliases.Count, visibleCount = pack.VisibleAliases.Count,
                maxCompletionTokens = pack.MaxCompletionTokens, maxResponseUtf8Bytes = request.ResponseBounds.MaxResponseUtf8Bytes });
        }
        FreezeArtifact.AssertJson(Root, "execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p5o-v32-semantic-cohort-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
            protocol = V5Protocol.ClaimSchemaVersionV3_2, composer = V5SemanticSparseDecisionComposerV3_1.Version,
            providerCalls = 0, goldRead = false, semanticScore = "NOT_RUN", requestCount = 4,
            route = new { gateway = "openrouter", model = envelope.Model, providerPin = envelope.Provider, temperature = 0, reasoning = "none", streaming = true, responseFormat = "json_object" },
            executionGate = new { maximumProviderCalls = 4, retry = 0, repair = false, fallback = false, goldRead = false },
            measurement = new { transportValid = "NOT_RUN", parserValid = "NOT_RUN", usableSparseDecisions = "NOT_RUN", boundClaims = "NOT_RUN", binderRefusals = "NOT_RUN", semanticTpFpFn = "NOT_RUN", finalProjectionQuality = "NOT_RUN", v4F1Baseline = 0.754 },
            rows,
        });
    }
}
