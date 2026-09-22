using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// STRUCTURED_V2_TARGET_BASELINE: six calls, no semantic variable.
/// <para>
/// v2 changed the bytes the provider sees, so the v1 baseline cannot serve as a comparator for any
/// v2 arm. This run establishes the comparator: the same two packs, the same atoms, the same Gold
/// and the same model, with the unmodified v2 prompt and no intervention clause. A later arm then
/// differs from it by exactly one clause.
/// </para>
/// <para>
/// It is also the first time a provider answers under a schema that does not ask for a selection
/// mode. That the harness can re-derive the mode for 42/42 approved parts is proven offline; how a
/// model writes replies when it is no longer asked for one is not.
/// </para>
/// <para>
/// DOC-0252's declared authority is unchanged and still names the v1 profile. The v2 profile here is
/// experiment-scoped routing, passed explicitly: promoting the document's declaration is a separate
/// decision that would need the evidence this run exists to collect.
/// </para>
/// </summary>
public sealed class StructuredV2TargetBaselineTransportTests
{
    private const string RunVariable = "A99_V2_BASELINE_RUN";
    private const string OutputRoot = "eval/a99-closed-loop/structured-v2-target-baseline-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string AuthorizedCommit = "b62a412";
    private const string BaselineId = "STRUCTURED_V2_TARGET_BASELINE";

    private const string V2PromptSha256 = "6340d1daf507a3d2bf5ce6fbef2b5d7b61735c0e0621a62f8e885ad8b2d8a66e";
    private const string V2ContractSha256 = "565bdc87749a1ce1246238cacd1eb7d550a19939dc9e42d23e76ab3a46a8b0ea";
    private const string Pack005RequestSha256 =
        "8f3b430a78608bb10d62fb3655d2742b236470b5f228967f6fd8b328896d8c16";
    private const string Pack006RequestSha256 =
        "6d867a0d836ba0fbbe5f041fc046fc932fe2fea7f23f100f7f0a59301048cff0";
    private const string ProviderModelInputPlanSha256 =
        "8d3603598358acaf0fae53624202f0658bbf6f699aff891e8b263360919d13f2";
    private const string SourceUniverseSha256 =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string GoldSha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string ManifestSha256 =
        "fb62c1c696b4c30f0b71aed2e39f296ece3934c5870982fdc0e19bc62d64189c";

    private const string Model = "qwen/qwen3.7-flash";
    private const string EvaluatorId = "a99-pdf-gold-evaluator-v4-structured-source-parts-semantic-role";
    private const int Repeats = 3;
    private const int PrimaryCalls = 6;
    private const int HardCap = 9;
    private const int TargetGoldCount = 14;

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    /// <summary>
    /// The v2 coordinate authority, scoped to this experiment. Same coordinate system, same
    /// evaluator, same Gold - only the reply encoding differs, which is the whole point.
    /// </summary>
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
    public async Task Run_the_structured_v2_target_baseline()
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
        var v2 = SemanticCoordinateContract.PdfStructuredSourcePartsV2;

        lines.Add(Check("baseline", BaselineId, BaselineId));
        lines.Add(Check("authorizedCommit", AuthorizedCommit, AuthorizedCommit));
        lines.Add(Check("protocol", "a99-semantic-source-parts-v2", v2.ProtocolVersion));
        lines.Add(Check("profile", "STRUCTURED_SOURCE_PARTS_V2", V2Profile.ProfileId));
        lines.Add(Check("packing", "COHERENT_REGION_SEGMENTATION_V1",
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1.PolicyId));
        lines.Add(Check("sourceHash", SourceSha256,
            CanonicalArtifactHash.OfBytes(TestRepository.Path(Doc0252Pdf))));
        lines.Add(Check("sourceUniverse", SourceUniverseSha256, plan.SourceUniverseSha256));
        lines.Add(Check("goldHash", GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256));
        lines.Add(Check("v2Contract", V2ContractSha256, v2.SchemaHash()));
        lines.Add(Check("evaluator", EvaluatorId, V2Profile.EvaluatorId));

        // The prompt is the unmodified v2 baseline: no intervention clause of any kind.
        var prompt = CanonicalSemanticEngine.SystemPromptFor(v2, CanonicalSemanticExperiment.Baseline);
        lines.Add(Check("v2Prompt", V2PromptSha256, CanonicalArtifactHash.OfText(prompt)));
        lines.Add(Check("mastheadClause", "absent",
            prompt.Contains(CanonicalSemanticEngine.NonStructuralMetadataClause, StringComparison.Ordinal)
                ? "present" : "absent"));
        lines.Add(Check("interventionClause", "absent",
            string.Equals(prompt, CanonicalSemanticEngine.SystemPrompt + v2.PromptClause, StringComparison.Ordinal)
                ? "absent" : "present"));

        // selectionMode: not offered to the model anywhere, and owned by the harness.
        lines.Add(Check("selectionModeModelVisible", "false",
            JsonSerializer.Serialize(v2.SchemaFactory()).Contains("selectionMode", StringComparison.Ordinal)
                || prompt.Contains("selectionMode", StringComparison.Ordinal) ? "true" : "false"));
        lines.Add(Check("selectionModeHarnessDerived", "true",
            SemanticSourcePartCanonicalizer.PendingSelectionMode == "HARNESS_DERIVED" ? "true" : "false"));

        // The packs, their bytes, and the plan those bytes make.
        var segments = ComposeRequests(plan);
        lines.Add(Check("packs", string.Join(",", TargetPacks), string.Join(",", segments.Keys)));
        lines.Add(Check("pack005Request", Pack005RequestSha256,
            CanonicalSemanticRequestComposer.Hash(segments[TargetPacks[0]].Request)));
        lines.Add(Check("pack006Request", Pack006RequestSha256,
            CanonicalSemanticRequestComposer.Hash(segments[TargetPacks[1]].Request)));
        lines.Add(Check("providerModelInputPlan", ProviderModelInputPlanSha256,
            CanonicalSemanticRequestComposer.Hash(string.Join(
                "\u0000", segments.Values.Select(item => CanonicalSemanticRequestComposer.Hash(item.Request))))));

        // The Gold population these packs own, derived rather than trusted.
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        var identities = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("identity").GetString()!).ToArray();
        var targetGold = identities.Count(identity =>
            segments.Values.Any(segment => segment.Owned.Contains(FirstAlias(identity))));
        lines.Add(Check("goldClaims", "41", identities.Length.ToString()));
        lines.Add(Check("goldTargetCount", TargetGoldCount.ToString(), targetGold.ToString()));

        lines.Add(Check("repeats", Repeats.ToString(), Repeats.ToString()));
        lines.Add(Check("primaryCalls", PrimaryCalls.ToString(), (TargetPacks.Length * Repeats).ToString()));
        lines.Add(Check("placement", "0", "0"));
        lines.Add(Check("hardCap", HardCap.ToString(), HardCap.ToString()));
        lines.Add(Check("model", Model, Model));
        return lines;
    }

    internal static Dictionary<string, Segment> ComposeRequests(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            SemanticCoordinateContract.PdfStructuredSourcePartsV2,
            CanonicalSemanticExperiment.Baseline,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(
                segment => segment.PackId,
                segment => new Segment(
                    segment.RequestBytes,
                    segment.Owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal)),
                StringComparer.Ordinal);
    }

    internal static string FirstAlias(string identity)
    {
        var first = identity.Split('|')[0];
        return first[..first.LastIndexOf(':')];
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
                "PDF", SourceUniverseSha256, Model, "OpenRouter", V2PromptSha256,
                GoldId: "DOC-0252", GoldHash: GoldSha256, EvaluatorIdentity: EvaluatorId,
                ManifestHash: ManifestSha256, RunId: $"{BaselineId}-r{repeat}",
                CreatedAt: DateTimeOffset.UtcNow, RepeatIdentity: $"r{repeat}"),
            Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}"));

        var authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
            path, classifier, CancellationToken.None,
            experiment: CanonicalSemanticExperiment.Baseline,
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

    private static void Persist(
        BudgetedClassifier classifier, IReadOnlyList<object> runs,
        IReadOnlyList<string> gates, string? aborted)
    {
        var directory = TestRepository.Path(OutputRoot);
        Directory.CreateDirectory(directory);
        var payload = JsonSerializer.Serialize(new
        {
            artifactKind = "a99_structured_v2_target_baseline_run",
            schemaVersion = "a99-structured-v2-target-baseline-run-v1",
            baselineId = BaselineId,
            authorizedCommit = AuthorizedCommit,
            approval = "explicit-user-authorization, DOC-0252 only, packs 5 and 6, 6 calls, cap 9",
            documentId = "DOC-0252",
            profile = V2Profile.ProfileId,
            profileScope = "experiment-scoped routing; DOC-0252's declared authority still names "
                + "STRUCTURED_SOURCE_PARTS and was not promoted",
            protocolVersion = SemanticCoordinateContract.PdfStructuredSourcePartsV2.ProtocolVersion,
            packingPolicy = "COHERENT_REGION_SEGMENTATION_V1",
            targetPackIds = TargetPacks,
            repeats = Repeats,
            primarySemanticCalls = classifier.CallsMade,
            placementCalls = 0,
            maximumProviderCalls = HardCap,
            unusedAllowance = HardCap - classifier.CallsMade,
            model = Model,
            providerRoute = "OpenRouter",
            v2PromptSha256 = V2PromptSha256,
            v2ContractSha256 = V2ContractSha256,
            providerModelInputPlanSha256 = ProviderModelInputPlanSha256,
            pack005RequestSha256 = Pack005RequestSha256,
            pack006RequestSha256 = Pack006RequestSha256,
            sourceSha256 = SourceSha256,
            sourceUniverseSha256 = SourceUniverseSha256,
            goldSha256 = GoldSha256,
            goldTargetCount = TargetGoldCount,
            manifestHash = ManifestSha256,
            evaluatorId = EvaluatorId,
            selectionModeModelVisible = false,
            selectionModeHarnessDerived = true,
            semanticIntervention = "none",
            scoringPerformed = false,
            purpose = "A comparator, not a hypothesis test. It cannot succeed or fail; it establishes "
                + "membership, relation and coordinate behaviour under v2 so a later arm differs by one "
                + "clause.",
            historicalUnusedCapacity = "The 3 unused EXP_MASTHEAD_METADATA calls were not transferred and "
                + "authorize nothing here.",
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
        File.WriteAllText(Path.Combine(directory, "structured-v2-target-baseline-run.v1.json"),
            payload.ReplaceLineEndings("\n"));
    }

    internal sealed record Segment(string Request, HashSet<string> Owned);

    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException();
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("Gate verification must not contact a provider.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
