using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A comparator for v2, prepared before anything is sent.
/// <para>
/// v2 changed the bytes the provider sees: a different schema, a different prompt, a different
/// plan hash. That makes the v1 baseline unusable as a comparator for any v2 semantic arm - an A/B
/// against it would move the coordinate contract and the semantic clause at once, and neither
/// result would be attributable. This preflight freezes a v2 baseline over the same two packs,
/// carrying no semantic intervention at all, so that a later arm differs by exactly one clause.
/// </para>
/// <para>
/// It also tests something the offline proofs cannot: how a provider actually behaves when the
/// reply no longer carries a selection mode. 42/42 Gold parts re-derive their mode from the source,
/// but no model has yet answered under the schema that stops asking for one.
/// </para>
/// </summary>
public sealed class StructuredV2TargetBaselinePreflightTests
{
    private const string PreflightRoot = "eval/a99-closed-loop/structured-v2-target-baseline-preflight-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string V2ContractSha256 = "565bdc87749a1ce1246238cacd1eb7d550a19939dc9e42d23e76ab3a46a8b0ea";
    private const string V2PromptSha256 = "6340d1daf507a3d2bf5ce6fbef2b5d7b61735c0e0621a62f8e885ad8b2d8a66e";
    private const string V2FullPlanPredecessorSha256 =
        "5cb9ccd0025785b5ca8bc2ba55ae528aff5dbd0a4b59bd146ffae91775cf6621";
    private const string V1ContractSha256 = "69b99b9099b964a5cf5985b8ec618db49c8ee5c3fa8a2bb8f69993cdc2e24f6f";

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    [Fact]
    public void Freeze_the_v2_target_baseline_before_any_call()
    {
        // ---- §1 v2 coordinate authority ---------------------------------------------------------
        var v2 = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        var v1 = SemanticCoordinateContract.PdfStructuredSourceParts;
        Assert.Equal("a99-semantic-source-parts-v2", v2.ProtocolVersion);
        Assert.Equal(V2ContractSha256, v2.SchemaHash());
        Assert.Equal(V1ContractSha256, v1.SchemaHash());   // v1 still intact beside it

        var prompt = CanonicalSemanticEngine.SystemPromptFor(v2, CanonicalSemanticExperiment.Baseline);
        Assert.Equal(V2PromptSha256, CanonicalArtifactHash.OfText(prompt));

        // selectionMode is not model-visible: not in the schema, not in the words.
        Assert.DoesNotContain("selectionMode", JsonSerializer.Serialize(v2.SchemaFactory()), StringComparison.Ordinal);
        foreach (var token in new[] { "selectionMode", "WHOLE_ALIAS", "VERBATIM_TEXT" })
            Assert.DoesNotContain(token, prompt, StringComparison.Ordinal);

        // ...and it is harness-derived, by the capability this baseline exists to exercise.
        Assert.Equal("HARNESS_DERIVED", SemanticSourcePartCanonicalizer.PendingSelectionMode);
        Assert.Equal("STRUCTURED_SOURCE_PARTS_V2", v2.Binding.BindingId);

        // ---- §5 Gold authority -------------------------------------------------------------------
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var gold = GoldIdentities();
        Assert.Equal(41, gold.Count);

        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);

        // ---- §2 target packs, and §4 exact request authority through the real route ---------------
        var segments = Compose(plan);
        Assert.Equal(TargetPacks, segments.Keys);
        Assert.DoesNotContain("PACK_007", string.Join(",", segments.Keys), StringComparison.Ordinal);

        // §4 byte-identical across R1/R2/R3: composition is what each repeat re-runs.
        var r2 = Compose(plan);
        var r3 = Compose(plan);
        Assert.Equal(segments.Values.Select(item => item.Request), r2.Values.Select(item => item.Request));
        Assert.Equal(segments.Values.Select(item => item.Request), r3.Values.Select(item => item.Request));

        var requestHashes = segments.ToDictionary(
            pair => pair.Key.Split(':')[1],
            pair => CanonicalSemanticRequestComposer.Hash(pair.Value.Request),
            StringComparer.Ordinal);
        var targetPlanHash = CanonicalSemanticRequestComposer.Hash(
            string.Join("\u0000", segments.Values.Select(item => CanonicalSemanticRequestComposer.Hash(item.Request))));
        Assert.NotEqual(V2FullPlanPredecessorSha256, targetPlanHash);  // two packs, not six

        // ---- §3 no semantic intervention ----------------------------------------------------------
        Assert.Equal(CanonicalSemanticEngine.SystemPrompt + v2.PromptClause, prompt);
        Assert.DoesNotContain(CanonicalSemanticEngine.NonStructuralMetadataClause, prompt, StringComparison.Ordinal);

        // ---- packing is unmoved: the same packs own the same atoms as under v1 ---------------------
        var underV1 = Compose(plan, v1);
        Assert.Equal(TargetPacks, underV1.Keys);
        foreach (var pack in TargetPacks)
            Assert.Equal(underV1[pack].Owned, segments[pack].Owned);
        Assert.NotEqual(underV1[TargetPacks[0]].Request, segments[TargetPacks[0]].Request);  // only the contract moved

        // ---- §5 target Gold population, derived from ownership rather than asserted ---------------
        var goldByPack = TargetPacks.ToDictionary(
            pack => pack.Split(':')[1],
            pack => gold.Where(identity => segments[pack].Owned.Contains(FirstAlias(identity)))
                .OrderBy(identity => identity, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        var targetGoldCount = goldByPack.Values.Sum(claims => claims.Length);

        FreezeArtifact.AssertJson(PreflightRoot, "structured-v2-target-baseline-preflight.v1.json", new
        {
            artifactKind = "a99_structured_v2_target_baseline_preflight",
            schemaVersion = "a99-structured-v2-target-baseline-preflight-v1",
            baselineId = "V2_TARGET_BASELINE",
            providerCalls = 0,
            modelCalls = 0,
            providerAuthorized = false,

            why = new
            {
                problem = "v2 changed the provider-visible schema, prompt and plan hash. An arm run under v2 "
                    + "and compared against the v1 baseline would move two things at once.",
                purpose = "A comparator that differs from a later semantic arm by exactly one clause.",
                secondPurpose = "The first provider behaviour ever observed under a schema that does not ask "
                    + "for a selection mode. The 42/42 derivation proof is offline; this is not.",
                notAHypothesisTest = "This baseline tests no semantic hypothesis and cannot succeed or fail. "
                    + "It establishes membership, relation and coordinate behaviour under v2.",
            },

            coordinateAuthority = new
            {
                head = "8951c5c",
                profile = "STRUCTURED_SOURCE_PARTS_V2",
                protocolVersion = v2.ProtocolVersion,
                contractSha256 = V2ContractSha256,
                promptSha256 = V2PromptSha256,
                bindingId = v2.Binding.BindingId,
                packingPolicy = SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1.PolicyId,
                packingPolicyVersion = SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1.PolicyVersion,
                selectionModeModelVisible = false,
                selectionModeHarnessDerived = true,
                v2FullPlanPredecessorSha256 = V2FullPlanPredecessorSha256,
                v1ContractSha256 = V1ContractSha256,
                v1Status = "unchanged and still reachable; historical runs stay readable against it",
            },

            requestAuthority = new
            {
                document = "DOC-0252",
                sourceUniverseSha256 = SourceUniverseSha256,
                targetPacks = TargetPacks,
                pack007Included = false,
                pack005RequestSha256 = requestHashes["PACK_005"],
                pack006RequestSha256 = requestHashes["PACK_006"],
                targetProviderModelInputPlanSha256 = targetPlanHash,
                byteIdenticalAcrossRepeats = true,
                ownedAtoms = TargetPacks.ToDictionary(
                    pack => pack.Split(':')[1], pack => segments[pack].Owned.Count, StringComparer.Ordinal),
                ownershipMatchesV1 = true,
                ownershipNote = "Packing is contract-independent and was not touched: the same packs own the "
                    + "same atoms under v1 and v2. Only the reply shape on the wire differs.",
            },

            goldAuthority = new
            {
                document = "DOC-0252",
                goldSha256 = GoldSha256,
                claims = 41,
                targetGoldCount,
                targetGoldByPack = goldByPack.ToDictionary(
                    pair => pair.Key, pair => pair.Value.Length, StringComparer.Ordinal),
                targetGoldIdentities = goldByPack,
                derivation = "Derived from pack ownership of each claim's first alias, not hard-coded.",
            },

            semanticIntervention = new
            {
                mastheadClause = false,
                bodyPropositionClause = false,
                scheduleItemClause = false,
                prompt = "the unmodified v2 baseline prompt",
            },

            callPlan = new
            {
                repeats = 3,
                targetPacksPerRepeat = 2,
                primaryProviderCalls = 6,
                placementCalls = 0,
                proposedHardCap = 9,
                historicalUnusedCapacity = new
                {
                    experiment = "EXP_MASTHEAD_METADATA",
                    unused = 3,
                    transferable = false,
                    note = "Historical unused capacity. It authorizes nothing here.",
                },
            },

            captureRequirement = new
            {
                replayComplete = true,
                perCall = new[]
                {
                    "exact request bytes and sha256",
                    "exact raw response bytes and sha256",
                    "authority profile",
                    "packing policy id and version",
                    "coordinate contract version and schema hash",
                    "repeat index",
                    "packId",
                    "model and provider lineage",
                },
            },

            predeclaredMeasurements = new
            {
                perPackPerRepeat = new[]
                {
                    "raw proposals", "canonicalized", "bound", "refused (by reason)",
                    "TP", "FP", "FN", "semanticRole", "relationHints", "parent relation",
                    "raw response sha256",
                },
                mustBeZero = new[] { "UnexpectedVerbatimText" },
                zeroNote = "Under v2 the model cannot state a mode, so this refusal class should be "
                    + "structurally unreachable. A non-zero count would mean the schema is not the only "
                    + "thing shaping the reply.",
                refusalsCountedByReason = "A claim not proposed and a claim refused on shape are different "
                    + "outcomes and are never merged into one number.",
            },

            laterAbRequirement = new
            {
                armName = "EXP_MASTHEAD_METADATA_V2",
                mustMatch = new[]
                {
                    "authority profile", "packing policy", "Gold hash", "target packs",
                    "model and provider settings", "repeat count", "coordinate contract",
                },
                intendedDelta = "the masthead semantic clause, and nothing else",
                runInThisTask = false,
            },

            openQuestionsNotAnsweredHere = new
            {
                q1 = "Did canonicalization remove coordinate-shape instability in practice? Proven offline; "
                    + "unobserved against a provider until this baseline runs.",
                q2 = "Does the masthead clause help classification on its own? The v1 arm failed, but under a "
                    + "coordinate confound, so that result does not answer it.",
                relationPolicy = "ROOT -> NONE is untouched and remains a separate question. If genuine "
                    + "headings move to NONE in a v2 arm where this baseline does not, that is evidence for "
                    + "membership/placement separation - which this task does not decide.",
            },
        });
    }

    private static Dictionary<string, Segment> Compose(
        PdfStructuredSourceAuthority plan, SemanticCoordinateContract? contract = null)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            contract ?? SemanticCoordinateContract.PdfStructuredSourcePartsV2,
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

    private static HashSet<string> GoldIdentities()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("identity").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FirstAlias(string identity)
    {
        var first = identity.Split('|')[0];
        return first[..first.LastIndexOf(':')];
    }

    private sealed record Segment(string Request, HashSet<string> Owned);

    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException();
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("PROVIDER_CALLS must remain 0 in a preflight.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
