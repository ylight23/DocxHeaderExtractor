using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfS2gSuccessorAuthorityTests
{
    private const string Root =
        "eval/a99-closed-loop/semantic-text-replay-successor-v1-runtime-authority/DOC-0252";
    private const string OriginalRoot =
        "eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252";
    private const string SourceHash =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseHash =
        "cb3c9af67a7f17fd9560b56cf23bb9648a9fcfe3ea4eb5d333ac7282176bfc66";
    private const string AliasCatalogHash =
        "867a2dd5cccf46984a1b282e7a69916dbececb1f3a35da2e5b1e7f6379774305";
    private const string PromptHash =
        "8b056f1722b356dd9e836908b8d05ad0850353a06fbc0a4aa39db568fd47f0a8";
    private const string SemanticContractHash =
        "21687e5a78d59b8c82124dcc567c7353024145d599b1294c41c888ffe4512365";
    private const string OriginalManifestHash =
        "7c48d54c94e4ab60f022a2e5104b44eefb5e4639ac9aea31e8842abeda183cfb";
    private const string SuccessorManifestHash =
        "2ac4513d81d63fcaab23cf26d0fbacf4bd5ac741770eeb0f4c4ce9c0fcbd3474";

    [Fact]
    public void Successor_authority_is_new_zero_provider_adoption_manifest()
    {
        using var document = JsonDocument.Parse(Read("experiment-manifest.v1.json"));
        var root = document.RootElement;
        var manifest = root.GetProperty("manifest");

        Assert.Equal(SuccessorManifestHash, root.GetProperty("manifestHash").GetString());
        Assert.Equal(SuccessorManifestHash,
            PdfExperimentManifestHasher.HashCanonicalJson(manifest.GetRawText()));
        Assert.Equal(SemanticContractHash, root.GetProperty("semanticContractHash").GetString());
        Assert.Equal("DOC-0252-PDF-S2E-RUNTIME-B0-ADOPTION-V1",
            manifest.GetProperty("experimentId").GetString());
        Assert.Equal(SourceHash, manifest.GetProperty("sourceSha256").GetString());
        Assert.Equal(SourceUniverseHash, manifest.GetProperty("sourceUniverseSha256").GetString());
        Assert.Equal(PromptHash, manifest.GetProperty("prompt").GetProperty("promptSha256").GetString());
        Assert.Equal(0, manifest.GetProperty("budget").GetProperty("maximumProviderCalls").GetInt32());

        var adoption = manifest.GetProperty("adoption");
        Assert.Equal(0, adoption.GetProperty("providerCallsAllowedForAdoption").GetInt32());
        Assert.Equal(0, adoption.GetProperty("modelCallsAllowedForAdoption").GetInt32());
        Assert.Equal(OriginalManifestHash, adoption.GetProperty("originalManifestHash").GetString());
        Assert.False(adoption.GetProperty("transportOriginallyApprovedUnderSuccessorContract").GetBoolean());
        Assert.Equal(29, adoption.GetProperty("originalProviderCallLedger").GetProperty("total").GetInt32());
    }

    [Fact]
    public void Successor_census_keeps_original_bundle_manifest_provenance_and_matches_live_authority()
    {
        using var census = JsonDocument.Parse(Read("bundle-census.v1.json"));
        var root = census.RootElement;
        Assert.True(root.GetProperty("all3BundlesMatchSuccessorAuthority").GetBoolean());
        Assert.True(root.GetProperty("manifestHashInBundleIsOriginalProvenance").GetBoolean());

        var bundles = root.GetProperty("bundles").EnumerateArray().ToArray();
        Assert.Equal(3, bundles.Length);
        Assert.Equal(OriginalManifestHash, root.GetProperty("originalManifestHash").GetString());
        Assert.All(bundles, bundle =>
        {
            Assert.Equal(OriginalManifestHash, bundle.GetProperty("manifestHash").GetString());
            Assert.Equal(SourceHash, bundle.GetProperty("sourceHash").GetString());
            Assert.Equal(SourceUniverseHash, bundle.GetProperty("sourceUniverseHash").GetString());
            Assert.Equal(AliasCatalogHash, bundle.GetProperty("aliasCatalogHash").GetString());
            Assert.Equal(PromptHash, bundle.GetProperty("promptHash").GetString());
            Assert.Equal(SemanticContractHash, bundle.GetProperty("semanticContractHash").GetString());
            Assert.Equal("qwen/qwen3.7-flash", bundle.GetProperty("modelIdentity").GetString());
            Assert.Equal("openai-chat-completions-v1", bundle.GetProperty("modelRoute").GetString());
            Assert.True(bundle.GetProperty("successorAuthorityFieldsMatch").GetBoolean());
        });
    }

    [Fact]
    public void Every_captured_bundle_is_immutable_self_consistent_and_replayable_without_transport()
    {
        using var census = JsonDocument.Parse(Read("bundle-census.v1.json"));
        var censusBundles = census.RootElement.GetProperty("bundles").EnumerateArray().ToArray();
        Assert.Equal(3, censusBundles.Length);

        foreach (var censusBundle in censusBundles)
        {
            var bundlePath = RepositoryPath(censusBundle.GetProperty("path").GetString()!);
            var bundle = JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(
                File.ReadAllText(bundlePath))!;

            Assert.Equal(censusBundle.GetProperty("bundleHash").GetString(), bundle.BundleHash);
            Assert.Equal(censusBundle.GetProperty("proposalHash").GetString(), bundle.ProposalHash);
            Assert.Equal(OriginalManifestHash, bundle.ManifestHash);
            Assert.Equal(AliasCatalogHash, bundle.AliasCatalogHash);
            Assert.Equal(SourceHash, bundle.SourceHash);
            Assert.Equal(SourceUniverseHash, bundle.SourceUniverseHash);
            Assert.Equal(PromptHash, bundle.PromptHash);
            Assert.Equal(SemanticContractHash, bundle.SemanticContractHash);

            Assert.Empty(SemanticAuthorityReplay.Validate(bundle, SourceHash, SourceUniverseHash));

            var proposalHashBeforeReplay = SemanticAuthorityReplayHashing.ProposalHash(bundle.Proposals);
            var first = SemanticAuthorityReplay.Replay(bundle, SourceHash, SourceUniverseHash);
            var proposalHashDuringReplay = SemanticAuthorityReplayHashing.ProposalHash(bundle.Proposals);
            var second = SemanticAuthorityReplay.Replay(bundle, SourceHash, SourceUniverseHash);

            Assert.Equal(bundle.ProposalHash, proposalHashBeforeReplay);
            Assert.Equal(proposalHashBeforeReplay, proposalHashDuringReplay);
            Assert.Equal(
                JsonSerializer.Serialize(first.MaterializedProjection),
                JsonSerializer.Serialize(second.MaterializedProjection));
        }
    }

    [Fact]
    public void Adoption_preflight_is_zero_provider_and_does_not_score()
    {
        using var preflight = JsonDocument.Parse(Read("adoption-preflight.v1.json"));
        var root = preflight.RootElement;
        var adoption = root.GetProperty("successorAdoption");

        Assert.Equal(0, adoption.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, adoption.GetProperty("modelCalls").GetInt32());
        Assert.Equal(3, adoption.GetProperty("bundleCount").GetInt32());
        Assert.True(adoption.GetProperty("all3BundlesMatchSuccessorAuthority").GetBoolean());
        Assert.False(adoption.GetProperty("proposalMutation").GetBoolean());
        Assert.False(adoption.GetProperty("sourceRebindingBeforeReplay").GetBoolean());
        Assert.False(adoption.GetProperty("scoringPerformed").GetBoolean());
        Assert.True(root.GetProperty("checks").GetProperty("scoringInputsComplete").GetBoolean());
    }

    [Fact]
    public void Evidence_ledger_keeps_S2C_S2E_and_successor_adoption_separate()
    {
        using var ledger = JsonDocument.Parse(Read("evidence-ledger.v1.json"));
        var entries = ledger.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        Assert.Equal(3, entries.Length);
        Assert.Equal("ABORTED_AUTHORITY_MISMATCH", entries[0].GetProperty("status").GetString());
        Assert.Equal(9, entries[0].GetProperty("providerCalls").GetInt32());
        Assert.Equal("TRANSPORT_COMPLETE_AUTHORITY_MISMATCH", entries[1].GetProperty("status").GetString());
        Assert.Equal(29, entries[1].GetProperty("providerCalls").GetInt32());
        Assert.Equal(3, entries[1].GetProperty("publishedReplayBundles").GetInt32());
        Assert.Equal(0, entries[2].GetProperty("providerCalls").GetInt32());
        Assert.False(entries[2].GetProperty("scored").GetBoolean());
    }

    [Fact]
    public void Original_S2E_manifest_and_bundles_remain_the_historical_execution_authority()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            RepositoryPath(OriginalRoot + "/experiment-manifest.v1.json")));
        Assert.Equal(OriginalManifestHash, manifest.RootElement.GetProperty("manifestHash").GetString());
        Assert.Equal("abc8bb1f767e7556f121ed4a1f708ed60bd97502ca420e399ce7af26711e6e53",
            manifest.RootElement.GetProperty("semanticContractHash").GetString());
    }

    private static string Read(string relativePath) => File.ReadAllText(RepositoryPath(Root + "/" + relativePath));

    private static string RepositoryPath(string relativePath) =>
        System.IO.Path.Combine(RepositoryRoot(), relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
