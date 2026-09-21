using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfS2dSourceUniverseAuthorityTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string SourceUniverse = "eval/a99-closed-loop/pdf-gold-doc0252/source-universe.v1.json";
    private const string Gold =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.occurrence-gold.v1.json";
    private const string Reconciliation = "eval/a99-closed-loop/pdf-canary-072/source-universe-reconciliation.v1.json";
    private const string OldHash =
        "5dd617b27d7c1479f81fb41468076b5f6ba826a7d86cc9a04f6dfa0fa624c3ec";
    private const string RuntimeHash =
        "cb3c9af67a7f17fd9560b56cf23bb9648a9fcfe3ea4eb5d333ac7282176bfc66";
    private const string AliasCatalogHash =
        "867a2dd5cccf46984a1b282e7a69916dbececb1f3a35da2e5b1e7f6379774305";

    [Fact]
    public async Task Reconciled_preflight_live_runtime_and_replay_capture_share_one_hash()
    {
        using var report = JsonDocument.Parse(Read(Reconciliation));
        var reconciledHash = report.RootElement
            .GetProperty("reconciledPreflightAuthority")
            .GetProperty("sourceUniverseSha256").GetString();

        var builds = Enumerable.Range(0, 3)
            .Select(_ => PdfCanonicalSourceUniverseBuilder.Build(Path(Pdf)))
            .ToArray();

        Assert.All(builds, universe => Assert.Equal(RuntimeHash, universe.SourceUniverseSha256));
        Assert.Single(builds.Select(universe => universe.SourceUniverseSha256).Distinct());
        Assert.Equal(RuntimeHash, reconciledHash);

        var universe = builds[0];
        var input = universe.CreateProductionInput("DOC-0252") with
        {
            ReplayCapture = new SemanticAuthorityCaptureMetadata(
                "PDF", universe.SourceUniverseSha256, "offline-s2d-model", "offline-s2d", "frozen-prompt",
                ManifestHash: "s2d-reconciliation-only")
        };
        var result = await CanonicalSemanticProductionEntryPoint.RunAsync(
            input, new EmptySemanticModel());

        var bundle = Assert.IsType<SemanticAuthorityReplayBundle>(result.ReplayBundle);
        Assert.Equal(RuntimeHash, bundle.SourceUniverseHash);
        Assert.Equal(AliasCatalogHash, bundle.AliasCatalogHash);
        Assert.Equal(RuntimeHash, reconciledHash);
    }

    [Fact]
    public void Approved_source_rows_and_gold_reconcile_to_the_live_runtime_universe()
    {
        var universe = PdfCanonicalSourceUniverseBuilder.Build(Path(Pdf));
        using var old = JsonDocument.Parse(Read(SourceUniverse));
        var oldRows = old.RootElement.GetProperty("rows").EnumerateArray().ToArray();

        Assert.Equal(1013, oldRows.Length);
        Assert.Equal(1013, universe.Aliases.Count);
        foreach (var (row, alias) in oldRows.Zip(universe.Aliases))
        {
            Assert.Equal(row.GetProperty("sourceAlias").GetString(), alias.Alias);
            Assert.Equal(row.GetProperty("sourceId").GetString(), alias.SourceId);
            Assert.Equal(row.GetProperty("ordinal").GetInt32(), alias.SourceOrdinal);
            Assert.Equal(row.GetProperty("verbatimText").GetString(), alias.Text);
            Assert.Equal(row.GetProperty("page").GetInt32(), alias.SourceAnchor?.Page);
            Assert.Equal(row.GetProperty("characters").GetInt32(), alias.Text.Length);
        }

        var authority = PdfGoldOccurrenceAuthorityLoader.LoadFromFiles(
            Path("eval/a99-closed-loop/pdf-gold-doc0252/review-decisions.v1.json"),
            Path(SourceUniverse));
        var gold = PdfGoldOccurrenceMaterializer.Freeze(authority);
        Assert.Equal(41, gold.Headings.Count);
        Assert.Empty(PdfGoldValidator.Validate(gold, universe.Catalog, universe.Aliases));
        Assert.All(gold.Headings, heading =>
            Assert.Contains(universe.Aliases, alias => alias.Alias == heading.SourceAlias));
    }

    [Fact]
    public void The_old_manifest_identity_is_preserved_and_is_not_the_reconciled_runtime_identity()
    {
        using var report = JsonDocument.Parse(Read(Reconciliation));
        using var manifest = JsonDocument.Parse(Read("eval/a99-closed-loop/pdf-canary-072/experiment-manifest.v1.json"));

        Assert.Equal(OldHash, manifest.RootElement.GetProperty("manifest")
            .GetProperty("sourceUniverseSha256").GetString());
        Assert.Equal(OldHash, report.RootElement.GetProperty("oldApprovedAuthority")
            .GetProperty("sourceUniverseSha256").GetString());
        Assert.Equal(RuntimeHash, report.RootElement.GetProperty("liveRuntimeAuthority")
            .GetProperty("sourceUniverseSha256").GetString());
        Assert.NotEqual(OldHash, RuntimeHash);
        Assert.False(report.RootElement.GetProperty("reconciledPreflightAuthority")
            .GetProperty("existingApprovedManifestMutated").GetBoolean());
        Assert.Equal("MIXED_AUTHORITY_DRIFT", report.RootElement.GetProperty("classification").GetString());
        Assert.Equal(9, report.RootElement.GetProperty("s2cAttempt")
            .GetProperty("providerCallsConsumed").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("modelCalls").GetInt32());
    }

    [Fact]
    public async Task Stale_manifest_authority_is_rejected_before_live_provider_transport()
    {
        using var manifestDocument = JsonDocument.Parse(
            Read("eval/a99-closed-loop/pdf-canary-072/experiment-manifest.v1.json"));
        var manifest = JsonSerializer.Deserialize<PdfExperimentManifest>(
            manifestDocument.RootElement.GetProperty("manifest").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var gate = new PdfExperimentExecutionGate(
            manifest,
            new PdfExperimentApproval(manifest.ManifestHash, "s2d-test", "offline-test"),
            Runtime(manifest));
        using var fake = new RequestCapturingClassifier();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PdfCanonicalExtraction.RunExecutionAsync(
                UploadedFile.FromLocalPath(Path(Pdf)),
                new PipelineOptions { ExperimentGate = gate },
                fake,
                analystSendsDataExternally: true,
                ct: CancellationToken.None));

        Assert.Equal("PDF_EXPERIMENT_LIVE_SOURCE_UNIVERSE_SHA_MISMATCH", error.Message);
        Assert.Empty(fake.Requests);
        Assert.Equal(0, gate.ProviderCalls);
    }

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

    private sealed class EmptySemanticModel : ICanonicalSemanticTextModel
    {
        public Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticProductionInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CanonicalSemanticTextInferenceResult(
                [], new CanonicalSemanticInferenceTelemetry("offline-s2d", "stop"))
            {
                ParsedProposals = [],
                RawModelResponseHash = "offline-s2d-raw-response"
            });
    }
}
