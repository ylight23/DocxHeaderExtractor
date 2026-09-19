using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfExperimentManifestTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldPack = "eval/a99-closed-loop/pdf-gold-doc0252";
    private const string OccurrenceGold =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.occurrence-gold.v1.json";
    private const string SemanticFreeze =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/DOC-0252.semantic-freeze.v1.json";
    private const string Preflight = "eval/a99-closed-loop/pdf-canary-072/preflight.v1.json";

    [Fact]
    public void The_frozen_manifest_is_deterministic_and_compared_as_an_artifact()
    {
        var first = BuildManifest();
        var second = BuildManifest();

        Assert.Equal(PdfExperimentManifestHasher.Serialize(first),
            PdfExperimentManifestHasher.Serialize(second));
        Assert.Equal(first.ManifestHash, second.ManifestHash);
        FreezeArtifact.AssertJson("eval/a99-closed-loop/pdf-canary-072", "experiment-manifest.v1.json",
            new { manifestHash = first.ManifestHash, manifest = first });
    }

    [Fact]
    public void Every_identity_bearing_change_creates_a_new_manifest_hash()
    {
        var manifest = BuildManifest();
        var hashes = new[]
        {
            (manifest with { SourceUniverseSha256 = "changed" }).ManifestHash,
            (manifest with { OccurrenceGold = manifest.OccurrenceGold with { ArtifactSha256 = "changed" } }).ManifestHash,
            (manifest with { Model = manifest.Model with { ModelIdentifier = "changed" } }).ManifestHash,
            (manifest with { Prompt = manifest.Prompt with { PromptSha256 = "changed" } }).ManifestHash,
            (manifest with { SourcePacket = manifest.SourcePacket with { PacketSha256 = "changed" } }).ManifestHash,
            (manifest with { Routing = manifest.Routing with { PlacementRetryEnabled = false } }).ManifestHash,
            (manifest with { Budget = new PdfExperimentBudgetIdentity(999) }).ManifestHash,
            (manifest with { Evaluator = manifest.Evaluator with { OccurrenceEvaluatorContractVersion = "changed" } }).ManifestHash,
        };

        Assert.All(hashes, hash => Assert.NotEqual(manifest.ManifestHash, hash));
        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Gate_rejects_each_manifest_runtime_mismatch_fail_closed()
    {
        var manifest = BuildManifest();
        var approval = Approval(manifest);
        var runtime = Runtime(manifest);
        var cases = new Dictionary<string, PdfExperimentRuntimeBinding>(StringComparer.Ordinal)
        {
            ["source"] = runtime with { SourceSha256 = "changed" },
            ["universe"] = runtime with { SourceUniverseSha256 = "changed" },
            ["occurrence-gold"] = runtime with { OccurrenceGoldSha256 = "changed" },
            ["prompt"] = runtime with { PromptSha256 = "changed" },
            ["packet"] = runtime with { SourcePacketSha256 = "changed" },
            ["model"] = runtime with { ModelIdentifier = "changed" },
            ["routing"] = runtime with
            {
                Routing = runtime.Routing with { PlacementRetryEnabled = false },
            },
            ["evaluator"] = runtime with { OccurrenceEvaluatorContractVersion = "changed" },
        };

        foreach (var pair in cases)
        {
            var gate = new PdfExperimentExecutionGate(manifest, approval, pair.Value);
            var error = Assert.Throws<InvalidOperationException>(gate.EnsureReady);
            Assert.Contains("MISMATCH", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Missing_approval_blocks_the_fake_transport()
    {
        var manifest = BuildManifest();
        var gate = new PdfExperimentExecutionGate(manifest, null, Runtime(manifest));
        using var fake = new RequestCapturingClassifier();
        using var guarded = new PdfExperimentGatedHeaderClassifier(fake, gate, disposeInner: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guarded.BoundaryCutAsync("system", "user"));

        Assert.Contains("APPROVAL_REQUIRED", error.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
        Assert.Equal(0, gate.ProviderCalls);
    }

    [Fact]
    public void Approval_for_another_manifest_cannot_authorize_this_manifest()
    {
        var manifest = BuildManifest();
        var other = manifest with { ExperimentId = "different-experiment" };
        var gate = new PdfExperimentExecutionGate(manifest, Approval(other), Runtime(manifest));

        var error = Assert.Throws<InvalidOperationException>(gate.EnsureReady);
        Assert.Contains("APPROVAL_MANIFEST_MISMATCH", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_approval_allows_the_fake_transport()
    {
        var manifest = BuildManifest();
        var gate = new PdfExperimentExecutionGate(manifest, Approval(manifest), Runtime(manifest));
        using var fake = new RequestCapturingClassifier();
        using var guarded = new PdfExperimentGatedHeaderClassifier(fake, gate, disposeInner: false);

        var response = await guarded.BoundaryCutAsync("system", "user");

        Assert.Contains("headings", response, StringComparison.Ordinal);
        Assert.Single(fake.Requests);
        Assert.Equal(1, gate.ProviderCalls);
    }

    [Fact]
    public async Task All_retry_and_recovery_calls_share_the_same_total_budget()
    {
        var manifest = BuildManifest() with
        {
            Budget = new PdfExperimentBudgetIdentity(2),
        };
        var gate = new PdfExperimentExecutionGate(manifest, Approval(manifest), Runtime(manifest));
        using var fake = new RequestCapturingClassifier();
        using var guarded = new PdfExperimentGatedHeaderClassifier(fake, gate, disposeInner: false);

        await guarded.BoundaryCutAsync("system", "initial");
        await guarded.BoundaryCutAsync("system", "placement-retry");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guarded.BoundaryCutAsync("system", "visual-recovery"));

        Assert.Contains("BUDGET_EXCEEDED", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, gate.ProviderCalls);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Offline_deterministic_lane_remains_unchanged_and_spends_no_provider_call()
    {
        var path = Path(Pdf);
        var document = await PdfCanonicalExtraction.RunAsync(
            UploadedFile.FromLocalPath(path), new PipelineOptions { DisableLlm = true });

        Assert.Equal("pdf-source-document", document.Provenance.SourceCatalogKind);
        Assert.Equal(0, document.Provenance.ProviderCalls);
    }

    [Fact]
    public void Manifest_keeps_DOC0252_gold_and_semantic_role_contract_bound()
    {
        var manifest = BuildManifest();
        var decisions = JsonDocument.Parse(Read(GoldPack + "/review-decisions.v1.json"))
            .RootElement.GetProperty("decisions").EnumerateArray().ToArray();
        var occurrence = JsonDocument.Parse(Read(OccurrenceGold)).RootElement;

        Assert.Equal("DOC-0252", manifest.DocumentId);
        Assert.Equal(1013, decisions.Length);
        Assert.Equal(38, decisions.Count(row => row.GetProperty("humanDecision").GetString() == "HEADING"));
        Assert.Equal(975, decisions.Count(row => row.GetProperty("humanDecision").GetString() == "NOT_HEADING"));
        Assert.Equal(41, occurrence.GetProperty("headings").GetArrayLength());
        Assert.Equal(41, manifest.OccurrenceGold.HeadingClaimCount);
        Assert.True(manifest.OccurrenceGold.OccurrenceEvaluable);
        Assert.Equal(41, manifest.SemanticAuthority.SemanticHeadingTotal);
        Assert.True(manifest.Evaluator.SemanticRoleEvaluationEnabled);
        Assert.Equal(0, occurrence.GetProperty("providerCalls").GetInt32());
    }

    private static PdfExperimentManifest BuildManifest()
    {
        using var preflight = JsonDocument.Parse(Read(Preflight));
        var request = preflight.RootElement.GetProperty("request");
        var packet = new
        {
            schemaVersion = "a99-pdf-canonical-request-packet-v1",
            protocol = preflight.RootElement.GetProperty("contract").GetProperty("protocol").GetString(),
            ownedPerSegment = 120,
            visibleMargin = 20,
            systemPromptSha256 = request.GetProperty("systemPromptSha256").GetString(),
            userPayloadSha256 = request.GetProperty("userPayloadSha256").EnumerateArray()
                .Select(item => item.GetString()).ToArray(),
        };
        var packetHash = PdfExperimentManifestHasher.HashCanonicalJson(JsonSerializer.Serialize(packet));

        return new PdfExperimentManifest(
            "a99-pdf-experiment-manifest-v1",
            "DOC-0252-PDF-B0",
            "DOC-0252",
            "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61",
            "5dd617b27d7c1479f81fb41468076b5f6ba826a7d86cc9a04f6dfa0fa624c3ec",
            new PdfExperimentGoldIdentity(
                OccurrenceGold,
                CanonicalArtifactHash.OfTextFile(Path(OccurrenceGold)), 41, true),
            new PdfExperimentSemanticAuthorityIdentity(
                SemanticFreeze,
                CanonicalArtifactHash.OfTextFile(Path(SemanticFreeze)), 41),
            new PdfExperimentModelIdentity("OpenRouter", "qwen/qwen3.7-flash", "openai-chat-completions-v1"),
            new PdfExperimentPromptIdentity("canonical-semantic-shared-b0-v1",
                request.GetProperty("systemPromptSha256").GetString()!),
            new PdfExperimentPacketIdentity("a99-pdf-canonical-request-packet-v1", packetHash),
            new PdfExperimentRoutingIdentity(
                ["primary-semantic-proposal", "unresolved-placement-retry"],
                "unresolved-placement-only-v1",
                false,
                false,
                false,
                true),
            new PdfExperimentBudgetIdentity(10),
            new PdfExperimentEvaluatorIdentity(
                "a99-pdf-gold-evaluator-v2-semantic-role",
                true,
                "NOT_ADJUDICATED"));
    }

    private static PdfExperimentRuntimeBinding Runtime(PdfExperimentManifest manifest) =>
        new(
            manifest.DocumentId,
            manifest.SourceSha256,
            manifest.SourceUniverseSha256,
            manifest.OccurrenceGold.ArtifactSha256,
            manifest.OccurrenceGold.HeadingClaimCount,
            manifest.OccurrenceGold.OccurrenceEvaluable,
            manifest.SemanticAuthority.ArtifactSha256,
            manifest.SemanticAuthority.SemanticHeadingTotal,
            manifest.Prompt.PromptSha256,
            manifest.SourcePacket.PacketSha256,
            manifest.Model.ProviderIdentifier,
            manifest.Model.ModelIdentifier,
            manifest.Model.TransportProtocol,
            manifest.Routing,
            manifest.Evaluator.OccurrenceEvaluatorContractVersion,
            manifest.Evaluator.SemanticRoleEvaluationEnabled);

    private static PdfExperimentApproval Approval(PdfExperimentManifest manifest) =>
        new(manifest.ManifestHash, "approval-DOC-0252-B0", "explicit-human-approval");

    private static string Read(string relativePath) => File.ReadAllText(Path(relativePath));

    private static string Path(string relativePath) =>
        System.IO.Path.Combine(RepositoryRoot(), relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
