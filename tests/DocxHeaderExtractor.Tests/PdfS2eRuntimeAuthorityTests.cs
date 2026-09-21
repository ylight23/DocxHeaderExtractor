using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfS2eRuntimeAuthorityTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Gold =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.occurrence-gold.v1.json";
    private const string SemanticFreeze =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/DOC-0252.semantic-freeze.v1.json";
    private const string Root =
        "eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252";
    private const string ExperimentId = "DOC-0252-PDF-S2E-RUNTIME-B0";
    private const string ManifestHash =
        "7c48d54c94e4ab60f022a2e5104b44eefb5e4639ac9aea31e8842abeda183cfb";
    private const string RuntimeSourceUniverseHash =
        "cb3c9af67a7f17fd9560b56cf23bb9648a9fcfe3ea4eb5d333ac7282176bfc66";
    private const string PromptHash =
        "8b056f1722b356dd9e836908b8d05ad0850353a06fbc0a4aa39db568fd47f0a8";
    private const string SemanticContractHash =
        "abc8bb1f767e7556f121ed4a1f708ed60bd97502ca420e399ce7af26711e6e53";
    private const string GoldHash =
        "2ebbc6b8af28dec38514d0b53aa36599a00ca86e87af050bfb3008b98bef6d31";
    private const string SemanticAuthorityHash =
        "abf7da69b0ddfd1663165798a8c0064229e0c2c623ad1a2cc9c09d7550e77624";
    private const string SourceHash =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";

    [Fact]
    public async Task Runtime_derived_authority_is_frozen_and_manifest_round_trips_exactly()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("A99_S2E_GENERATE"), "1", StringComparison.Ordinal))
        {
            await GenerateAsync();
            return;
        }

        using var manifestDocument = JsonDocument.Parse(Read("experiment-manifest.v1.json"));
        var root = manifestDocument.RootElement;
        var manifest = JsonSerializer.Deserialize<PdfExperimentManifest>(
            root.GetProperty("manifest").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(ManifestHash, root.GetProperty("manifestHash").GetString());
        Assert.Equal(ManifestHash, manifest.ManifestHash);
        Assert.Equal(manifest.ManifestHash, PdfExperimentManifestHasher.Compute(manifest));
        Assert.Equal(RuntimeSourceUniverseHash, manifest.SourceUniverseSha256);
        Assert.Equal(PromptHash, manifest.Prompt.PromptSha256);
        Assert.Equal(SemanticContractHash, root.GetProperty("semanticContractHash").GetString());
        Assert.Equal(41, manifest.OccurrenceGold.HeadingClaimCount);
        Assert.True(manifest.OccurrenceGold.OccurrenceEvaluable);
        Assert.Equal(10, manifest.Budget.MaximumProviderCalls);

        var runtime = PdfCanonicalSourceUniverseBuilder.Build(Path(Pdf));
        Assert.Equal(RuntimeSourceUniverseHash, runtime.SourceUniverseSha256);
        Assert.Equal(SourceHash, runtime.SourceSha256);
        Assert.Equal(1013, runtime.Aliases.Count);

        var gate = new PdfExperimentExecutionGate(
            manifest,
            new PdfExperimentApproval(ManifestHash, "s2e-offline-preflight", "offline-authority-check"),
            Runtime(manifest));

        using var preflight = JsonDocument.Parse(Read("preflight.v1.json"));
        var preflightHash = preflight.RootElement.GetProperty("authority")
            .GetProperty("sourceUniverseSha256").GetString();
        Assert.Equal(preflightHash, manifest.SourceUniverseSha256);
        Assert.Equal(preflightHash, runtime.SourceUniverseSha256);
        Assert.Equal(preflightHash, preflight.RootElement.GetProperty("replayCapture")
            .GetProperty("sourceUniverseSha256").GetString());
        Assert.Equal("867a2dd5cccf46984a1b282e7a69916dbececb1f3a35da2e5b1e7f6379774305",
            preflight.RootElement.GetProperty("authority").GetProperty("aliasCatalogHash").GetString());
        Assert.Equal(0, preflight.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, preflight.RootElement.GetProperty("modelCalls").GetInt32());

        gate.EnsureLiveSourceUniverse(runtime.SourceUniverseSha256);
    }

    [Fact]
    public void Fresh_authority_keeps_gold_and_aborted_attempts_separate()
    {
        using var manifest = JsonDocument.Parse(Read("experiment-manifest.v1.json"));
        using var source = JsonDocument.Parse(Read("source-universe-runtime.v1.json"));
        using var ledger = JsonDocument.Parse(Read("aborted-attempt.v1.json"));
        using var replay = JsonDocument.Parse(Read("replay-capture-contract.v1.json"));

        Assert.Equal(GoldHash, manifest.RootElement.GetProperty("manifest")
            .GetProperty("occurrenceGold").GetProperty("artifactSha256").GetString());
        Assert.Equal(41, manifest.RootElement.GetProperty("manifest")
            .GetProperty("occurrenceGold").GetProperty("headingClaimCount").GetInt32());
        Assert.Equal(RuntimeSourceUniverseHash, source.RootElement
            .GetProperty("sourceUniverseSha256").GetString());
        Assert.Equal(1013, source.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal("ABORTED_AUTHORITY_MISMATCH", ledger.RootElement
            .GetProperty("status").GetString());
        Assert.Equal(9, ledger.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, ledger.RootElement.GetProperty("publishedReplayBundles").GetInt32());
        Assert.Equal(0, ledger.RootElement.GetProperty("usableBaselineRepeats").GetInt32());
        Assert.True(replay.RootElement.GetProperty("requireReplayBundle").GetBoolean());
        Assert.Equal(Root.Replace('\\', '/'), replay.RootElement
            .GetProperty("artifactRoot").GetString());

        var runtime = PdfCanonicalSourceUniverseBuilder.Build(Path(Pdf));
        var authority = PdfGoldOccurrenceAuthorityLoader.LoadFromFiles(
            Path("eval/a99-closed-loop/pdf-gold-doc0252/review-decisions.v1.json"),
            Path("eval/a99-closed-loop/pdf-gold-doc0252/source-universe.v1.json"));
        var gold = PdfGoldOccurrenceMaterializer.Freeze(authority);
        Assert.Equal(41, gold.Headings.Count);
        Assert.Empty(PdfGoldValidator.Validate(gold, runtime.Catalog, runtime.Aliases));
    }

    [Fact]
    public void New_authority_recomputes_the_current_call_plan_and_does_not_reuse_old_manifest()
    {
        using var preflight = JsonDocument.Parse(Read("preflight.v1.json"));
        using var oldManifest = JsonDocument.Parse(File.ReadAllText(
            Path("eval/a99-closed-loop/pdf-canary-072/experiment-manifest.v1.json")));
        var plan = preflight.RootElement.GetProperty("callPlan");

        Assert.Equal(3, plan.GetProperty("repeatCount").GetInt32());
        Assert.Equal(9, plan.GetProperty("primarySemanticCallsPerRepeat").GetInt32());
        Assert.Equal(1, plan.GetProperty("placementAllowancePerRepeat").GetInt32());
        Assert.Equal(10, plan.GetProperty("maximumCallsPerRepeat").GetInt32());
        Assert.Equal(30, plan.GetProperty("totalProviderCallsPlanned").GetInt32());
        Assert.Equal("DOC-0252-PDF-S2E-RUNTIME-B0", preflight.RootElement
            .GetProperty("experimentId").GetString());
        Assert.NotEqual(
            oldManifest.RootElement.GetProperty("manifestHash").GetString(),
            JsonDocument.Parse(Read("experiment-manifest.v1.json"))
                .RootElement.GetProperty("manifestHash").GetString());
        Assert.Equal("COST_UNKNOWN", preflight.RootElement.GetProperty("cost")
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task New_authority_replay_capture_uses_the_same_runtime_universe_without_transport()
    {
        using var manifestDocument = JsonDocument.Parse(Read("experiment-manifest.v1.json"));
        var manifest = JsonSerializer.Deserialize<PdfExperimentManifest>(
            manifestDocument.RootElement.GetProperty("manifest").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var universe = PdfCanonicalSourceUniverseBuilder.Build(Path(Pdf));
        var input = universe.CreateProductionInput("DOC-0252") with
        {
            ReplayCapture = new SemanticAuthorityCaptureMetadata(
                "PDF", universe.SourceUniverseSha256, manifest.Model.ModelIdentifier,
                manifest.Model.TransportProtocol, manifest.Prompt.PromptSha256,
                Gold, GoldHash, manifest.Evaluator.OccurrenceEvaluatorContractVersion,
                ManifestHash, "s2e-offline-replay")
        };

        var result = await CanonicalSemanticProductionEntryPoint.RunAsync(
            input, new EmptySemanticModel());
        var bundle = Assert.IsType<SemanticAuthorityReplayBundle>(result.ReplayBundle);

        Assert.Equal(manifest.SourceUniverseSha256, bundle.SourceUniverseHash);
        Assert.Equal(RuntimeSourceUniverseHash, bundle.SourceUniverseHash);
        Assert.Equal("867a2dd5cccf46984a1b282e7a69916dbececb1f3a35da2e5b1e7f6379774305",
            bundle.AliasCatalogHash);
        Assert.Empty(bundle.Proposals);
    }

    [Fact]
    public void Provider_postflight_blocks_scoring_when_replay_contract_identity_differs()
    {
        using var audit = JsonDocument.Parse(Read("postflight-audit.v1.json"));
        var root = audit.RootElement;

        Assert.Equal("PROVIDER_RUN_COMPLETE_BASELINE_BLOCKED", root.GetProperty("status").GetString());
        Assert.Equal(29, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(3, root.GetProperty("replayBundles").GetInt32());
        Assert.Equal("PASS", root.GetProperty("replayBundleReloadValidation").GetString());
        Assert.False(root.GetProperty("semanticContractHashMatch").GetBoolean());
        Assert.False(root.GetProperty("baselineUsableForScoring").GetBoolean());
        Assert.NotEqual(
            root.GetProperty("manifestSemanticContractHash").GetString(),
            root.GetProperty("replaySemanticContractHash").GetString());
    }

    [Fact]
    public async Task Stale_semantic_contract_authority_blocks_before_provider_transport()
    {
        using var manifestDocument = JsonDocument.Parse(Read("experiment-manifest.v1.json"));
        var parsed = JsonSerializer.Deserialize<PdfExperimentManifest>(
            manifestDocument.RootElement.GetProperty("manifest").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var stale = parsed with { SemanticContractHash = "stale-semantic-contract" };
        var gate = new PdfExperimentExecutionGate(
            stale,
            new PdfExperimentApproval(stale.ManifestHash, "s2f-stale-contract", "offline-test"),
            Runtime(parsed));
        using var fake = new RequestCapturingClassifier();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                Path(Pdf), fake, CancellationToken.None, experimentGate: gate));

        Assert.Equal("PDF_EXPERIMENT_SEMANTIC_CONTRACT_HASH_MISMATCH", error.Message);
        Assert.Equal(0, gate.ProviderCalls);
        Assert.Empty(fake.Requests);
    }

    private static async Task GenerateAsync()
    {
        var path = Path(Pdf);
        var builds = Enumerable.Range(0, 3)
            .Select(_ => PdfCanonicalSourceUniverseBuilder.Build(path))
            .ToArray();
        Assert.All(builds, universe => Assert.Equal(RuntimeSourceUniverseHash, universe.SourceUniverseSha256));
        Assert.Single(builds.Select(universe => universe.SourceUniverseSha256).Distinct());

        using var firstCapture = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(path, firstCapture, CancellationToken.None);
        using var secondCapture = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(path, secondCapture, CancellationToken.None);
        Assert.Equal(
            firstCapture.Requests.Select(request => Sha256(request.UserMessage)),
            secondCapture.Requests.Select(request => Sha256(request.UserMessage)));
        Assert.Equal(9, firstCapture.Requests.Count);

        var universe = builds[0];
        var sourceFile = UploadedFile.FromLocalPath(path);
        Assert.Equal(SourceHash, sourceFile.Sha256);
        var requestHashes = firstCapture.Requests.Select(request => Sha256(request.UserMessage)).ToArray();
        var systemPromptHash = Sha256(firstCapture.Requests[0].SystemPrompt);
        Assert.Equal(PromptHash, systemPromptHash);
        var packet = new
        {
            schemaVersion = "a99-pdf-canonical-request-packet-v1",
            protocol = CanonicalSemanticContract.ProtocolVersion,
            ownedPerSegment = 120,
            visibleMargin = 20,
            systemPromptSha256 = systemPromptHash,
            userPayloadSha256 = requestHashes,
        };
        var packetHash = PdfExperimentManifestHasher.HashCanonicalJson(JsonSerializer.Serialize(packet));
        var manifest = new PdfExperimentManifest(
            "a99-pdf-experiment-manifest-v1",
            ExperimentId,
            "DOC-0252",
            SourceHash,
            universe.SourceUniverseSha256,
            new PdfExperimentGoldIdentity(Gold, GoldHash, 41, true),
            new PdfExperimentSemanticAuthorityIdentity(SemanticFreeze, SemanticAuthorityHash, 41),
            new PdfExperimentModelIdentity("OpenRouter", "qwen/qwen3.7-flash", "openai-chat-completions-v1"),
            new PdfExperimentPromptIdentity("canonical-semantic-shared-b0-v1", systemPromptHash),
            new PdfExperimentPacketIdentity("a99-pdf-canonical-request-packet-v1", packetHash),
            new PdfExperimentRoutingIdentity(
                ["primary-semantic-proposal", "unresolved-placement-retry"],
                "unresolved-placement-only-v1", false, false, false, true),
            new PdfExperimentBudgetIdentity(10),
            new PdfExperimentEvaluatorIdentity(
                "a99-pdf-gold-evaluator-v2-semantic-role", true, "NOT_ADJUDICATED"));

        var root = Path(Root);
        Directory.CreateDirectory(root);
        WriteJson("experiment-manifest.v1.json", new
        {
            manifestHash = manifest.ManifestHash,
            semanticContractHash = SemanticContractHash,
            manifest,
        });

        var packets = firstCapture.Requests.Select(ParsePacket).ToArray();
        var owned = packets.SelectMany(packetInfo => packetInfo.Owned).ToArray();
        var visible = packets.SelectMany(packetInfo => packetInfo.Visible).ToArray();
        var maxChars = firstCapture.Requests.Max(request => request.SystemPrompt.Length + request.UserMessage.Length);
        var maxBytes = firstCapture.Requests.Max(request =>
            Encoding.UTF8.GetByteCount(request.SystemPrompt + "\n" + request.UserMessage));
        var maxTokens = (int)Math.Ceiling(maxChars / 4d);
        var estimatedInputTokens = firstCapture.Requests.Select(request =>
            (int)Math.Ceiling((request.SystemPrompt.Length + request.UserMessage.Length) / 4d)).ToArray();
        WriteJson("preflight.v1.json", new
        {
            artifactKind = "A99_S2E_RUNTIME_DERIVED_PREFLIGHT",
            schemaVersion = "a99-pdf-runtime-derived-preflight-v1",
            experimentId = ExperimentId,
            documentId = "DOC-0252",
            providerCalls = 0,
            modelCalls = 0,
            source = new { file = Pdf, sourceSha256 = SourceHash, parserLines = universe.ParserLineCount, aliases = universe.Aliases.Count },
            authority = new
            {
                sourceSha256 = SourceHash,
                sourceUniverseSha256 = universe.SourceUniverseSha256,
                aliasCatalogHash = SemanticAuthorityReplayHashing.AliasCatalogHash(universe.Aliases),
                semanticContractHash = SemanticContractHash,
                promptSha256 = systemPromptHash,
                goldSha256 = GoldHash,
                goldHeadingClaims = 41,
                goldCompatible = true,
            },
            manifest = new { path = Root.Replace('\\', '/') + "/experiment-manifest.v1.json", hash = manifest.ManifestHash },
            replayCapture = new
            {
                sourceUniverseSha256 = universe.SourceUniverseSha256,
                requireReplayBundle = true,
                artifactRoot = Root.Replace('\\', '/'),
                oneBundlePerDocumentRepeat = true,
            },
            segmentation = new
            {
                ownedPerSegment = 120,
                visibleMargin = 20,
                segments = firstCapture.Requests.Count,
                ownedTotal = owned.Length,
                ownedDistinct = owned.Distinct(StringComparer.Ordinal).Count(),
                visibleTotal = visible.Length,
            },
            request = new
            {
                systemPromptSha256 = systemPromptHash,
                userPayloadSha256 = requestHashes,
                maxRequestBytes = maxBytes,
                estimatedMaximumInputTokens = maxTokens,
                estimatedInputTokens,
                estimatedTotalInputTokens = estimatedInputTokens.Sum(),
                requestBytes = firstCapture.Requests.Select(request =>
                    Encoding.UTF8.GetByteCount(request.SystemPrompt + "\n" + request.UserMessage)).ToArray(),
                expectedItemCount = firstCapture.Requests.Select(request => request.ExpectedItemCount).ToArray(),
            },
            callPlan = new
            {
                repeatCount = 3,
                primarySemanticCallsPerRepeat = firstCapture.Requests.Count,
                placementAllowancePerRepeat = 1,
                maximumCallsPerRepeat = firstCapture.Requests.Count + 1,
                totalProviderCallsPlanned = 3 * (firstCapture.Requests.Count + 1),
            },
            cost = new
            {
                status = "COST_UNKNOWN",
                pricing = (object?)null,
                note = "No authoritative model price was used or fetched during offline preflight.",
                estimatedMaximumInputTokensPerPrimaryRequest = maxTokens,
            },
            previousAttempt = new
            {
                status = "ABORTED_AUTHORITY_MISMATCH",
                providerCalls = 9,
                publishedReplayBundles = 0,
                authorityHash = "5dd617b27d7c1479f81fb41468076b5f6ba826a7d86cc9a04f6dfa0fa624c3ec",
                manifestHash = "fb62c1c696b4c30f0b71aed2e39f296ece3934c5870982fdc0e19bc62d64189c",
            },
        });

        var runtimeRows = universe.Aliases.Select(alias => new
        {
            sourceAlias = alias.Alias,
            sourceId = alias.SourceId,
            ordinal = alias.SourceOrdinal,
            text = alias.Text,
            sourceStart = alias.SourceSpan.Start,
            sourceEnd = alias.SourceSpan.End,
            page = alias.SourceAnchor?.Page,
        }).ToArray();
        WriteJson("source-universe-runtime.v1.json", new
        {
            artifactKind = "a99_pdf_runtime_source_universe",
            schemaVersion = "a99-pdf-runtime-source-universe-v1",
            documentId = "DOC-0252",
            sourceSha256 = SourceHash,
            sourceUniverseSha256 = universe.SourceUniverseSha256,
            aliasCatalogHash = SemanticAuthorityReplayHashing.AliasCatalogHash(universe.Aliases),
            parserLineCount = universe.ParserLineCount,
            rows = runtimeRows,
        });
        WriteJson("aborted-attempt.v1.json", new
        {
            artifactKind = "a99_pdf_experiment_aborted_attempt",
            schemaVersion = "a99-pdf-aborted-attempt-v1",
            experimentId = "DOC-0252-PDF-B0",
            status = "ABORTED_AUTHORITY_MISMATCH",
            providerCalls = 9,
            publishedReplayBundles = 0,
            r2Executed = false,
            r3Executed = false,
            usableBaselineRepeats = 0,
            authorityHash = "5dd617b27d7c1479f81fb41468076b5f6ba826a7d86cc9a04f6dfa0fa624c3ec",
            manifestHash = "fb62c1c696b4c30f0b71aed2e39f296ece3934c5870982fdc0e19bc62d64189c",
            note = "These calls are excluded from all new repeat statistics and scoring.",
        });
        WriteJson("replay-capture-contract.v1.json", new
        {
            artifactKind = "a99_pdf_replay_capture_contract",
            schemaVersion = "a99-pdf-replay-capture-contract-v1",
            experimentId = ExperimentId,
            artifactRoot = Root.Replace('\\', '/'),
            requireReplayBundle = true,
            perRepeat = "provider response -> pure parse -> CanonicalSemanticProposal[] -> atomic replay bundle -> reload validation -> source-aware validation",
            naming = "DOC-0252/r{repeat}/semantic-authority-replay-{repeat}.json; distinct from the aborted S2C root",
        });
        for (var index = 0; index < firstCapture.Requests.Count; index++)
            WriteText($"request-{index}.json", firstCapture.Requests[index].UserMessage);
        WriteText("system-prompt.txt", firstCapture.Requests[0].SystemPrompt);
    }

    private static PacketInfo ParsePacket(CapturedRequest request)
    {
        var schemaAt = request.UserMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(schemaAt < 0 ? request.UserMessage : request.UserMessage[..schemaAt]);
        var root = document.RootElement;
        var owned = root.GetProperty("ownedSourceAliases").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        var visible = root.GetProperty("sourceEvidence").EnumerateArray()
            .Select(item => item.GetProperty("alias").GetString()!).ToArray();
        return new PacketInfo(owned, visible);
    }

    private sealed record PacketInfo(string[] Owned, string[] Visible);

    private static PdfExperimentRuntimeBinding Runtime(PdfExperimentManifest manifest) =>
        new(
            manifest.DocumentId, manifest.SourceSha256, manifest.SourceUniverseSha256,
            manifest.OccurrenceGold.ArtifactSha256, manifest.OccurrenceGold.HeadingClaimCount,
            manifest.OccurrenceGold.OccurrenceEvaluable, manifest.SemanticAuthority.ArtifactSha256,
            manifest.SemanticAuthority.SemanticHeadingTotal, manifest.Prompt.PromptSha256,
            manifest.SourcePacket.PacketSha256, manifest.Model.ProviderIdentifier,
            manifest.Model.ModelIdentifier, manifest.Model.TransportProtocol, manifest.Routing,
            manifest.Evaluator.OccurrenceEvaluatorContractVersion,
            manifest.Evaluator.SemanticRoleEvaluationEnabled);

    private static void WriteJson(string relativePath, object value)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path(Root + "/" + relativePath), JsonSerializer.Serialize(value, options), new UTF8Encoding(false));
    }

    private static void WriteText(string relativePath, string value) =>
        File.WriteAllText(Path(Root + "/" + relativePath), value, new UTF8Encoding(false));

    private sealed class EmptySemanticModel : ICanonicalSemanticTextModel
    {
        public Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticProductionInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CanonicalSemanticTextInferenceResult(
                [], new CanonicalSemanticInferenceTelemetry("offline-s2e", "stop"))
            {
                ParsedProposals = [],
                RawModelResponseHash = "offline-s2e-raw-response",
            });
    }

    private static string Read(string relativePath) => File.ReadAllText(Path(Root + "/" + relativePath));

    private static string Path(string relativePath) =>
        System.IO.Path.Combine(TestRepository.Root(), relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

}
