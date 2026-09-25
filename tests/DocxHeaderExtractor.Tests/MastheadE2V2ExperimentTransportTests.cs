using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EXP_MASTHEAD_METADATA_E2_V2: one invariant, under the frozen structured-v2 input.
/// <para>
/// E1 named a category and suppressed nothing - all three masthead families survived all three
/// repeats, with no membership, role or relation change attributable to it. E2 does not add more
/// categories. It states that heading eligibility is decided before placement, and that
/// parent-node:NONE cannot be the reason a span is admitted.
/// </para>
/// <para>
/// The gates below check two different byte authorities and keep them apart. The evidence packet is
/// the user message and is identical in every arm, which is what proves packing did not move. The
/// provider input is the system prompt and the user message together, and it must differ per arm.
/// Checking only the first would let this arm run against the wrong prompt and still pass.
/// </para>
/// </summary>
public sealed class MastheadE2V2ExperimentTransportTests
{
    private const string RunVariable = "A99_EXP_MASTHEAD_E2_RUN";
    private const string OutputRoot =
        "eval/a99-closed-loop/exp-masthead-e2-v2-experiment-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string AuthorizedCommit = "065a0c6";
    private const string ExperimentId = "EXP_MASTHEAD_METADATA_E2_V2";
    private const string E2ClauseSha256 =
        "1cfbeaf20fd024cf50b446ac911e274c6970c9244cb85ad34fb8eb534b9fc734";
    private const string ExperimentPromptSha256 =
        "93cead4cb4e8d3dee5018b789a1d95a2807b69bfa81ac5f85726de3aba0682ed";
    private const string Pack005ProviderRequestSha256 =
        "af9edf0563601bd7281a0dfd4c05e31ee2bc46c7c23672a090ffbcf9302b258b";
    private const string Pack006ProviderRequestSha256 =
        "93292c853c1e65da4820cbde6095adcacf5bc29c8fab6f2b5085a4a72438abb9";
    private const string ProviderModelInputPlanSha256 =
        "349724f59801c696d77ad394e36e2880d2cb5590375c1ef11aa9debbe66626be";

    // The predecessors this arm must not be confused with on the wire.
    private const string E1ProviderModelInputPlanSha256 =
        "d788652788771cc6998fd5d94d531d07e939599bab2ae4b59b702aca7b3d037d";
    private const string BaselineProviderModelInputPlanSha256 =
        "c0348ca82bf7cbccc1d7cd3222951754989752410a5260724941f4b3ffc0f45e";
    private const string Pack005UserMessageSha256 =
        "8f3b430a78608bb10d62fb3655d2742b236470b5f228967f6fd8b328896d8c16";
    private const string Pack006UserMessageSha256 =
        "6d867a0d836ba0fbbe5f041fc046fc932fe2fea7f23f100f7f0a59301048cff0";
    private const string V2ContractSha256 =
        "565bdc87749a1ce1246238cacd1eb7d550a19939dc9e42d23e76ab3a46a8b0ea";
    private const string SourceUniverseSha256 =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string GoldSha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string ManifestSha256 =
        "fb62c1c696b4c30f0b71aed2e39f296ece3934c5870982fdc0e19bc62d64189c";
    private const string Model = "qwen/qwen3.7-flash";
    private const string EvaluatorId =
        "a99-pdf-gold-evaluator-v4-structured-source-parts-semantic-role";
    private const int Repeats = 3;
    private const int PrimaryCalls = 6;
    private const int HardCap = 9;
    private const int TargetGoldCount = 14;

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    internal static readonly PdfSemanticAuthorityProfile V2Profile = new(
        "STRUCTURED_SOURCE_PARTS_V2",
        "STRUCTURED_SOURCE_PART_TUPLE",
        SemanticCoordinateContract.PdfStructuredSourcePartsV2,
        EvaluatorId,
        StructuredSourcePartsEvaluable: true,
        SourceAuthorityId: PdfSemanticAuthorityProfile.StructuredAtomSourceAuthority);

    [Fact]
    public void All_authorization_gates_hold_without_contacting_a_provider()
    {
        var gates = VerifyGates();
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_the_approved_masthead_metadata_v2_experiment()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var gates = VerifyGates();
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));

        // Six fresh capture identities, checked here rather than in the always-on gate test: this
        // is the last moment before money is spent, and it is the only moment at which occupied
        // slots mean something is about to be overwritten.
        var occupied = Enumerable.Range(1, Repeats)
            .Select(repeat => Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}"))
            .Count(directory => Directory.Exists(directory) && Directory.GetFiles(directory).Length > 0);
        Assert.Equal(0, occupied);

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey,
            Model = Model,
        });
        using var budgeted = new BudgetedClassifier(provider, HardCap);

        var runs = new List<object>();
        var aborted = (string?)null;
        for (var repeat = 1; repeat <= Repeats && aborted is null; repeat++)
        {
            budgeted.DocumentId = "DOC-0252";
            budgeted.Repeat = repeat;
            budgeted.Stage = "semantic";
            try
            {
                runs.Add(await RunOnceAsync(repeat, budgeted));
            }
            catch (Exception error)
            {
                aborted = $"r{repeat}: {error.GetType().Name}: {error.Message}";
            }
        }

        Persist(budgeted, runs, gates, aborted);
        Assert.Null(aborted);
        Assert.Equal(PrimaryCalls, budgeted.CallsMade);
    }

    private static IReadOnlyList<string> VerifyGates()
    {
        var lines = new List<string>();
        var path = TestRepository.Path(Doc0252Pdf);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(path);
        var contract = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        var experimentPrompt = CanonicalSemanticEngine.SystemPromptFor(
            contract, HistoricalRequest.Of(CanonicalSemanticExperiment.MembershipBeforePlacement));

        lines.Add(Check("experiment", ExperimentId, ExperimentId));
        lines.Add(Check("authorizedCommit", AuthorizedCommit, AuthorizedCommit));
        lines.Add(Check("protocol", "a99-semantic-source-parts-v2", contract.ProtocolVersion));
        lines.Add(Check("profile", "STRUCTURED_SOURCE_PARTS_V2", V2Profile.ProfileId));
        lines.Add(Check("packing", "COHERENT_REGION_SEGMENTATION_V1",
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1.PolicyId));
        lines.Add(Check("packs", string.Join(",", TargetPacks), string.Join(",", TargetPacks)));
        lines.Add(Check("repeats", Repeats.ToString(), Repeats.ToString()));
        lines.Add(Check("primaryCalls", PrimaryCalls.ToString(), (TargetPacks.Length * Repeats).ToString()));
        lines.Add(Check("placement", "0", "0"));
        lines.Add(Check("hardCap", HardCap.ToString(), HardCap.ToString()));
        lines.Add(Check("goldTargetCount", TargetGoldCount.ToString(), TargetGoldCount.ToString()));
        lines.Add(Check("selectionModeModelVisible", "false", "false"));
        lines.Add(Check("selectionModeHarnessDerived", "true", "true"));
        lines.Add(Check("sourceHash", SourceSha256, CanonicalArtifactHash.OfBytes(path)));
        lines.Add(Check("sourceUniverse", SourceUniverseSha256, plan.SourceUniverseSha256));
        lines.Add(Check("goldHash", GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256));
        lines.Add(Check("v2Contract", V2ContractSha256, contract.SchemaHash()));
        lines.Add(Check("evaluator", EvaluatorId, V2Profile.EvaluatorId));
        lines.Add(Check("e2Clause", E2ClauseSha256,
            CanonicalArtifactHash.OfText(CanonicalSemanticEngine.MembershipBeforePlacementClause)));
        // E2 is not E1 plus something: the two arms are never combined.
        lines.Add(Check("e1ClauseAbsent", "true",
            experimentPrompt.Contains(CanonicalSemanticEngine.NonStructuralMetadataClause,
                StringComparison.Ordinal) ? "false" : "true"));
        lines.Add(Check("experimentPrompt", ExperimentPromptSha256,
            CanonicalArtifactHash.OfText(experimentPrompt)));

        var baselinePrompt = CanonicalSemanticEngine.SystemPromptFor(
            contract, HistoricalRequest.Of(CanonicalSemanticExperiment.Baseline));
        lines.Add(Check("e2BeforeV2Contract", "true",
            experimentPrompt.IndexOf(CanonicalSemanticEngine.MembershipBeforePlacementClause,
                StringComparison.Ordinal) < experimentPrompt.IndexOf(contract.PromptClause!, StringComparison.Ordinal)
                ? "true" : "false"));
        lines.Add(Check("v2BaselineUnchanged", "true",
            CanonicalArtifactHash.OfText(baselinePrompt) ==
            "6340d1daf507a3d2bf5ce6fbef2b5d7b61735c0e0621a62f8e885ad8b2d8a66e"
                ? "true" : "false"));

        var segments = ComposeRequests(plan);
        lines.Add(Check("pack005UserMessage", Pack005UserMessageSha256,
            CanonicalSemanticRequestComposer.Hash(segments[TargetPacks[0]].Request)));
        lines.Add(Check("pack006UserMessage", Pack006UserMessageSha256,
            CanonicalSemanticRequestComposer.Hash(segments[TargetPacks[1]].Request)));
        var providerRequests = segments.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.Serialize(new { systemPrompt = experimentPrompt, userMessage = pair.Value.Request }),
            StringComparer.Ordinal);
        lines.Add(Check("pack005ProviderRequest", Pack005ProviderRequestSha256,
            SemanticAuthorityTransportCall.Sha256Utf8(providerRequests[TargetPacks[0]])));
        lines.Add(Check("pack006ProviderRequest", Pack006ProviderRequestSha256,
            SemanticAuthorityTransportCall.Sha256Utf8(providerRequests[TargetPacks[1]])));
        lines.Add(Check("providerModelInputPlan", ProviderModelInputPlanSha256,
            CanonicalSemanticRequestComposer.Hash(string.Join("\u0000", TargetPacks.Select(pack =>
                SemanticAuthorityTransportCall.Sha256Utf8(providerRequests[pack]))))));

        using var gold = CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256);
        var identities = gold.RootElement.GetProperty("occurrence").GetProperty("claims")
            .EnumerateArray().Select(claim => claim.GetProperty("identity").GetString()!).ToArray();
        var targetGold = identities.Count(identity =>
            segments.Values.Any(segment => segment.Owned.Contains(FirstAlias(identity))));
        lines.Add(Check("goldClaims", "41", identities.Length.ToString()));
        lines.Add(Check("goldTargetCountDerived", TargetGoldCount.ToString(), targetGold.ToString()));
        // The evidence packets are identical in every arm, so only the provider input can tell
        // this run apart from its predecessors. Assert that, rather than assuming it.
        var planHash = CanonicalSemanticRequestComposer.Hash(string.Join("\u0000", TargetPacks.Select(pack =>
            SemanticAuthorityTransportCall.Sha256Utf8(providerRequests[pack]))));
        lines.Add(Check("providerInputDiffersFromE1", "true",
            planHash == E1ProviderModelInputPlanSha256 ? "false" : "true"));
        lines.Add(Check("providerInputDiffersFromBaseline", "true",
            planHash == BaselineProviderModelInputPlanSha256 ? "false" : "true"));

        lines.Add(Check("model", Model, Model));
        lines.Add(Check("manifest", ManifestSha256, ManifestSha256));
        return lines;
    }

    private static Dictionary<string, Segment> ComposeRequests(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            SemanticCoordinateContract.PdfStructuredSourcePartsV2,
            HistoricalRequest.Of(CanonicalSemanticExperiment.MembershipBeforePlacement),
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment => new Segment(
                segment.RequestBytes,
                segment.Owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal)),
                StringComparer.Ordinal);
    }

    private static async Task<object> RunOnceAsync(int repeat, BudgetedClassifier classifier)
    {
        var path = TestRepository.Path(Doc0252Pdf);
        var before = classifier.CallsMade;
        var capture = new SemanticAuthorityReplayCaptureRequest(
            new SemanticAuthorityCaptureMetadata(
                "PDF", SourceUniverseSha256, Model, "OpenRouter", ExperimentPromptSha256,
                GoldId: "DOC-0252", GoldHash: GoldSha256, EvaluatorIdentity: EvaluatorId,
                ManifestHash: ManifestSha256, RunId: $"{ExperimentId}-r{repeat}",
                CreatedAt: DateTimeOffset.UtcNow, RepeatIdentity: $"r{repeat}"),
            Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}"));

        var authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
            path, classifier, CancellationToken.None,
            experiment: HistoricalRequest.Of(CanonicalSemanticExperiment.MembershipBeforePlacement),
            replayCapture: capture,
            profile: V2Profile,
            packingPolicy: SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            selectedPackIds: TargetPacks.ToHashSet(StringComparer.Ordinal),
            runPlacement: false);

        var bundle = authority.ReplayBundle;
        return new
        {
            repeat,
            providerCalls = classifier.CallsMade - before,
            elements = authority.Structure.Elements.Count,
            bundleHash = bundle?.BundleHash,
            proposalHash = bundle?.ProposalHash,
            proposals = bundle?.Proposals.Count ?? 0,
            rawModelResponseHash = bundle?.RawModelResponseHash,
            goldHash = bundle?.GoldHash,
        };
    }

    private static void Persist(BudgetedClassifier classifier, IReadOnlyList<object> runs,
        IReadOnlyList<string> gates, string? aborted)
    {
        var directory = TestRepository.Path(OutputRoot);
        Directory.CreateDirectory(directory);
        var payload = JsonSerializer.Serialize(new
        {
            artifactKind = "a99_exp_masthead_e2_v2_run",
            schemaVersion = "a99-exp-masthead-e2-v2-run-v1",
            experimentId = ExperimentId,
            authorizedCommit = AuthorizedCommit,
            approval = "explicit-user-authorization, DOC-0252 only, packs 5 and 6, 6 calls, cap 9",
            documentId = "DOC-0252",
            protocolVersion = SemanticCoordinateContract.PdfStructuredSourcePartsV2.ProtocolVersion,
            profile = V2Profile.ProfileId,
            packingPolicy = "COHERENT_REGION_SEGMENTATION_V1",
            targetPackIds = TargetPacks,
            repeats = Repeats,
            primarySemanticCalls = classifier.CallsMade,
            placementCalls = 0,
            maximumProviderCalls = HardCap,
            unusedAllowance = HardCap - classifier.CallsMade,
            model = Model,
            providerRoute = "OpenRouter",
            e2ClauseSha256 = E2ClauseSha256,
            experimentPromptSha256 = ExperimentPromptSha256,
            providerModelInputPlanSha256 = ProviderModelInputPlanSha256,
            pack005ProviderRequestSha256 = Pack005ProviderRequestSha256,
            pack006ProviderRequestSha256 = Pack006ProviderRequestSha256,
            v2ContractSha256 = V2ContractSha256,
            sourceSha256 = SourceSha256,
            sourceUniverseSha256 = SourceUniverseSha256,
            goldSha256 = GoldSha256,
            goldTargetCount = TargetGoldCount,
            manifestHash = ManifestSha256,
            evaluatorId = EvaluatorId,
            selectionModeModelVisible = false,
            selectionModeHarnessDerived = true,
            placementDisabled = true,
            semanticIntervention = "E2 membership-before-placement clause only",
            hypothesis = "MEMBERSHIP_BEFORE_PLACEMENT",
            comparators = new
            {
                baselineProviderModelInputPlanSha256 = BaselineProviderModelInputPlanSha256,
                e1ProviderModelInputPlanSha256 = E1ProviderModelInputPlanSha256,
                provenanceNote = "The historical v2 baseline artifact records 8d360359 under the name "
                    + "providerModelInputPlanSha256, but that value is its evidence-packet plan. It is left "
                    + "as recorded; the baseline's true provider-input plan is c0348ca8, and that is the "
                    + "comparator lineage.",
            },
            historicalUnusedCapacity = "Baseline and E1 each ended with 3 unused calls. Neither transfers "
                + "here, and the margin between 6 and the cap of 9 is not permission for another repeat, "
                + "rerun or variant.",
            scoringPerformed = false,
            aborted,
            gates,
            runs,
            callLedger = classifier.Ledger.Select(call => new
            {
                call.Ordinal, call.DocumentId, call.Repeat, call.Stage,
                call.SystemPromptSha256, call.RequestSha256, call.ResponseSha256,
                call.RequestChars, call.ResponseChars, call.ElapsedMs,
            }).ToArray(),
        }, FreezeArtifact.Json);
        File.WriteAllText(Path.Combine(directory, "exp-masthead-e2-v2-run.v1.json"),
            payload.ReplaceLineEndings("\n"));
    }

    private static string FirstAlias(string identity)
    {
        var first = identity.Split('|')[0];
        return first[..first.LastIndexOf(':')];
    }

    private static string Check(string name, string expected, string actual) =>
        $"{name}: {(string.Equals(expected, actual, StringComparison.Ordinal) ? "MATCH" : "MISMATCH")} " +
        $"expected={expected} actual={actual}";

    internal sealed record Segment(string Request, HashSet<string> Owned);

    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException("Gate verification must not contact a provider.");
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage,
            CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("Gate verification must not contact a provider.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
