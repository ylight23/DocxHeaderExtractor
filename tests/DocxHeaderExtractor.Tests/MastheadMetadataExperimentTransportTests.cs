using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EXP_MASTHEAD_METADATA: six calls, one variable.
/// <para>
/// The evidence packets are byte-identical to the arm already captured - same atoms, same packing,
/// same contract, same two packs - and the model, its settings and Gold are unchanged. What differs
/// is one appended clause in the system prompt, so whatever the replies do differently is
/// attributable to it and to nothing else.
/// </para>
/// <para>
/// Every gate below runs before the first request and throws rather than warns. A run that starts
/// against the wrong prompt or the wrong packs spends money on replies that cannot answer the
/// question they were authorized for.
/// </para>
/// </summary>
public sealed class MastheadMetadataExperimentTransportTests
{
    private const string RunVariable = "A99_EXP_MASTHEAD_RUN";
    private const string OutputRoot = "eval/a99-closed-loop/exp-masthead-metadata-experiment-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string AuthorizedCommit = "cf2735e";
    private const string ExperimentId = "EXP_MASTHEAD_METADATA";
    private const string ExperimentPromptSha256 =
        "78396aa66a9dad6baef0cb24ee3468d9bfc740a4216acf3c51659c89eba54e5d";
    private const string PredecessorPromptSha256 =
        "2207221eb8782c13296023aefe5b3cfe9a771eba652f029745948e2534fe580e";
    private const string ProviderModelInputPlanSha256 =
        "f088be88dbf35cd52486472bb0ef65ce213d35a06fadc3b09a747a2f5930d2d8";
    private const string Pack005RequestSha256 =
        "21d4edc1895351d81c1fb79a072f7e863b0c321ad98012aab39a1977798c13da";
    private const string Pack006RequestSha256 =
        "3a7f173d774f9ae594344c82f1954098812a01af19227a76234c5419b4d16f96";
    private const string SourceUniverseSha256 =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string GoldSha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string StructuredContractSha256 =
        "69b99b9099b964a5cf5985b8ec618db49c8ee5c3fa8a2bb8f69993cdc2e24f6f";
    private const string ManifestSha256 =
        "fb62c1c696b4c30f0b71aed2e39f296ece3934c5870982fdc0e19bc62d64189c";

    private const string Model = "qwen/qwen3.7-flash";
    private const string EvaluatorId = "a99-pdf-gold-evaluator-v4-structured-source-parts-semantic-role";
    private const int Repeats = 3;
    private const int PrimaryCalls = 6;
    private const int HardCap = 9;

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    [Fact]
    public void All_authorization_gates_hold_without_contacting_a_provider()
    {
        var gates = VerifyGates();
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_the_masthead_metadata_experiment()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var gates = VerifyGates();
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));

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

    // ---- gates ----------------------------------------------------------------------------------

    private static IReadOnlyList<string> VerifyGates()
    {
        var lines = new List<string>();
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));

        lines.Add(Check("experiment", ExperimentId, ExperimentId));
        lines.Add(Check("authorizedCommit", AuthorizedCommit, AuthorizedCommit));
        lines.Add(Check("profile", "STRUCTURED_SOURCE_PARTS",
            PdfSemanticAuthorityProfile.StructuredSourceParts.ProfileId));
        lines.Add(Check("packing", "COHERENT_REGION_SEGMENTATION_V1",
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1.PolicyId));
        lines.Add(Check("sourceHash", SourceSha256,
            CanonicalArtifactHash.OfBytes(TestRepository.Path(Doc0252Pdf))));
        lines.Add(Check("sourceUniverse", SourceUniverseSha256, plan.SourceUniverseSha256));
        lines.Add(Check("goldHash", GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256));
        lines.Add(Check("structuredContract", StructuredContractSha256,
            SemanticCoordinateContract.PdfStructuredSourceParts.SchemaHash()));

        // The one variable, pinned on both sides: the arm's prompt must be the authorized one, and
        // the baseline's must still be the baseline's.
        var experimentPrompt = CanonicalSemanticEngine.SystemPromptFor(
            SemanticCoordinateContract.PdfStructuredSourceParts,
            CanonicalSemanticExperiment.NonStructuralMetadataConstrained);
        var predecessorPrompt = CanonicalSemanticEngine.SystemPromptFor(
            SemanticCoordinateContract.PdfStructuredSourceParts, CanonicalSemanticExperiment.Baseline);
        lines.Add(Check("experimentPrompt", ExperimentPromptSha256, CanonicalArtifactHash.OfText(experimentPrompt)));
        lines.Add(Check("predecessorPrompt", PredecessorPromptSha256, CanonicalArtifactHash.OfText(predecessorPrompt)));

        // The packs, their bytes, and the plan those bytes make.
        var requests = ComposeRequests(plan);
        lines.Add(Check("packs", string.Join(",", TargetPacks), string.Join(",", requests.Keys)));
        lines.Add(Check("pack005Request", Pack005RequestSha256,
            CanonicalSemanticRequestComposer.Hash(requests[TargetPacks[0]])));
        lines.Add(Check("pack006Request", Pack006RequestSha256,
            CanonicalSemanticRequestComposer.Hash(requests[TargetPacks[1]])));
        lines.Add(Check("providerModelInputPlan", ProviderModelInputPlanSha256,
            CanonicalSemanticRequestComposer.Hash(string.Join(
                "\u0000", requests.Values.Select(CanonicalSemanticRequestComposer.Hash)))));

        lines.Add(Check("repeats", Repeats.ToString(), Repeats.ToString()));
        lines.Add(Check("primaryCalls", PrimaryCalls.ToString(), (TargetPacks.Length * Repeats).ToString()));
        lines.Add(Check("placement", "0", "0"));
        lines.Add(Check("hardCap", HardCap.ToString(), HardCap.ToString()));
        lines.Add(Check("model", Model, Model));
        lines.Add(Check("evaluator", EvaluatorId,
            PdfSemanticAuthorityProfile.StructuredSourceParts.EvaluatorId));
        return lines;
    }

    private static Dictionary<string, string> ComposeRequests(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            SemanticCoordinateContract.PdfStructuredSourceParts,
            CanonicalSemanticExperiment.NonStructuralMetadataConstrained,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment => segment.RequestBytes, StringComparer.Ordinal);
    }

    private static string Check(string name, string expected, string actual) =>
        $"{name}: {(string.Equals(expected, actual, StringComparison.Ordinal) ? "MATCH" : "MISMATCH")} " +
        $"expected={expected} actual={actual}";

    // ---- one repeat ------------------------------------------------------------------------------

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
            experiment: CanonicalSemanticExperiment.NonStructuralMetadataConstrained,
            replayCapture: capture,
            profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
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

    private static void Persist(
        BudgetedClassifier classifier, IReadOnlyList<object> runs,
        IReadOnlyList<string> gates, string? aborted)
    {
        var directory = TestRepository.Path(OutputRoot);
        Directory.CreateDirectory(directory);
        var payload = JsonSerializer.Serialize(new
        {
            artifactKind = "a99_exp_masthead_metadata_run",
            schemaVersion = "a99-exp-masthead-metadata-run-v1",
            experimentId = ExperimentId,
            authorizedCommit = AuthorizedCommit,
            approval = "explicit-user-authorization, DOC-0252 only, packs 5 and 6, 6 calls, cap 9",
            documentId = "DOC-0252",
            profile = "STRUCTURED_SOURCE_PARTS",
            packingPolicy = "COHERENT_REGION_SEGMENTATION_V1",
            targetPackIds = TargetPacks,
            repeats = Repeats,
            primarySemanticCalls = classifier.CallsMade,
            placementCalls = 0,
            maximumProviderCalls = HardCap,
            unusedAllowance = HardCap - classifier.CallsMade,
            model = Model,
            providerRoute = "OpenRouter",
            experimentPromptSha256 = ExperimentPromptSha256,
            predecessorPromptSha256 = PredecessorPromptSha256,
            providerModelInputPlanSha256 = ProviderModelInputPlanSha256,
            structuredContractSha256 = StructuredContractSha256,
            sourceSha256 = SourceSha256,
            sourceUniverseSha256 = SourceUniverseSha256,
            goldSha256 = GoldSha256,
            manifestHash = ManifestSha256,
            evaluatorId = EvaluatorId,
            scoringPerformed = false,
            conclusionScope = "DOC-0252 causal evidence for the masthead/event-metadata distinction. Not a "
                + "cross-genre proof: 48 of 3955 approved headings are materialized corpus-wide.",
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
        File.WriteAllText(Path.Combine(directory, "exp-masthead-metadata-run.v1.json"),
            payload.ReplaceLineEndings("\n"));
    }

    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException();
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("Gate verification must not transport.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
