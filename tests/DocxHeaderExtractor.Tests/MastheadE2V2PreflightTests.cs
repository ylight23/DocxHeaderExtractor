using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EXP_MASTHEAD_METADATA_E2, prepared and frozen before anything is sent.
/// <para>
/// E1 named a category - organisation, event, date, venue - and suppressed nothing: all three
/// masthead families survived all three repeats, and the causal diff attributed no Gold membership,
/// role or relation change to it. So E2 does not add more categories. It adds one invariant:
/// eligibility is decided before placement, and parent-node:NONE cannot be the reason a span is
/// admitted.
/// </para>
/// <para>
/// This is a prompt-policy experiment, not the membership/placement architecture. It tests whether
/// stating the decision order is enough, which is worth knowing before building a contract that
/// enforces it.
/// </para>
/// </summary>
public sealed class MastheadE2V2PreflightTests
{
    private const string PreflightRoot = "eval/a99-closed-loop/exp-masthead-e2-v2-preflight-v1";
    private const string OutputRoot = "eval/a99-closed-loop/exp-masthead-e2-v2-experiment-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string V2ContractSha256 = "565bdc87749a1ce1246238cacd1eb7d550a19939dc9e42d23e76ab3a46a8b0ea";
    private const string BaselinePromptSha256 =
        "6340d1daf507a3d2bf5ce6fbef2b5d7b61735c0e0621a62f8e885ad8b2d8a66e";
    private const string E1PromptSha256 =
        "fb46d62cb7ddbb469d56a54f3c90e37d955c8fce85b8f75257ac19a14b2c64fa";

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    private static readonly (string Family, string[] Aliases)[] MastheadFamilies =
    [
        ("international-program-and-tag", ["L0513:S0", "L0514:S0"]),
        ("hybrid-meeting", ["L0515:S0"]),
        ("date-venue-address", ["L0516:S0", "L0517:S0", "L0518:S0"]),
    ];

    [Fact]
    public void Freeze_the_e2_eligibility_gate_before_any_call()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);

        var v2 = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        Assert.Equal(V2ContractSha256, v2.SchemaHash());

        // ---- §3/§4 the clause, its bytes, and where it sits ---------------------------------------
        var baseline = CanonicalSemanticEngine.SystemPromptFor(v2, CanonicalSemanticExperiment.Baseline);
        var e1 = CanonicalSemanticEngine.SystemPromptFor(
            v2, CanonicalSemanticExperiment.NonStructuralMetadataConstrained);
        var e2 = CanonicalSemanticEngine.SystemPromptFor(
            v2, CanonicalSemanticExperiment.MembershipBeforePlacement);

        Assert.Equal(BaselinePromptSha256, CanonicalArtifactHash.OfText(baseline));
        Assert.Equal(E1PromptSha256, CanonicalArtifactHash.OfText(e1));   // E1 unmoved by E2 existing
        var e2PromptSha256 = CanonicalArtifactHash.OfText(e2);
        Assert.NotEqual(BaselinePromptSha256, e2PromptSha256);
        Assert.NotEqual(E1PromptSha256, e2PromptSha256);

        // E2 is an addition to the semantic core, and it precedes the coordinate clause.
        var clause = CanonicalSemanticEngine.MembershipBeforePlacementClause;
        Assert.Equal(CanonicalSemanticEngine.SystemPrompt + clause + v2.PromptClause, e2);
        Assert.True(
            e2.IndexOf(clause, StringComparison.Ordinal) < e2.IndexOf(v2.PromptClause!, StringComparison.Ordinal),
            "the eligibility clause must precede coordinate serialization instructions");

        // E2 is not E1 plus something: the arms are separate and never combined.
        Assert.DoesNotContain(CanonicalSemanticEngine.NonStructuralMetadataClause, e2, StringComparison.Ordinal);
        Assert.DoesNotContain(clause, e1, StringComparison.Ordinal);

        // No document-specific wording reached production text.
        foreach (var forbidden in new[]
        {
            "DOC-0252", "ICP", "TAG", "Hybrid", "Bamboo", "New York", "Comparison Program",
            "L0513", "L0515", "L0516", "March", "Agenda", "Participants",
        })
        {
            Assert.DoesNotContain(forbidden, clause, StringComparison.OrdinalIgnoreCase);
        }

        // The invariant this arm exists to test is actually stated.
        Assert.Contains("eligibility comes before placement", clause, StringComparison.Ordinal);
        Assert.Contains("must never be used to admit text", clause, StringComparison.Ordinal);

        // ---- §12 exact request authority through the real routed path ------------------------------
        var requests = Compose(plan, CanonicalSemanticExperiment.MembershipBeforePlacement);
        Assert.Equal(TargetPacks, requests.Keys);
        Assert.Equal(requests.Values, Compose(plan, CanonicalSemanticExperiment.MembershipBeforePlacement).Values);

        // The evidence packets are untouched by the clause: only the system prompt differs.
        var baselineRequests = Compose(plan, CanonicalSemanticExperiment.Baseline);
        Assert.Equal(baselineRequests.Values, requests.Values);

        var requestHashes = requests.ToDictionary(
            pair => pair.Key.Split(':')[1],
            pair => CanonicalSemanticRequestComposer.Hash(pair.Value),
            StringComparer.Ordinal);
        var planHash = CanonicalSemanticRequestComposer.Hash(
            string.Join("\u0000", requests.Values.Select(CanonicalSemanticRequestComposer.Hash)));

        // ---- §5 known-Gold safety, over every materialized claim in the corpus ----------------------
        var goldRisk = AssessGoldRisk();
        Assert.Equal(0, goldRisk.AtRisk);
        var targetGold = TargetGold(plan);
        Assert.Equal(14, targetGold.Length);
        Assert.Equal(0, targetGold.Count(claim => !claim.Eligible));

        // ---- §13 capture identities must be fresh --------------------------------------------------
        var captureSlots = Enumerable.Range(1, 3).Select(repeat =>
        {
            var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
            var slot = Path.Combine(directory, SemanticAuthorityCaptureReservationSchema.SlotFileName);
            var existing = Directory.Exists(directory)
                ? Directory.GetFiles(directory).Select(Path.GetFileName).ToArray()
                : [];
            return new
            {
                repeat,
                directoryExists = Directory.Exists(directory),
                slotReserved = File.Exists(slot),
                existingFiles = existing,
                fresh = existing.Length == 0,
            };
        }).ToArray();
        Assert.All(captureSlots, slot => Assert.True(slot.fresh,
            $"r{slot.repeat} already holds capture files; a rerun must not overwrite evidence"));

        FreezeArtifact.AssertJson(PreflightRoot, "exp-masthead-e2-v2-preflight.v1.json", new
        {
            artifactKind = "a99_exp_masthead_e2_v2_preflight",
            schemaVersion = "a99-exp-masthead-e2-v2-preflight-v1",
            experimentId = "EXP_MASTHEAD_METADATA_E2_V2",
            providerCalls = 0,
            modelCalls = 0,
            providerAuthorized = false,

            causalPredecessors = new
            {
                baselineV2 = new
                {
                    scores = new[] { "R1 14/4/0", "R2 14/4/0", "R3 14/6/0" },
                    promptSha256 = BaselinePromptSha256,
                },
                e1V2 = new
                {
                    scores = new[] { "R1 14/6/0", "R2 14/6/0", "R3 14/4/0" },
                    promptSha256 = E1PromptSha256,
                    result = "MASTHEAD_POLICY_INSUFFICIENT",
                    mastheadFamilySuppressions = 0,
                    mastheadFamilyPersistences = 9,
                    rootToNoneReproduced = false,
                    roleRegressionAttributed = 0,
                },
                note = "Frozen as they stand. Nothing here rewrites them.",
            },

            hypothesis = new
            {
                id = "MEMBERSHIP_BEFORE_PLACEMENT",
                statement = "Separating heading eligibility from placement, and forbidding "
                    + "parent-node:NONE from admitting an otherwise ineligible span, will suppress "
                    + "masthead metadata without changing Gold heading membership.",
                whyNotMoreCategories = "E1 already named the category and suppressed nothing. What it left "
                    + "available was the reasoning that prominent identifying text might still be an "
                    + "accepted label, with the tree-less relation as somewhere to put it.",
                notOptionC = "An invariant stated in the prompt, not enforced by a contract. Whether stating "
                    + "it suffices is exactly what this measures.",
            },

            promptAuthority = new
            {
                e2PromptSha256,
                clauseChars = clause.Length,
                clauseSha256 = CanonicalArtifactHash.OfText(clause),
                composition = "BASE_SYSTEM_PROMPT -> E2_ELIGIBILITY_CLAUSE -> V2_COORDINATE_CLAUSE",
                precedesCoordinateClause = true,
                combinedWithE1 = false,
                relationOntologyModified = false,
                documentSpecificWording = false,
            },

            requestAuthority = new
            {
                document = "DOC-0252",
                sourceUniverseSha256 = SourceUniverseSha256,
                contractSha256 = V2ContractSha256,
                packingPolicy = SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1.PolicyId,
                targetPacks = TargetPacks,
                pack005RequestSha256 = requestHashes["PACK_005"],
                pack006RequestSha256 = requestHashes["PACK_006"],
                providerModelInputPlanSha256 = planHash,
                repeatByteEquivalent = true,
                evidencePacketsIdenticalToBaseline = true,
                sharedWithBaselineAndE1 = "The pack request hashes and this plan hash are the same across "
                    + "the baseline, E1 and E2, because the request bytes are the evidence packets and the "
                    + "clause lives in the system prompt. They confirm the packets did not move; they do "
                    + "not identify an arm. The prompt hash is what distinguishes the arms.",
            },

            goldSafety = new
            {
                goldSha256 = GoldSha256,
                materializedClaims = goldRisk.Total,
                knownGoldTpsAtRisk = goldRisk.AtRisk,
                targetGoldClaims = targetGold.Length,
                targetGoldTpsAtRisk = 0,
                targetGold = targetGold.Select(claim => new
                {
                    claim.Identity, claim.Role, claim.Text, claim.Admission,
                }),
                admissionTest = "Each claim must satisfy A (the document's canonical title) or B (opens and "
                    + "names a structural unit at that location) - the clause's own two admissions.",
                crossGenreNoneSafety = "NOT_ESTABLISHED",
                crossGenreNote = "48 of 3955 approved headings are materialized corpus-wide. This checks the "
                    + "claims that exist; it is not a corpus-wide safety proof.",
            },

            targetFamilies = MastheadFamilies.Select(family => new
            {
                family.Family,
                family.Aliases,
                baselineV2 = "PRESENT 3/3",
                e1V2 = "PRESENT 3/3",
                desiredEffect = "family-level suppression",
            }),

            controls = new
            {
                nonTarget = new[]
                {
                    new { alias = "L0550:S0", role = "schedule item",
                          rule = "tracked, not targeted; success may not be claimed from suppressing it" },
                    new { alias = "L0396:S0", role = "body proposition and source-grounding control",
                          rule = "tracked, not targeted; TextNotInAtom must remain a visible refusal" },
                },
                relation = new
                {
                    aliases = new[] { "L0400:S0", "L0420:S0", "L0470:S0", "L0507:S0" },
                    baselineV2 = "ROOT in all repeats",
                    e1V2 = "ROOT_TO_NONE not reproduced",
                    predeclared = "Any systematic ROOT->NONE caused by E2 is regression evidence.",
                },
            },

            outOfScope = new
            {
                semanticRoleOntology = "The free-text role field does not intersect Gold's ontology "
                    + "(MeetingSection, AgendaItem, SectionHeading). Not fixed here, not constrained here, "
                    + "and E2 success is not scored from role wording. Recorded for diagnostics only.",
                relationSerialization = "parent-node:parent-node:... appears 8 times in the baseline. Not "
                    + "repaired here.",
                sourceGrounding = "L0396 alias/text misalignment stays a control. The canonicalizer is not "
                    + "weakened and any TextNotInAtom stays visible as a refusal.",
            },

            captureIdentities = new
            {
                required = 6,
                fresh = captureSlots.All(slot => slot.fresh),
                slots = captureSlots,
                rule = "Any collision, reserved or incomplete slot blocks before the provider is contacted.",
            },

            callPlan = new
            {
                repeats = 3,
                targetPacksPerRepeat = 2,
                primaryProviderCalls = 6,
                placementCalls = 0,
                proposedHardCap = 9,
                previousUnusedAllowance = new
                {
                    baselineV2 = 3,
                    e1V2 = 3,
                    transferable = false,
                    note = "Historical. Neither authorizes anything here.",
                },
            },

            successCriteria = new
            {
                declaredBefore = "any output of this arm exists",
                supported = "masthead family presence materially decreases, all 14 target Gold headings "
                    + "remain semantically proposed and bound, no new systematic Gold relation regression, "
                    + "and no comparable replacement false-positive family appears",
                insufficient = "masthead families substantially persist while Gold behaviour stays intact",
                regressive = "Gold membership or systematic relation behaviour degrades",
                mixed = "suppression together with regression",
                notJudgedBy = "the false-positive total alone, which mixes three mechanisms",
            },

            ifInsufficient = "Continuing to add prose would be low yield. The next step would be an explicit "
                + "two-stage decision contract rather than E3 and E4 wording.",

            conclusionScope = "DOC-0252 causal evidence. Freezing this clause production-wide would need a "
                + "cross-genre cohort this corpus cannot currently supply.",
        });
    }

    private static Dictionary<string, string> Compose(
        PdfStructuredSourceAuthority plan, CanonicalSemanticExperiment experiment)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            SemanticCoordinateContract.PdfStructuredSourcePartsV2,
            experiment,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment => segment.RequestBytes, StringComparer.Ordinal);
    }

    /// <summary>
    /// Each target claim against the clause's own two admissions. A claim that is neither the
    /// document's title nor the name of a unit opened at that location would be at risk.
    /// </summary>
    private static TargetClaim[] TargetGold(PdfStructuredSourceAuthority plan)
    {
        var segments = StructuredV2TargetBaselineTransportTests.ComposeRequests(plan);
        var owned = segments.Values.SelectMany(segment => segment.Owned).ToHashSet(StringComparer.Ordinal);
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");

        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Where(claim => owned.Contains(StructuredV2TargetBaselineTransportTests.FirstAlias(
                claim.GetProperty("identity").GetString()!)))
            .Select(claim =>
            {
                var identity = claim.GetProperty("identity").GetString()!;
                var role = claim.TryGetProperty("semanticRole", out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString()! : "(none)";
                var text = claim.TryGetProperty("approvedWording", out var wording)
                    && wording.ValueKind == JsonValueKind.String ? wording.GetString()! : "(none)";

                // Every approved role in this document names a unit that opens at its location:
                // a meeting section, an agenda item, a section heading. None is descriptive
                // metadata about the document or the occasion, which is what the clause excludes.
                var admission = role is "AgendaItem" or "MeetingSection" or "SectionHeading"
                    ? "B-opens-and-names-a-structural-unit"
                    : "UNCLASSIFIED";
                return new TargetClaim(identity, role, text, admission != "UNCLASSIFIED", admission);
            })
            .OrderBy(claim => claim.Identity, StringComparer.Ordinal)
            .ToArray();
    }

    private static GoldRisk AssessGoldRisk()
    {
        var total = 0;
        var atRisk = 0;
        foreach (var entry in CanonicalGoldRegistry.Entries)
        {
            using var gold = CanonicalGoldRegistry.Resolve(entry.AuthorityId);
            if (!gold.RootElement.TryGetProperty("semantic", out var semantic)) continue;
            foreach (var claim in semantic.GetProperty("claims").EnumerateArray())
            {
                total++;
                var role = claim.TryGetProperty("semanticRole", out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString()! : "(none)";

                // At risk only if the approved role is itself descriptive metadata about the
                // document or the occasion - the one thing the clause tells the model not to report.
                if (role.Contains("Masthead", StringComparison.OrdinalIgnoreCase)
                    || role.Contains("Metadata", StringComparison.OrdinalIgnoreCase)
                    || role.Contains("Venue", StringComparison.OrdinalIgnoreCase)
                    || role.Contains("Date", StringComparison.OrdinalIgnoreCase))
                {
                    atRisk++;
                }
            }
        }
        return new GoldRisk(total, atRisk);
    }

    private sealed record TargetClaim(
        string Identity, string Role, string Text, bool Eligible, string Admission);

    private sealed record GoldRisk(int Total, int AtRisk);

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
