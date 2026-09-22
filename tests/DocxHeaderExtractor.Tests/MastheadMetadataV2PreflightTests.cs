using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class MastheadMetadataV2PreflightTests
{
    private const string V2PromptSha256 =
        "6340d1daf507a3d2bf5ce6fbef2b5d7b61735c0e0621a62f8e885ad8b2d8a66e";
    private const string E1ClauseSha256 =
        "81d2c37cdf8542d9948396e59c653d017f69a8d1591b86279b85114b4cf6bfa8";
    private const string E1V2PromptSha256 =
        "fb46d62cb7ddbb469d56a54f3c90e37d955c8fce85b8f75257ac19a14b2c64fa";
    private const string Pack005UserMessageSha256 =
        "8f3b430a78608bb10d62fb3655d2742b236470b5f228967f6fd8b328896d8c16";
    private const string Pack006UserMessageSha256 =
        "6d867a0d836ba0fbbe5f041fc046fc932fe2fea7f23f100f7f0a59301048cff0";
    private const string Pack005ProviderRequestSha256 =
        "173a5172a58018ac616b4b06c79f20c6f314db214cc406610da2937fe03b1977";
    private const string Pack006ProviderRequestSha256 =
        "ac7f6905cfcf5377c92241d089815ab29bc4bb6db0fc40b17e67a953c0dbeec7";
    private const string ProviderModelInputPlanSha256 =
        "d788652788771cc6998fd5d94d531d07e939599bab2ae4b59b702aca7b3d037d";
    private const string SourceUniverseSha256 =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    [Fact]
    public void E1_v2_prompt_and_provider_bound_request_plan_are_frozen_offline()
    {
        var contract = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        var baselinePrompt = CanonicalSemanticEngine.SystemPromptFor(
            contract, CanonicalSemanticExperiment.Baseline);
        var e1Prompt = CanonicalSemanticEngine.SystemPromptFor(
            contract, CanonicalSemanticExperiment.NonStructuralMetadataConstrained);

        Assert.Equal(V2PromptSha256, CanonicalArtifactHash.OfText(baselinePrompt));
        Assert.Equal(E1ClauseSha256,
            CanonicalArtifactHash.OfText(CanonicalSemanticEngine.NonStructuralMetadataClause));
        Assert.Equal(E1V2PromptSha256, CanonicalArtifactHash.OfText(e1Prompt));
        Assert.True(
            e1Prompt.IndexOf(CanonicalSemanticEngine.NonStructuralMetadataClause, StringComparison.Ordinal)
            < e1Prompt.IndexOf(contract.PromptClause!, StringComparison.Ordinal));

        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(
            "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);

        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            contract,
            CanonicalSemanticExperiment.NonStructuralMetadataConstrained,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));
        var segments = model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment => segment.RequestBytes, StringComparer.Ordinal);

        Assert.Equal(TargetPacks, segments.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(Pack005UserMessageSha256,
            CanonicalSemanticRequestComposer.Hash(segments[TargetPacks[0]]));
        Assert.Equal(Pack006UserMessageSha256,
            CanonicalSemanticRequestComposer.Hash(segments[TargetPacks[1]]));

        var providerRequests = segments.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.Serialize(new { systemPrompt = e1Prompt, userMessage = pair.Value }),
            StringComparer.Ordinal);
        Assert.Equal(Pack005ProviderRequestSha256,
            SemanticAuthorityTransportCall.Sha256Utf8(providerRequests[TargetPacks[0]]));
        Assert.Equal(Pack006ProviderRequestSha256,
            SemanticAuthorityTransportCall.Sha256Utf8(providerRequests[TargetPacks[1]]));
        Assert.Equal(ProviderModelInputPlanSha256,
            CanonicalSemanticRequestComposer.Hash(string.Join(
                "\u0000", TargetPacks
                    .Select(pack => SemanticAuthorityTransportCall.Sha256Utf8(providerRequests[pack])))));
    }

    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException("PROVIDER_CALLS must remain 0.");
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage,
            CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("PROVIDER_CALLS must remain 0.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public void Dispose() { }
    }
}
