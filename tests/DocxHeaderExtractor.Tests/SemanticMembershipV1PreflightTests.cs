using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PHYSICAL_STAGE1_MEMBERSHIP_ONLY, prepared and frozen before anything is sent.
/// <para>
/// The experiment this prepares changes exactly one thing: the model is asked only which source
/// spans are accepted claims, and is not told that placement or a role exists. Same document, same
/// packs, same evidence bytes, same model and settings, same source-grounding seam. No masthead
/// wording is carried over from E1 or E2 - importing either would move two variables at once and
/// make the result unreadable, which is the mistake this whole stage exists to stop repeating.
/// </para>
/// <para>
/// The delicate part is the prompt derivation. The base prompt states which things are real
/// headings that hold no place in the tree inside the paragraph explaining parent-node:NONE, so
/// deleting that paragraph wholesale would quietly narrow what the model accepts. That content is
/// kept and carried by the membership disposition instead.
/// </para>
/// </summary>
public sealed class SemanticMembershipV1PreflightTests
{
    private const string PreflightRoot = "eval/a99-closed-loop/stage1-membership-preflight-v1";
    private const string OutputRoot = "eval/a99-closed-loop/stage1-membership-experiment-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string BaselinePromptSha256 =
        "6340d1daf507a3d2bf5ce6fbef2b5d7b61735c0e0621a62f8e885ad8b2d8a66e";
    private const string V2ContractSha256 = "565bdc87749a1ce1246238cacd1eb7d550a19939dc9e42d23e76ab3a46a8b0ea";
    private const string EvidencePacket005Sha256 =
        "8f3b430a78608bb10d62fb3655d2742b236470b5f228967f6fd8b328896d8c16";
    private const string EvidencePacket006Sha256 =
        "6d867a0d836ba0fbbe5f041fc046fc932fe2fea7f23f100f7f0a59301048cff0";
    private const string BaselineProviderPlanSha256 =
        "c0348ca82bf7cbccc1d7cd3222951754989752410a5260724941f4b3ffc0f45e";

    private const string Model = "qwen/qwen3.7-flash";

    /// <summary>
    /// The capture slots exactly as this preflight observed them before authorization. Recorded
    /// rather than recomputed, so the artifact keeps stating the condition that justified the
    /// authorization instead of drifting to describe what a later run left in those directories.
    /// </summary>
    private static readonly object[] FrozenPreflightSlots =
    [
        new { repeat = 1, directoryExists = false, existingFiles = Array.Empty<string>() },
        new { repeat = 2, directoryExists = false, existingFiles = Array.Empty<string>() },
        new { repeat = 3, directoryExists = false, existingFiles = Array.Empty<string>() },
    ];

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    /// <summary>Concepts the model must not learn from its own task instructions.</summary>
    private static readonly string[] Stage2Surface =
    [
        "parent-node", "parentClaimId", "relationHints", "semanticRole", "selectionMode",
        "ROOT", "NONE", "hierarchy", "same-node", "level",
    ];

    [Fact]
    public void Freeze_the_stage1_membership_protocol_before_any_call()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);

        var stage1 = SemanticCoordinateContract.PdfSemanticMembershipV1;
        var v2 = SemanticCoordinateContract.PdfStructuredSourcePartsV2;

        // ---- §3/§12 a new authority beside the untouched predecessors ------------------------------
        Assert.Equal("a99-semantic-membership-v1", stage1.ProtocolVersion);
        Assert.Equal(V2ContractSha256, v2.SchemaHash());
        Assert.Equal("69b99b9099b964a5cf5985b8ec618db49c8ee5c3fa8a2bb8f69993cdc2e24f6f",
            SemanticCoordinateContract.PdfStructuredSourceParts.SchemaHash());
        var schemaHash = stage1.SchemaHash();
        Assert.NotEqual(V2ContractSha256, schemaHash);

        // ---- §4/§20/§21 the schema offers no placement, role or coordinate mode --------------------
        var schemaJson = JsonSerializer.Serialize(stage1.Schema());
        foreach (var forbidden in Stage2Surface)
            Assert.DoesNotContain(forbidden, schemaJson, StringComparison.Ordinal);
        Assert.Contains("DOCUMENT_LABEL", schemaJson, StringComparison.Ordinal);
        Assert.Contains("STRUCTURAL_UNIT", schemaJson, StringComparison.Ordinal);
        Assert.Contains("sourceAlias", schemaJson, StringComparison.Ordinal);
        Assert.Contains("verbatimText", schemaJson, StringComparison.Ordinal);

        // ---- §8/§9 the prompt, and what was removed from the baseline ------------------------------
        var prompt = CanonicalSemanticEngine.MembershipPromptFor(stage1);
        var promptHash = CanonicalArtifactHash.OfText(prompt);
        var baselinePrompt = CanonicalSemanticEngine.SystemPromptFor(v2, CanonicalSemanticExperiment.Baseline);
        Assert.Equal(BaselinePromptSha256, CanonicalArtifactHash.OfText(baselinePrompt));
        Assert.NotEqual(BaselinePromptSha256, promptHash);

        Assert.Equal(CanonicalSemanticEngine.Stage1MembershipPrompt + stage1.PromptClause, prompt);

        // No experiment wording of any kind reached it.
        Assert.DoesNotContain(CanonicalSemanticEngine.NonStructuralMetadataClause, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(CanonicalSemanticEngine.MembershipBeforePlacementClause, prompt, StringComparison.Ordinal);
        // Whole words. A substring test flags "TAG" inside "stage" and would report a clean
        // prompt as contaminated, which is how a real check becomes one nobody trusts.
        foreach (var forbidden in new[]
        {
            "masthead", "organisation", "programme", "venue", "eligibility",
            "DOC-0252", "ICP", "TAG", "Hybrid", "Agenda", "Participants",
        })
        {
            Assert.DoesNotMatch(WholeWord(forbidden), prompt);
        }

        // And the check is not vacuous: it fires on wording that really is contaminated.
        Assert.Matches(WholeWord("masthead"), "a masthead block");
        Assert.DoesNotMatch(WholeWord("TAG"), "the membership stage");

        // The model cannot learn from its task that a second stage exists.
        foreach (var concept in new[]
        {
            "parent-node", "parentClaimId", "relationHints", "semanticRole", "selectionMode",
            "same-node", "hierarchy",
        })
        {
            Assert.DoesNotContain(concept, prompt, StringComparison.Ordinal);
        }

        // The membership content the base prompt kept inside its relation paragraph is preserved.
        foreach (var preserved in new[]
        {
            "title and subtitle", "running headers and footers", "table and figure labels",
            "form labels and signature labels", "you should still report them",
        })
        {
            Assert.Contains(preserved, prompt, StringComparison.OrdinalIgnoreCase);
        }

        // ---- §10 evidence packets, unchanged -------------------------------------------------------
        var requests = Compose(plan, stage1);
        Assert.Equal(TargetPacks, requests.Keys);
        var evidence = requests.ToDictionary(
            pair => pair.Key.Split(':')[1],
            pair => CanonicalSemanticRequestComposer.Hash(pair.Value),
            StringComparer.Ordinal);

        // The user message is the packet plus the appended schema, and the packet itself carries
        // the contract's protocol version. So Stage 1's evidence bytes cannot be literally equal to
        // the baseline's, and claiming they are would be the same mislabelling that already cost
        // one preflight its lineage. What must hold is that the atoms, ownership, margins and
        // layout are untouched - so the packets are compared with the protocol field, and only the
        // protocol field, neutralised.
        var baselineRequests = Compose(plan, v2);
        Assert.Equal(EvidencePacket005Sha256,
            CanonicalSemanticRequestComposer.Hash(baselineRequests[TargetPacks[0]]));
        Assert.Equal(EvidencePacket006Sha256,
            CanonicalSemanticRequestComposer.Hash(baselineRequests[TargetPacks[1]]));

        var evidenceContentIdentical = TargetPacks.All(pack => string.Equals(
            NeutralizeProtocol(PacketOf(baselineRequests[pack]), v2.ProtocolVersion),
            NeutralizeProtocol(PacketOf(requests[pack]), stage1.ProtocolVersion),
            StringComparison.Ordinal));
        Assert.True(evidenceContentIdentical, "the evidence content beneath the protocol field moved");

        // The literal packets differ, and by exactly one field - proven by showing that swapping
        // the protocol back makes them equal, and that they were not equal before.
        Assert.NotEqual(PacketOf(baselineRequests[TargetPacks[0]]), PacketOf(requests[TargetPacks[0]]));
        var stage1PacketHashes = TargetPacks.ToDictionary(
            pack => pack.Split(':')[1],
            pack => CanonicalSemanticRequestComposer.Hash(PacketOf(requests[pack])),
            StringComparer.Ordinal);

        // ---- §13 the bytes that actually reach the provider ----------------------------------------
        var providerInputs = ProviderInputs(requests, prompt);
        var planHash = ProviderPlanHash(requests, prompt);
        var baselineProviderPlan = ProviderPlanHash(baselineRequests, baselinePrompt);
        Assert.Equal(BaselineProviderPlanSha256, baselineProviderPlan);
        Assert.NotEqual(baselineProviderPlan, planHash);
        Assert.NotEqual(
            ProviderInputs(baselineRequests, baselinePrompt)["PACK_005"], providerInputs["PACK_005"]);

        // Deterministic.
        Assert.Equal(planHash, ProviderPlanHash(Compose(plan, stage1), prompt));

        // ---- §15/§19 the decoded reply reaches the validated internal seam ---------------------------
        var multiPartGold = GoldIdentities().Single(identity => identity.Contains('|'));
        var fixture = MultiPartFixture(plan, multiPartGold);
        using var reply = JsonDocument.Parse(fixture);
        Assert.Empty(stage1.Validate(reply.RootElement));

        var accepted = SemanticMembershipV1.Accept(
            plan.Atoms, SourceSha256, SemanticMembershipV1.Decode(reply.RootElement));
        var claim = Assert.Single(accepted.Accepted);
        Assert.Equal(multiPartGold, claim.CanonicalIdentity);
        Assert.Equal(2, claim.Coordinates.Count);
        Assert.Equal(Stage1AuthorityClaimId.For(SourceSha256, claim.Coordinates).Value, claim.ClaimId.Value);
        Assert.Equal(66, claim.ClaimId.Value.Length);
        Assert.Empty(accepted.Refusals);

        // ---- §23 six fresh capture identities, distinct from every predecessor -----------------------
        var captureSlots = Enumerable.Range(1, 3).Select(repeat =>
        {
            var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
            return new
            {
                repeat,
                directoryExists = Directory.Exists(directory),
                existingFiles = Directory.Exists(directory)
                    ? Directory.GetFiles(directory).Select(Path.GetFileName).ToArray()
                    : [],
            };
        }).ToArray();
        var runArtifact = Path.Combine(TestRepository.Path(OutputRoot), "stage1-membership-run.v1.json");
        if (!File.Exists(runArtifact))
            Assert.All(captureSlots, slot => Assert.Empty(slot.existingFiles));

        FreezeArtifact.AssertJson(PreflightRoot, "stage1-membership-preflight.v1.json", new
        {
            artifactKind = "a99_stage1_membership_preflight",
            schemaVersion = "a99-stage1-membership-preflight-v1",
            experimentId = "PHYSICAL_STAGE1_MEMBERSHIP_ONLY",
            providerCalls = 0,
            modelCalls = 0,
            providerAuthorized = false,

            hypothesis = new
            {
                statement = "Removing structural placement and semantic role from the model's task "
                    + "changes, or does not change, semantic membership.",
                onlyIntendedChange = "monolithic semantic+role+placement task -> physical membership-only task",
                explicitlyNotAlsoTesting = new[]
                {
                    "new masthead wording", "E1 wording", "E2 wording", "a new candidate policy",
                    "new packing", "new source evidence", "new model settings",
                },
            },

            protocol = new
            {
                stage1 = new
                {
                    protocolVersion = stage1.ProtocolVersion,
                    schemaSha256 = schemaHash,
                    promptSha256 = promptHash,
                    bindingId = stage1.Binding.BindingId,
                    coordinateSystem = stage1.CoordinateSystem,
                },
                predecessorsUnchanged = new
                {
                    sourcePartsV1 = SemanticCoordinateContract.PdfStructuredSourceParts.SchemaHash(),
                    sourcePartsV2 = V2ContractSha256,
                    v2BaselinePromptSha256 = BaselinePromptSha256,
                },
            },

            fieldOwnership = new
            {
                modelDecides = new[]
                {
                    "whether a claim is accepted", "the source alias or aliases",
                    "the exact quoted text when selecting part of an occurrence",
                    "the order of source parts", "DOCUMENT_LABEL or STRUCTURAL_UNIT",
                },
                modelDoesNotDecide = new[]
                {
                    "selectionMode", "offsets", "structural parent", "ROOT", "NONE",
                    "hierarchy", "semanticRole",
                },
                harnessOwns = new[]
                {
                    "selectionMode", "exact UTF-16 coordinates", "AuthorityClaimId",
                    "grounding refusal", "collision detection",
                },
                providerVisible = new
                {
                    semanticRole = false,
                    relationFields = false,
                    selectionMode = false,
                    offsets = false,
                    stage2Fields = false,
                },
            },

            promptDerivation = new
            {
                derivedFrom = "the clean structured-v2 baseline semantic core",
                newMembershipPolicyIntroduced = false,
                removed = new object[]
                {
                    new
                    {
                        instruction = "REQUIRED for every heading: exactly one relationHints entry; "
                            + "parent-node:<alias> / ROOT / NONE",
                        classification = "PLACEMENT_ONLY",
                    },
                    new
                    {
                        instruction = "Putting a title at ROOT instead pushes every real section one "
                            + "level deeper",
                        classification = "PLACEMENT_ONLY",
                    },
                    new
                    {
                        instruction = "Never emit a numeric level; the harness derives level from relations",
                        classification = "PLACEMENT_ONLY",
                    },
                    new
                    {
                        instruction = "OPTIONAL same-node:<key> for another occurrence of a section "
                            + "already reported",
                        classification = "PLACEMENT_ONLY",
                        note = "Occurrence grouping is a graph question over accepted claims, so it "
                            + "belongs to the placement stage rather than to acceptance.",
                    },
                    new
                    {
                        instruction = "sourceAliases plural form",
                        classification = "HARNESS_OWNED_COORDINATE",
                        note = "Superseded by ordered sourceParts, which v2 already proved.",
                    },
                },
                roleOnlyRemovals = new
                {
                    fromPrompt = 0,
                    fromSchema = 1,
                    note = "semanticRole never appeared in the prompt text - it existed only as a schema "
                        + "field. That is consistent with both arms mismatching Gold's ontology on all 42 "
                        + "target claims: nothing ever told the model what vocabulary to use.",
                },
                preservedMembershipContent = new
                {
                    what = "the document's own title and subtitle, running headers and footers, table and "
                        + "figure labels, form and signature labels are real labels and should still be "
                        + "reported",
                    wasStatedIn = "the parent-node:NONE paragraph of the base prompt",
                    nowCarriedBy = "the DOCUMENT_LABEL disposition",
                    why = "That sentence is membership wearing a placement token. Dropping the paragraph "
                        + "wholesale would have narrowed what the model accepts and turned a task "
                        + "decomposition into an unauthorized policy change.",
                },
                justificationForEveryMembershipRemoval = "none were removed",
            },

            requestAuthority = new
            {
                document = "DOC-0252",
                sourceSha256 = SourceSha256,
                sourceUniverseSha256 = SourceUniverseSha256,
                packingPolicy = SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1.PolicyId,
                targetPacks = TargetPacks,

                baselineEvidencePacket005Sha256 = EvidencePacket005Sha256,
                baselineEvidencePacket006Sha256 = EvidencePacket006Sha256,
                stage1EvidencePacket005Sha256 = stage1PacketHashes["PACK_005"],
                stage1EvidencePacket006Sha256 = stage1PacketHashes["PACK_006"],
                evidenceByteIdenticalToBaseline = false,
                evidenceContentIdenticalToBaseline = true,
                evidenceMeaning = "The packet the harness builds from atoms, ownership, margins and "
                    + "layout, before the schema is appended. It also carries the contract's protocol "
                    + "version, so a Stage-1 packet cannot be byte-identical to a v2 one.",
                evidenceProof = "The packets are equal once the protocol field, and only the protocol "
                    + "field, is neutralised; they are unequal without that. So atoms, ownership, "
                    + "margins and layout did not move, and the difference is one declared string.",
                honestyNote = "Reporting this as byte-identical would repeat the mislabelling that "
                    + "already cost one preflight its lineage.",

                stage1UserMessage005Sha256 = evidence["PACK_005"],
                stage1UserMessage006Sha256 = evidence["PACK_006"],
                userMessageNote = "The composer appends SCHEMA= to the packet, so the user message "
                    + "differs from the baseline's by exactly the schema. The packet inside it is "
                    + "asserted identical.",

                providerInputPack005Sha256 = providerInputs["PACK_005"],
                providerInputPack006Sha256 = providerInputs["PACK_006"],
                providerModelInputPlanSha256 = planHash,
                providerInputMeaning = "system prompt and user message together, composed exactly as the "
                    + "adapter composes them when it reserves a capture slot",
                providerInputDiffersFromBaseline = true,
                baselineProviderModelInputPlanSha256 = baselineProviderPlan,
                repeatByteEquivalent = true,
            },

            providerSettings = new
            {
                model = Model,
                route = "OpenRouter",
                temperature = 0,
                reasoningEffort = "none",
                responseFormat = "json_object",
                seedOrDecodingParameterAddedForThisExperiment = false,
                unavoidableDifference = "none beyond the response schema itself",
            },

            internalSeamReuse = new
            {
                stage1AcceptedClaimReused = true,
                authorityClaimIdReused = true,
                stage1MembershipEvaluatorReused = true,
                canonicalizerReused = true,
                binderReused = true,
                secondCompetingRepresentation = false,
                note = "The provider-facing DTO is transport form; Stage1AcceptedClaim is authority form.",
            },

            failClosedCases = new[]
            {
                "unknown alias", "quote not found in the named alias",
                "ambiguous quote without disambiguation", "duplicate source parts",
                "overlapping parts", "out-of-source-order multi-part claim",
                "invalid membership disposition", "malformed JSON or schema",
                "AuthorityClaimId collision",
            },
            noWideningOnGroundingFailure = true,

            comparator = new
            {
                arm = "STRUCTURED_V2_TARGET_BASELINE",
                promptSha256 = BaselinePromptSha256,
                membership = new[] { "R1 14/4/0", "R2 14/4/0", "R3 14/6/0" },
                goldBound = "14/14 in every repeat",
                targetGoldCount = 14,
                mastheadFamilies = new
                {
                    f1 = "L0513/L0514 organisation and programme masthead - PRESENT 3/3",
                    f2 = "L0515 meeting-format masthead - PRESENT 3/3",
                    f3 = "L0516-derived event, date and venue metadata - PRESENT 3/3",
                },
                notUsedAsPrimaryComparator = new[] { "EXP_MASTHEAD_METADATA_V2", "EXP_MASTHEAD_METADATA_E2_V2" },
            },

            controls = new object[]
            {
                new { alias = "L0396:S0", role = "body proposition and source-grounding control",
                      rule = "diagnostic only; its disappearance is not masthead success" },
                new { alias = "L0550:S0", role = "schedule item",
                      rule = "diagnostic only; its disappearance is not masthead success" },
            },

            predeclaredMetrics = new
            {
                perRepeat = new[]
                {
                    "raw claims proposed", "grounding refusals by reason", "accepted bound claims",
                    "TP", "FP", "FN", "masthead family F1/F2/F3 presence",
                    "other false-positive identities", "AuthorityClaimId stability",
                },
                outcomeClasses = new[] { "NOT_PROPOSED", "PROPOSED_BUT_REFUSED", "BOUND" },
                metricsThatDoNotExist = new[]
                {
                    "relation correctness", "ROOT correctness", "NONE usage", "semanticRole agreement",
                },
                why = "Those outputs do not exist under this protocol, so there is nothing to measure "
                    + "and nothing that can quietly influence the membership score.",
            },

            predeclaredInterpretation = new object[]
            {
                new
                {
                    outcome = "A", condition = "Gold stays 14/14 and masthead families materially contract",
                    classification = "PHYSICAL_TASK_SEPARATION_AFFECTS_MEMBERSHIP",
                    meaning = "The model could discriminate all along; the monolithic task was making it "
                        + "use that ability badly.",
                },
                new
                {
                    outcome = "B", condition = "Gold stays 14/14 and masthead families remain substantially present",
                    classification = "TWO_STAGE_SEPARATION_DOES_NOT_FIX_MEMBERSHIP_POLICY",
                    meaning = "Placement and serialization coupling are eliminated as explanations. The "
                        + "problem is Stage-1 semantic discrimination and the membership policy itself.",
                },
                new
                {
                    outcome = "C", condition = "Gold membership regresses",
                    classification = "STAGE1_MEMBERSHIP_CONTRACT_REGRESSIVE",
                },
                new
                {
                    outcome = "D", condition = "grounding or contract integrity fails",
                    classification = "STAGE1_PROTOCOL_INTEGRITY_DEFECT",
                },
                new { notClassifiedBy = "the false-positive total alone" },
            },

            captureRequirement = new
            {
                replayComplete = true,
                perCall = new[]
                {
                    "protocol", "schema hash", "prompt hash", "packing policy", "packId", "repeat",
                    "exact request bytes and sha256", "raw response bytes and sha256",
                    "provider and model lineage",
                },
                identities = new
                {
                    required = 6,
                    // Frozen as observed when authorization was granted, before any call existed. A
                    // later reader is told what justified the authorization, not what the run left
                    // behind afterwards.
                    fresh = true,
                    slots = FrozenPreflightSlots,
                    distinctFromPredecessors = "separate experiment root, and a prompt and provider-input "
                        + "hash shared with no other arm",
                },
            },

            callPlan = new
            {
                repeats = 3,
                targetPacksPerRepeat = 2,
                stage1ProviderCalls = 6,
                stage2ProviderCalls = 0,
                placementCalls = 0,
                proposedHardCap = 9,
                stage2RequestGenerated = false,
                afterEachResponse = "validate, decode, canonicalize, bind, AuthorityClaimId, freeze "
                    + "accepted set, score membership, stop",
                historicalUnusedAllowance = "none transfers",
            },

            stage2 = new { implemented = false, batchingPolicy = "DEFERRED" },

            limitations = new
            {
                materializedGold = "48 / 3955",
                crossGenreGeneralizationEstablished = false,
                singleDocument = "DOC-0252",
                note = "Even a successful result here is DOC-0252 evidence, not corpus-wide semantic proof.",
            },
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static System.Text.RegularExpressions.Regex WholeWord(string term) =>
        new($@"\b{System.Text.RegularExpressions.Regex.Escape(term)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The packet with its declared protocol version replaced by a fixed token, so two packets
    /// built under different contracts can be compared on everything except the contract they name.
    /// </summary>
    private static string NeutralizeProtocol(string packet, string protocolVersion) =>
        packet.Replace(protocolVersion, "<PROTOCOL>", StringComparison.Ordinal);

    /// <summary>The evidence packet alone - the user message with the appended schema removed.</summary>
    private static string PacketOf(string requestBytes)
    {
        var marker = requestBytes.LastIndexOf("\nSCHEMA=", StringComparison.Ordinal);
        return marker < 0 ? requestBytes : requestBytes[..marker];
    }

    private static Dictionary<string, string> Compose(
        PdfStructuredSourceAuthority plan, SemanticCoordinateContract contract)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            contract,
            CanonicalSemanticExperiment.Baseline,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment => segment.RequestBytes, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ProviderInputs(
        Dictionary<string, string> requests, string systemPrompt) =>
        requests.ToDictionary(
            pair => pair.Key.Split(':')[1],
            pair => SemanticAuthorityTransportCall.Sha256Utf8(
                JsonSerializer.Serialize(new { systemPrompt, userMessage = pair.Value })),
            StringComparer.Ordinal);

    private static string ProviderPlanHash(Dictionary<string, string> requests, string systemPrompt) =>
        CanonicalSemanticRequestComposer.Hash(string.Join("\u0000", TargetPacks.Select(pack =>
            SemanticAuthorityTransportCall.Sha256Utf8(
                JsonSerializer.Serialize(new { systemPrompt, userMessage = requests[pack] })))));

    /// <summary>A Stage-1 reply naming the corpus's one multi-part claim, in its exact two parts.</summary>
    private static string MultiPartFixture(PdfStructuredSourceAuthority plan, string identity)
    {
        var parts = identity.Split('|').Select(part =>
        {
            var split = part.LastIndexOf(':');
            var alias = part[..split];
            var span = part[(split + 1)..].Split('-');
            var atom = plan.Atoms.First(item => item.Alias == alias);
            var start = int.Parse(span[0]);
            var end = int.Parse(span[1]);
            var whole = start == 0 && end == atom.Text.Length;
            return whole
                ? $"{{\"sourceAlias\":{JsonSerializer.Serialize(alias)}}}"
                : $"{{\"sourceAlias\":{JsonSerializer.Serialize(alias)},\"verbatimText\":"
                    + $"{JsonSerializer.Serialize(atom.Text[start..end])}}}";
        });

        return "{\"claims\":[{\"membership\":\"STRUCTURAL_UNIT\",\"sourceParts\":["
            + string.Join(",", parts) + "]}]}";
    }

    private static HashSet<string> GoldIdentities()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("identity").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

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
