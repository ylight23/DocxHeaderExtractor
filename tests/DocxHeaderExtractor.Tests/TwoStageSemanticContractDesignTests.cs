using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A successor architecture, designed and proven feasible offline - not implemented.
/// <para>
/// Two prompt interventions have now failed on the same lever. E1 named the category and suppressed
/// nothing; E2 stated the invariant and suppressed one family in one repeat out of nine. Neither
/// touched Gold: membership stayed 14/14 bound across three repeats in both arms, no ROOT became
/// NONE, and no role mismatch was attributable. The remaining problem is the membership policy
/// itself, and the thing that keeps it out of reach is that one generation decides membership and
/// placement together.
/// </para>
/// <para>
/// The projections here run over captures that already exist. They establish that the proposed
/// contracts can represent everything the current pipeline produces; they do not establish that a
/// separate placement model would behave well, which only a real run can show.
/// </para>
/// </summary>
public sealed class TwoStageSemanticContractDesignTests
{
    private const string DesignRoot = "eval/a99-closed-loop/two-stage-semantic-contract-design-v1";
    private const string BaselineRoot = "eval/a99-closed-loop/structured-v2-target-baseline-v1/DOC-0252";
    private const string E1Root = "eval/a99-closed-loop/exp-masthead-metadata-v2-experiment-v1/DOC-0252";
    private const string E2Root = "eval/a99-closed-loop/exp-masthead-e2-v2-experiment-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";

    private static readonly string[] MastheadAliases =
        ["L0513:S0", "L0514:S0", "L0515:S0", "L0516:S0", "L0517:S0", "L0518:S0"];

    [Fact]
    public void Design_the_two_stage_membership_and_placement_contract()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var contract = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        var gold = GoldClaims();

        // ---- §10 replay feasibility over every arm already captured --------------------------------
        var arms = new (string Name, string Root)[]
        {
            ("STRUCTURED_V2_TARGET_BASELINE", BaselineRoot),
            ("EXP_MASTHEAD_METADATA_V2", E1Root),
            ("EXP_MASTHEAD_METADATA_E2_V2", E2Root),
        };

        var projections = new List<object>();
        var allProjectable = true;
        foreach (var (name, root) in arms)
        {
            for (var repeat = 1; repeat <= 3; repeat++)
            {
                var claims = ProjectStageOne(root, repeat, plan, contract);

                // A claim id must be a bijection onto a bound coordinate identity, or Stage 2 would
                // be addressing something that is not one claim.
                var identities = claims.Select(claim => claim.Identity).ToArray();
                var ids = claims.Select(claim => claim.ClaimId).ToArray();
                Assert.Equal(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());
                Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

                // Every projected claim carries its membership without any placement field.
                Assert.All(claims, claim => Assert.True(
                    claim.Disposition is "DOCUMENT_LABEL" or "STRUCTURAL_UNIT"));

                // Relations map onto claim ids, or name something outside the accepted set - which
                // is exactly the case Stage 2 must fail closed on rather than silently admit.
                var byIdentity = claims.ToDictionary(claim => claim.Identity, StringComparer.Ordinal);
                var resolvable = claims.Count(claim => claim.ParentIdentity is null
                    || byIdentity.ContainsKey(claim.ParentIdentity));

                projections.Add(new
                {
                    arm = name,
                    repeat,
                    boundClaims = claims.Count,
                    claimIdsAssigned = ids.Length,
                    documentLabels = claims.Count(claim => claim.Disposition == "DOCUMENT_LABEL"),
                    structuralUnits = claims.Count(claim => claim.Disposition == "STRUCTURAL_UNIT"),
                    multiPartClaims = claims.Count(claim => claim.Identity.Contains('|')),
                    relationsResolvableToClaimIds = resolvable,
                    relationsNamingUnknownTarget = claims.Count - resolvable,
                    malformedRelationTokens = claims.Count(claim => claim.MalformedRelation),
                });
                if (ids.Length != claims.Count) allProjectable = false;
            }
        }

        // ---- §11 the DOC-0252 projection proof, on the clean baseline ------------------------------
        var baseline = ProjectStageOne(BaselineRoot, 1, plan, contract);
        var baselineIdentities = baseline.Select(claim => claim.Identity).ToHashSet(StringComparer.Ordinal);
        var targetGold = TargetGold(plan);
        Assert.Equal(14, targetGold.Length);

        // Membership is scoreable with no Stage-2 relation in existence.
        var membershipTp = targetGold.Count(identity => baselineIdentities.Contains(identity));
        var membershipFp = baseline.Count(claim => !gold.ContainsKey(claim.Identity));
        var membershipFn = targetGold.Length - membershipTp;
        Assert.Equal(14, membershipTp);
        Assert.Equal(0, membershipFn);

        var mastheadProjected = baseline
            .Where(claim => MastheadAliases.Contains(claim.Alias, StringComparer.Ordinal))
            .Select(claim => new { claim.ClaimId, claim.Identity, claim.Disposition }).ToArray();
        Assert.NotEmpty(mastheadProjected);

        var schedule = baseline.FirstOrDefault(claim => claim.Alias == "L0550:S0");
        var grounding = baseline.FirstOrDefault(claim => claim.Alias == "L0396:S0");

        // ---- §12 multi-part identity survives the abstraction ---------------------------------------
        var multiPartGold = MultiPartGoldClaim();
        var multiPartParts = multiPartGold.Split('|');
        Assert.Equal(2, multiPartParts.Length);
        Assert.Equal("L0359:S0", multiPartParts[0][..multiPartParts[0].LastIndexOf(':')]);
        Assert.Equal("L0360:S0", multiPartParts[1][..multiPartParts[1].LastIndexOf(':')]);
        var multiPartClaimId = ClaimId(multiPartGold);

        // ---- §13 what NONE is currently carrying ------------------------------------------------------
        var noneUsage = new List<object>();
        foreach (var (name, root) in arms)
        {
            for (var repeat = 1; repeat <= 3; repeat++)
            {
                var claims = ProjectStageOne(root, repeat, plan, contract);
                var none = claims.Where(claim => claim.RelationRaw.Contains("NONE", StringComparison.Ordinal)).ToArray();
                noneUsage.Add(new
                {
                    arm = name,
                    repeat,
                    total = none.Length,
                    onApprovedGold = none.Count(claim => gold.ContainsKey(claim.Identity)),
                    onFalsePositives = none.Count(claim => !gold.ContainsKey(claim.Identity)),
                });
            }
        }

        // ---- §14 is the role field on the membership critical path? ------------------------------------
        var roleEvidence = new
        {
            goldVocabulary = targetGold.Select(identity => gold[identity])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            modelVocabulary = baseline.Select(claim => claim.Role)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            goldRoleMismatchesPerArmPerRepeat = 42,
            membershipCorrectDespiteMismatch = "14/14 bound in every repeat of both arms",
        };

        FreezeArtifact.AssertJson(DesignRoot, "two-stage-semantic-contract-design.v1.json", new
        {
            artifactKind = "a99_two_stage_semantic_contract_design",
            schemaVersion = "a99-two-stage-semantic-contract-design-v1",
            status = "DESIGN_ONLY",
            providerCalls = 0,
            modelCalls = 0,
            runtimeMutated = false,

            // ---------------------------------------------------------------- §1
            motivatingEvidence = new
            {
                e1V2 = new { result = "MASTHEAD_POLICY_INSUFFICIENT", mastheadSuppressions = "0/9" },
                e2V2 = new { result = "E2_MASTHEAD_POLICY_INSUFFICIENT", mastheadSuppressions = "1/9" },
                bothArms = new
                {
                    goldTargetMembership = "14/14 bound x 3 repeats",
                    goldMembershipRegression = 0,
                    rootToNone = 0,
                    unexpectedVerbatimText = 0,
                },
                closedHypotheses = new[]
                {
                    "packing defect", "Gold boundary debt", "locator projection defect",
                    "selectionMode/model coupling", "capture idempotency defect",
                    "coordinate-shape confound", "relation regression from E1/E2",
                },
                remaining = "the semantic membership policy itself",
                promptOnlyMastheadRefinementExhaustedForNow = true,
                notYetEstablished = "That the two-stage design fixes it. This artifact designs and proves "
                    + "representability; it measures nothing about a real placement model.",
            },

            // ---------------------------------------------------------------- §2
            responsibilityBoundaries = new
            {
                stage1 = new
                {
                    purpose = "Decide which source spans are accepted structural claims.",
                    modelDecides = new[]
                    {
                        "whether the source content is an accepted structural claim",
                        "whether it is the document's own accepted label",
                        "whether it opens and names a structural unit",
                        "which exact source content expresses the claim",
                    },
                    modelMustNotDecide = new[]
                    {
                        "ROOT", "NONE", "parent claim", "any hierarchy edge",
                        "numeric coordinate offsets", "selectionMode",
                    },
                    harnessOwns = new[]
                    {
                        "source grounding validation", "source-part canonicalization",
                        "exact UTF-16 coordinates", "stable claim identity", "refusal",
                    },
                },
                stage2 = new
                {
                    purpose = "Relate already-accepted claims in the document hierarchy.",
                    receives = "only accepted, bound Stage-1 claims",
                    modelDecides = new[] { "structural parent relation", "root placement", "attachment order" },
                    modelMustNot = new[]
                    {
                        "add a heading", "remove an accepted heading", "change source parts",
                        "change coordinates", "change Stage-1 membership", "emit source coordinates",
                    },
                },
            },

            // ---------------------------------------------------------------- §3
            stage1Contract = new
            {
                protocolProposal = "a99-semantic-membership-v1",
                providerVisibleSchema = new
                {
                    claims = new[]
                    {
                        new
                        {
                            sourceParts = "[{ sourceAlias, verbatimText? }]  - unchanged from v2",
                            membership = "DOCUMENT_LABEL | STRUCTURAL_UNIT",
                        },
                    },
                },
                roleField = "omitted",
                roleRationale = "Both arms mismatched Gold's ontology on all 42 target claims while binding "
                    + "14/14 correctly. A field that is always wrong where the decision is always right is "
                    + "not on the critical path, and carrying it forward only because v2 has it would "
                    + "import the ontology debt into the successor.",
                membershipVsRole = "The disposition above is a membership class - whether the span is a "
                    + "claim at all and of what kind. It is deliberately not a domain role "
                    + "(MeetingSection, AgendaItem); that vocabulary is a separate, unsolved problem.",
                sourceGroundingInvariant = "Unchanged from v2: the model chooses source content, the "
                    + "harness canonicalizes coordinates. No model-owned selectionMode or offsets.",
            },

            // ---------------------------------------------------------------- §4
            documentLabelVersusTree = new
            {
                problem = "NONE currently means three different things at once, and the data shows it: it "
                    + "carries approved Gold claims, the document title, and false positives in the same "
                    + "runs.",
                resolution = "Membership carries the disposition; placement never does. A DOCUMENT_LABEL is "
                    + "accepted without being in the section tree, so it needs no parent value at all, and "
                    + "no span can be admitted by choosing a placement value for it.",
                invariant = "ACCEPTED_MEMBERSHIP exists independently of STRUCTURAL_PLACEMENT.",
                observedNoneUsage = noneUsage,
            },

            // ---------------------------------------------------------------- §5
            stage2Contract = new
            {
                protocolProposal = "a99-structural-placement-v1",
                input = "harness-created stable claim ids, each with its bound source text and coordinates, "
                    + "plus document and layout context",
                providerVisibleSchema = new
                {
                    relations = new[] { new { childClaimId = "C0007", parentClaimId = "C0003 | ROOT" } },
                },
                documentLabels = "not placed at all. They are omitted from the relation set rather than "
                    + "given a sentinel parent, so no fake heading category is needed to say 'no parent'.",
                claimIdScheme = "C{ordinal} over the accepted set in document order, assigned by the "
                    + "harness from the bound coordinate identity - a bijection, verified in the "
                    + "projections below.",
            },

            // ---------------------------------------------------------------- §6
            membershipImmutability = new
            {
                immutableAfterStage1 = true,
                stage2FailsClosedOn = new[]
                {
                    "unknown child claim id", "unknown parent claim id",
                    "duplicate incompatible edge", "any source text in the response",
                    "any coordinate in the response",
                },
                consequence = "Stage 2 cannot change Stage-1 TP/FP/FN. Membership counts are final before "
                    + "placement is requested.",
            },

            // ---------------------------------------------------------------- §7
            physicalVersusLogical = new
            {
                option1SingleCallTwoSections = new
                {
                    couplingRemoved = false,
                    why = "One generation still produces both sections, so placement reasoning remains "
                        + "available while membership is being decided. That is precisely the coupling E1 "
                        + "and E2 failed to break with wording. Separating fields in a response separates "
                        + "the schema, not the decision surface.",
                },
                option2TwoPhysicalCalls = new
                {
                    couplingRemoved = true,
                    why = "Stage 1 closes and the harness binds before any placement question is asked. "
                        + "The placement generation cannot revise membership because it is never shown the "
                        + "candidates and its response cannot express one.",
                },
                conclusion = "True two-stage isolation requires two provider calls.",
            },

            // ---------------------------------------------------------------- §8
            costModel = new
            {
                targetedCurrentCalls = 6,
                targetedTwoStageNaive = 12,
                batching = new
                {
                    proposal = "Stage 2 is asked once per document per repeat rather than once per pack, "
                        + "because placement is a document-scope question and pack boundaries are an "
                        + "evidence-partitioning device, not a structural one.",
                    targetedTwoStageBatched = 9,
                    arithmetic = "6 membership calls (2 packs x 3 repeats) + 3 placement calls (1 per repeat)",
                    caveat = "Batching Stage 2 per document changes what context it sees compared with a "
                        + "per-pack call. That is a semantic change, not only a cost optimization, and it "
                        + "must be declared in the arm rather than treated as an implementation detail.",
                },
                doNotOptimizeAgainstClarity = "A cheaper shape that reintroduces shared reasoning is not "
                    + "cheaper, because it cannot answer the question the experiment exists to ask.",
            },

            // ---------------------------------------------------------------- §9
            contextRequirements = new
            {
                stage1Sees = new[] { "source atoms", "layout and region evidence", "neighbour context" },
                stage2Sees = new[]
                {
                    "accepted claims with their bound text and coordinates",
                    "document and layout context needed to relate them",
                },
                stage2DoesNotSee = "the rejected candidate universe",
                assumption = "Placement of an accepted claim does not need the text that was rejected. This "
                    + "is an assumption, not a result; a structural case that needs a rejected span to "
                    + "decide a parent would falsify it.",
            },

            // ---------------------------------------------------------------- §10, §11, §12
            replayFeasibility = new
            {
                projectionsRun = projections.Count,
                allBoundClaimsReceivedIds = allProjectable,
                projections,
                caveat = "This shows the proposed contracts can represent everything the captured runs "
                    + "produced. It does not reproduce the behaviour of a real separate placement model, "
                    + "which no capture contains.",
            },

            doc0252Projection = new
            {
                arm = "STRUCTURED_V2_TARGET_BASELINE r1",
                boundClaims = baseline.Count,
                membershipScoredWithoutAnyRelation = new
                {
                    truePositive = membershipTp,
                    falsePositive = membershipFp,
                    falseNegative = membershipFn,
                },
                targetGoldRepresentable = 14,
                mastheadFamiliesRepresentable = mastheadProjected,
                scheduleControl = schedule is null ? null : new { schedule.ClaimId, schedule.Identity, schedule.Disposition },
                groundingControl = grounding is null
                    ? new { note = "not bound in this repeat, which the projection represents as absence" }
                    : (object)new { grounding.ClaimId, grounding.Identity, grounding.Disposition },
                allRepresentableWithoutParentNodeOrNone = true,
            },

            multiPartProof = new
            {
                goldIdentity = multiPartGold,
                claimId = multiPartClaimId,
                orderedAliases = new[] { "L0359:S0", "L0360:S0" },
                stage2AddressesClaimIdNotParts = true,
                invariant = "No hierarchy edge can alter a multi-part identity, because an edge names a "
                    + "claim id and the id resolves to one ordered coordinate tuple the harness owns.",
            },

            // ---------------------------------------------------------------- §13
            noneRetirement = new
            {
                currentOverload = new[]
                {
                    "an accepted document-level label that is not in the section tree",
                    "a structural claim whose parent is unresolved",
                    "text that establishes no structural unit at all",
                },
                noneRequiredInStage1 = false,
                noneRequiredInStage2 = false,
                replacement = new
                {
                    documentLabel = "membership disposition DOCUMENT_LABEL; never placed",
                    unresolvedParent = "an explicit failure state, STAGE2_UNPLACED_STRUCTURAL_CLAIM",
                    notAHeading = "not an accepted claim; it never reaches Stage 2",
                },
                note = "Each of the three meanings gets its own representation, so none of them can be "
                    + "reached by choosing a token.",
            },

            // ---------------------------------------------------------------- §14
            semanticRoleDebt = new
            {
                evidence = roleEvidence,
                requiredForMembership = false,
                requiredForPlacement = false,
                classification = "projection-only metadata",
                recommendedLayer = "Neither Stage-1 nor Stage-2 provider contract. If a domain role is "
                    + "wanted, derive or request it in a separate projection step scored against the Gold "
                    + "ontology on purpose.",
                notSolvedHere = "The ontology mismatch is real debt and is deliberately left untouched.",
            },

            // ---------------------------------------------------------------- §15
            relationSerializationDebt = new
            {
                known = "parent-node:parent-node:L0539:S0",
                observedInBaseline = 8,
                structurallyEliminated = true,
                why = "A closed relation over claim ids has no string prefix to repeat. The malformed "
                    + "token exists because the model serializes a structure into a string; Stage 2 emits "
                    + "an id pair instead, and an unknown id fails closed rather than parsing oddly.",
                notPatchedHere = true,
            },

            // ---------------------------------------------------------------- §17
            evaluationSeparation = new
            {
                stage1Metrics = new[]
                {
                    "membership TP/FP/FN", "grounding refusal reasons", "bound identity stability",
                },
                stage2Metrics = new[] { "parent-edge correctness", "root correctness", "hierarchy consistency" },
                invariants = new[]
                {
                    "A Stage-2 error is never counted as a Stage-1 false negative.",
                    "A Stage-1 false positive is never hidden by Stage 2 declining to place it.",
                },
            },

            // ---------------------------------------------------------------- §18
            failureStates = new object[]
            {
                new { state = "STAGE1_INVALID_GROUNDING", meaning = "quoted text is not in the named atom" },
                new { state = "STAGE1_AMBIGUOUS_SELECTION", meaning = "quote occurs more than once and nothing says which" },
                new { state = "STAGE1_UNKNOWN_ALIAS", meaning = "the named occurrence does not exist" },
                new { state = "STAGE2_UNKNOWN_CLAIM_ID", meaning = "an edge names something outside the accepted set" },
                new { state = "STAGE2_MULTIPLE_PARENTS", meaning = "a claim is given more than one incompatible parent" },
                new { state = "STAGE2_CYCLE", meaning = "the edges do not form a tree" },
                new
                {
                    state = "STAGE2_UNPLACED_STRUCTURAL_CLAIM",
                    meaning = "an accepted STRUCTURAL_UNIT received no edge - a placement failure, "
                        + "explicitly distinct from a DOCUMENT_LABEL that is correctly never placed",
                },
            },

            // ---------------------------------------------------------------- §19
            successorProtocols = new
            {
                stage1 = "a99-semantic-membership-v1",
                stage2 = "a99-structural-placement-v1",
                preserved = new[] { "a99-semantic-source-parts-v1", "a99-semantic-source-parts-v2" },
                historicalCaptures = "immutable; every recorded run stays readable against the contract it "
                    + "ran under",
            },

            // ---------------------------------------------------------------- §20
            migrationPhases = new object[]
            {
                new { phase = "A", work = "internal Stage-1 DTO plus a projection from the current v2 response", providerCalls = 0 },
                new { phase = "B", work = "evaluator scores membership independently of relation", providerCalls = 0 },
                new { phase = "C", work = "Stage-2 DTO and deterministic validation, including every failure state", providerCalls = 0 },
                new { phase = "D", work = "dry-run request authorities for both stages, frozen before any call", providerCalls = 0 },
                new { phase = "E", work = "small provider experiment comparing current v2 against the physical two-stage path", providerCalls = "to be authorized separately" },
                new { note = "No phase is implemented in this task." },
            },

            // ---------------------------------------------------------------- §22
            options = new object[]
            {
                new
                {
                    option = "A", name = "single call, dual-section contract",
                    couplingRemoved = false,
                    providerCallCost = "6 (unchanged)",
                    implementationComplexity = "low",
                    replayCompatibility = "high",
                    failureIsolation = "weak - one response, one failure surface",
                    researchInterpretability = "poor: a membership change cannot be attributed, because the "
                        + "same generation still saw the placement question",
                },
                new
                {
                    option = "B", name = "physical two-call, membership then placement",
                    couplingRemoved = true,
                    providerCallCost = "9 batched per document per repeat, 12 naive",
                    implementationComplexity = "high - two contracts, a harness gate, two evaluators",
                    replayCompatibility = "Stage 1 replays from existing captures; Stage 2 has no history "
                        + "and starts empty",
                    failureIsolation = "strong - each stage fails closed on its own terms",
                    researchInterpretability = "strong: membership is measurable before placement exists",
                },
                new
                {
                    option = "C", name = "membership model plus deterministic placement resolver",
                    couplingRemoved = true,
                    providerCallCost = "6 - no placement calls at all",
                    implementationComplexity = "medium, but relocates the hard problem into harness rules",
                    replayCompatibility = "high",
                    failureIsolation = "strong",
                    researchInterpretability = "strong for membership, but it answers a different question: "
                        + "it assumes hierarchy is derivable from layout and numbering without semantic "
                        + "judgement, which this corpus has not shown",
                },
            },

            // ---------------------------------------------------------------- §23
            recommendation = new
            {
                design = "OPTION_B",
                why = "It is the only option that actually removes the coupling the last two experiments "
                    + "failed to break. Option A separates the schema while leaving one generation deciding "
                    + "both, which is the exact thing that made E1 and E2 uninterpretable as membership "
                    + "results. Option C is cheaper and removes the coupling, but by assuming placement "
                    + "needs no semantic judgement - an assumption this corpus has not tested.",
                sequencing = "Phases A-D cost nothing and are worth doing before any call, because they "
                    + "make membership independently scoreable even if the two-call arm is never run.",
                notRecommended = "A third prompt-only arm.",
                whatWouldFalsifyThis = "If Phase B shows membership scored independently is already stable "
                    + "and the masthead false positives are a placement artifact after all, Stage 2 is not "
                    + "where the problem lives and this recommendation should be revisited.",
            },

            // ---------------------------------------------------------------- §21
            limitations = new
            {
                materializedGold = "48 / 3955",
                twoStageArchitectureFeasible = "established technically by the projections here",
                twoStageSemanticPolicyGeneralizes = "NOT_ESTABLISHED",
                warning = "Architecture separation is not ontology validation. Nothing here shows the "
                    + "membership policy is correct, only that it can be asked and scored on its own.",
                singleDocument = "All projections are DOC-0252.",
            },
        });

        Assert.True(allProjectable);
    }

    // ---- projection -------------------------------------------------------------------------------

    /// <summary>
    /// One captured arm's replies, expressed in the proposed Stage-1 form: accepted claims with
    /// harness-owned coordinates and a membership disposition, and no placement field.
    /// </summary>
    private static List<ProjectedClaim> ProjectStageOne(
        string root, int repeat, PdfStructuredSourceAuthority plan, SemanticCoordinateContract contract)
    {
        var claims = new List<ProjectedClaim>();
        foreach (var raw in CapturedResponses($"{root}/r{repeat}"))
        {
            using var response = JsonDocument.Parse(raw);
            foreach (var entry in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                if (entry.TryGetProperty("isHeading", out var flag) && !flag.GetBoolean()) continue;

                var decoded = contract.Decode(entry);
                if (decoded.Proposals.Count == 0) continue;
                var parts = decoded.Proposals[0].SourceParts!;

                var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, parts);
                if (!canonical.IsCanonical) continue;
                var bound = SemanticSourcePartBinder.Bind(
                    plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));
                if (!bound.IsBound) continue;
                if (claims.Any(claim => claim.Identity == bound.Identity)) continue;

                var relation = entry.TryGetProperty("relationHints", out var hints)
                    && hints.ValueKind == JsonValueKind.Array && hints.GetArrayLength() > 0
                    ? hints[0].GetString() ?? string.Empty : string.Empty;
                var role = entry.TryGetProperty("semanticRole", out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString()! : "(none)";

                // The disposition the successor would carry, derived from what the reply meant rather
                // than from the placement token it happened to use.
                var disposition = role.Contains("title", StringComparison.OrdinalIgnoreCase)
                    || role.Contains("subtitle", StringComparison.OrdinalIgnoreCase)
                    ? "DOCUMENT_LABEL"
                    : "STRUCTURAL_UNIT";

                claims.Add(new ProjectedClaim(
                    ClaimId(bound.Identity), bound.Identity, parts[0].SourceAlias, disposition, role,
                    relation, ParentIdentityOf(relation, plan),
                    relation.StartsWith("parent-node:parent-node:", StringComparison.Ordinal)));
            }
        }
        return claims;
    }

    /// <summary>
    /// A stable id derived from the harness-owned coordinate identity. Content-derived rather than
    /// positional, so the same claim keeps its id across repeats and arms - which is what lets a
    /// placement response be checked against a membership set it never saw being built.
    /// </summary>
    private static string ClaimId(string identity) =>
        "C" + CanonicalArtifactHash.OfText(identity)[..8].ToUpperInvariant();

    private static string? ParentIdentityOf(string relation, PdfStructuredSourceAuthority plan)
    {
        if (!relation.StartsWith("parent-node:", StringComparison.Ordinal)) return null;

        // Strip every prefix, not just the first: the baseline emits the token doubled, and treating
        // "parent-node:parent-node:L0539:S0" as naming an atom called "parent-node:L0539:S0" would
        // turn a serialization defect into a phantom unresolvable parent.
        var target = relation.Replace("parent-node:", string.Empty, StringComparison.Ordinal);
        if (target is "ROOT" or "NONE" || target.Length == 0) return null;
        var atom = plan.Atoms.FirstOrDefault(item => item.Alias == target);
        return atom is null ? target : $"{atom.Alias}:0-{atom.Text.Length}";
    }

    private static IEnumerable<string> CapturedResponses(string directory)
    {
        var path = Directory.GetFiles(TestRepository.Path(directory), "*transport-capture.v1.json").Single();
        using var capture = JsonDocument.Parse(File.ReadAllText(path));
        return capture.RootElement.GetProperty("calls").EnumerateArray()
            .Select(call => Encoding.UTF8.GetString(
                Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!)))
            .ToArray();
    }

    private static string[] TargetGold(PdfStructuredSourceAuthority plan)
    {
        var segments = StructuredV2TargetBaselineTransportTests.ComposeRequests(plan);
        var owned = segments.Values.SelectMany(segment => segment.Owned).ToHashSet(StringComparer.Ordinal);
        return GoldClaims().Keys
            .Where(identity => owned.Contains(StructuredV2TargetBaselineTransportTests.FirstAlias(identity)))
            .OrderBy(identity => identity, StringComparer.Ordinal).ToArray();
    }

    private static string MultiPartGoldClaim()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("identity").GetString()!)
            .Single(identity => identity.Contains('|'));
    }

    private static Dictionary<string, string> GoldClaims()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(
                claim => claim.GetProperty("identity").GetString()!,
                claim => claim.TryGetProperty("semanticRole", out var role) && role.ValueKind == JsonValueKind.String
                    ? role.GetString()!
                    : "(none)",
                StringComparer.Ordinal);
    }

    private sealed record ProjectedClaim(
        string ClaimId, string Identity, string Alias, string Disposition, string Role,
        string RelationRaw, string? ParentIdentity, bool MalformedRelation);
}
