using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// What the eligibility clause actually caused.
/// <para>
/// E2 states an invariant rather than a category: heading eligibility is decided before placement,
/// and parent-node:NONE cannot be the reason a span is admitted. Against the same baseline, on the
/// same atoms, packs, Gold, model and coordinate contract, one appended clause is the only thing
/// between them.
/// </para>
/// <para>
/// Both arms are replayed here from their raw captured bytes - validate, decode, canonicalize, bind
/// - rather than read out of the score artifacts. A score artifact is a conclusion; comparing two
/// conclusions would inherit whatever either scorer decided, including how it grouped refusals.
/// </para>
/// </summary>
public sealed class MastheadE2V2CausalDiffTests
{
    private const string BaselineRoot = "eval/a99-closed-loop/structured-v2-target-baseline-v1/DOC-0252";
    private const string ArmRoot = "eval/a99-closed-loop/exp-masthead-e2-v2-experiment-v1/DOC-0252";
    private const string DiffRoot = "eval/a99-closed-loop/masthead-e2-v2-causal-diff-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string V2ContractSha256 = "565bdc87749a1ce1246238cacd1eb7d550a19939dc9e42d23e76ab3a46a8b0ea";
    private const string BaselinePromptSha256 =
        "6340d1daf507a3d2bf5ce6fbef2b5d7b61735c0e0621a62f8e885ad8b2d8a66e";
    private const string ArmPromptSha256 =
        "93cead4cb4e8d3dee5018b789a1d95a2807b69bfa81ac5f85726de3aba0682ed";

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    /// <summary>
    /// The three masthead families, named by the atoms they occupy. Comparing families rather than
    /// exact tuples matters: a claim that splits into two identities has not become a new kind of
    /// error, and counting it as one would manufacture a regression out of a formatting difference.
    /// </summary>
    private static readonly (string Family, string[] Aliases)[] MastheadFamilies =
    [
        ("international-program-and-tag", ["L0513:S0", "L0514:S0"]),
        ("hybrid-meeting", ["L0515:S0"]),
        ("date-venue-address", ["L0516:S0", "L0517:S0", "L0518:S0"]),
    ];

    private static readonly string[] RelationControls = ["L0400:S0", "L0420:S0", "L0470:S0", "L0507:S0"];
    private const string ScheduleControl = "L0550:S0";
    private const string GroundingControl = "L0396:S0";

    [Fact]
    public void Compare_the_e2_eligibility_arm_against_the_v2_baseline()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var contract = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        Assert.Equal(V2ContractSha256, contract.SchemaHash());
        Assert.NotEqual(BaselinePromptSha256, ArmPromptSha256);   // the one intended delta

        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var segments = StructuredV2TargetBaselineTransportTests.ComposeRequests(plan);
        var owned = segments.Values.SelectMany(segment => segment.Owned).ToHashSet(StringComparer.Ordinal);
        var gold = GoldClaims();
        var targetGold = gold.Keys
            .Where(identity => owned.Contains(StructuredV2TargetBaselineTransportTests.FirstAlias(identity)))
            .OrderBy(identity => identity, StringComparer.Ordinal).ToArray();
        Assert.Equal(14, targetGold.Length);

        var baseline = Replay(BaselineRoot, plan, contract, gold, owned);
        var arm = Replay(ArmRoot, plan, contract, gold, owned);

        // §1 both arms reach the coordinate layer cleanly.
        Assert.Equal(0, baseline.Sum(repeat => repeat.UnexpectedVerbatimText));
        Assert.Equal(0, arm.Sum(repeat => repeat.UnexpectedVerbatimText));

        // ---- §3 membership and binding delta over the 14 target claims ---------------------------
        var membershipRows = new List<object>();
        var membershipDelta = 0;
        var bindingDelta = 0;
        foreach (var identity in targetGold)
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var a = baseline[repeat].OutcomeOf(identity);
                var b = arm[repeat].OutcomeOf(identity);
                if (a == b) continue;
                membershipDelta++;
                if (a == "BOUND" || b == "BOUND") bindingDelta++;
                membershipRows.Add(new { identity, repeat = repeat + 1, baseline = a, armV2 = b });
            }
        }

        // ---- §4 masthead families ------------------------------------------------------------------
        var familyRows = new List<object>();
        var suppressions = 0;
        var persistences = 0;
        foreach (var (family, aliases) in MastheadFamilies)
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var a = baseline[repeat].FamilyPresent(aliases);
                var b = arm[repeat].FamilyPresent(aliases);
                if (a && !b) suppressions++;
                if (a && b) persistences++;
                familyRows.Add(new
                {
                    family,
                    repeat = repeat + 1,
                    baseline = a ? "PRESENT" : "ABSENT",
                    armV2 = b ? "PRESENT" : "ABSENT",
                    baselineIdentities = baseline[repeat].FamilyIdentities(aliases),
                    armIdentities = arm[repeat].FamilyIdentities(aliases),
                });
            }
        }

        // ---- §5 false-positive delta, exact and by family --------------------------------------------
        var fpRows = new List<object>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var a = baseline[repeat].FalsePositives;
            var b = arm[repeat].FalsePositives;
            fpRows.Add(new
            {
                repeat = repeat + 1,
                baselineCount = a.Count,
                armCount = b.Count,
                baselineOnlyExact = a.Except(b, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal),
                armOnlyExact = b.Except(a, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal),
                commonExact = a.Intersect(b, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal),
                baselineOnlyByAtom = AtomSet(a).Except(AtomSet(b), StringComparer.Ordinal).Order(StringComparer.Ordinal),
                armOnlyByAtom = AtomSet(b).Except(AtomSet(a), StringComparer.Ordinal).Order(StringComparer.Ordinal),
            });
        }

        // ---- §6 relation controls -------------------------------------------------------------------
        var relationRows = new List<object>();
        var rootToNone = 0;
        foreach (var alias in RelationControls)
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var a = baseline[repeat].ClaimOn(alias);
                var b = arm[repeat].ClaimOn(alias);
                var roleChanged = a?.Role != b?.Role;
                var relationChanged = a?.Relation != b?.Relation;
                var membershipChanged = (a is null) != (b is null) || a?.Outcome != b?.Outcome;
                var relationClass = membershipChanged ? "MEMBERSHIP"
                    : roleChanged && relationChanged ? "ROLE_AND_RELATION"
                    : roleChanged ? "ROLE_ONLY"
                    : relationChanged ? "RELATION_ONLY" : "UNCHANGED";
                if (IsRoot(a?.Relation) && IsNone(b?.Relation)) rootToNone++;
                relationRows.Add(new
                {
                    alias,
                    repeat = repeat + 1,
                    baseline = new { outcome = a?.Outcome, role = a?.Role, relation = a?.Relation },
                    armV2 = new { outcome = b?.Outcome, role = b?.Role, relation = b?.Relation },
                    classification = relationClass,
                });
            }
        }

        // ---- §7 attribution of every role mismatch on a Gold claim -----------------------------------
        var roleRows = new List<object>();
        var preexisting = 0;
        var introduced = 0;
        var changedWrong = 0;
        var corrected = 0;
        var armMismatches = 0;
        foreach (var identity in targetGold)
        {
            var goldRole = gold[identity];
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var a = baseline[repeat].ClaimOfIdentity(identity)?.Role;
                var b = arm[repeat].ClaimOfIdentity(identity)?.Role;
                var aOk = RoleMatches(goldRole, a);
                var bOk = RoleMatches(goldRole, b);
                if (!bOk) armMismatches++;

                var bucket = (aOk, bOk) switch
                {
                    (false, false) when string.Equals(a, b, StringComparison.Ordinal) => "PREEXISTING_IN_BASELINE",
                    (false, false) => "CHANGED_TO_DIFFERENT_WRONG_ROLE",
                    (true, false) => "INTRODUCED_BY_E1",
                    (false, true) => "E1_CORRECTED_BASELINE_ROLE",
                    _ => "BOTH_MATCH",
                };
                switch (bucket)
                {
                    case "PREEXISTING_IN_BASELINE": preexisting++; break;
                    case "CHANGED_TO_DIFFERENT_WRONG_ROLE": changedWrong++; break;
                    case "INTRODUCED_BY_E1": introduced++; break;
                    case "E1_CORRECTED_BASELINE_ROLE": corrected++; break;
                }
                if (bucket == "BOTH_MATCH") continue;
                roleRows.Add(new
                {
                    identity, repeat = repeat + 1, goldRole, baselineRole = a, armRole = b, bucket,
                });
            }
        }

        // ---- §8 NONE delta -----------------------------------------------------------------------------
        var noneRows = new List<object>();
        var newNone = new List<object>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var a = baseline[repeat].NoneIdentities();
            var b = arm[repeat].NoneIdentities();
            var added = b.Except(a, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            newNone.AddRange(added.Select(identity => new
            {
                repeat = repeat + 1, identity, isGold = gold.ContainsKey(identity),
            }));
            noneRows.Add(new
            {
                repeat = repeat + 1,
                baselineTotal = a.Count, baselineGold = a.Count(gold.ContainsKey), baselineFp = a.Count(x => !gold.ContainsKey(x)),
                armTotal = b.Count, armGold = b.Count(gold.ContainsKey), armFp = b.Count(x => !gold.ContainsKey(x)),
                added,
                removed = a.Except(b, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal),
            });
        }

        // ---- §9 the source-grounding control -------------------------------------------------------------
        var groundingRows = new List<object>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            groundingRows.Add(new
            {
                repeat = repeat + 1,
                baseline = Grounding(baseline[repeat], plan),
                armV2 = Grounding(arm[repeat], plan),
            });
        }
        var armGroundingRefusals = arm.Sum(repeat => repeat.Claims
            .Count(claim => claim.Alias == GroundingControl && claim.Outcome == "REFUSED_ON_SHAPE"));
        var baselineGroundingRefusals = baseline.Sum(repeat => repeat.Claims
            .Count(claim => claim.Alias == GroundingControl && claim.Outcome == "REFUSED_ON_SHAPE"));

        // Does grounding failure reach anything but this one control?
        var groundingElsewhere = arm.SelectMany(repeat => repeat.Claims)
            .Where(claim => claim.Outcome == "REFUSED_ON_SHAPE"
                && claim.Reason == nameof(SemanticSourcePartsStatus.TextNotInAtom)
                && claim.Alias != GroundingControl)
            .Select(claim => claim.Alias).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        // ---- §10 schedule control ------------------------------------------------------------------------
        var scheduleRows = Enumerable.Range(0, 3).Select(repeat => new
        {
            repeat = repeat + 1,
            baseline = Describe(baseline[repeat].ClaimOn(ScheduleControl)),
            armV2 = Describe(arm[repeat].ClaimOn(ScheduleControl)),
        }).ToArray();
        var scheduleDelta = Enumerable.Range(0, 3).Count(repeat =>
            !Equal(baseline[repeat].ClaimOn(ScheduleControl), arm[repeat].ClaimOn(ScheduleControl)));

        // ---- §11 variance ----------------------------------------------------------------------------------
        var variance = new
        {
            baseline = Variance(baseline),
            armV2 = Variance(arm),
            note = "Raw JSON hash variance is not semantic instability: identical claims differ in "
                + "whitespace and ordering. Bound-identity and family variance are the semantic measures.",
        };

        // ---- a defect the comparison exposed in both arms --------------------------------------------
        var malformed = new
        {
            baseline = baseline.Sum(repeat => repeat.Claims.Count(claim => Malformed(claim.Relation))),
            armV2 = arm.Sum(repeat => repeat.Claims.Count(claim => Malformed(claim.Relation))),
            examples = baseline.Concat(arm).SelectMany(repeat => repeat.Claims)
                .Where(claim => Malformed(claim.Relation))
                .Select(claim => claim.Relation).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            note = "A relation hint carrying its own prefix twice - 8 in the baseline, none in the arm. "
                + "It is the relation field repeating the shape problem the coordinate field has just "
                + "been relieved of: the model serializing a structure the harness should own. Its "
                + "absence here is not evidence that the clause fixes it, because nothing in the clause "
                + "addresses relations and three repeats cannot separate that from chance.",
        };

        // ---- §12 classification ------------------------------------------------------------------------------
        // Attribution is narrow on purpose. Gold states roles in a domain ontology (MeetingSection,
        // AgendaItem, SectionHeading) and neither arm is asked to emit that vocabulary, so the
        // mismatches exist in both arms. The baseline itself names the same claim
        // "section", "section-heading" and "section_heading" across three repeats. A different wrong
        // word is therefore drift in an unconstrained field, not an effect of the clause; only a role
        // the baseline got right and this arm got wrong can be attributed to it.
        var roleRegression = introduced > 0;
        var classification = suppressions == 0 && membershipDelta == 0 && !roleRegression && rootToNone == 0
            ? "MASTHEAD_POLICY_INSUFFICIENT"
            : suppressions == 0 && (roleRegression || rootToNone > 0)
                ? "MASTHEAD_POLICY_REGRESSIVE"
                : suppressions > 0 && (roleRegression || rootToNone > 0)
                    ? "MIXED"
                    : "MASTHEAD_POLICY_INSUFFICIENT";

        // E1 named the category and E2 stated the invariant; both left the families standing with
        // Gold untouched. A third wording would be the third attempt at the same lever, so the
        // fallback here is no longer more prose.
        var direction = rootToNone > 0 || roleRegression
            ? "INVESTIGATE_OPTION_C_MEMBERSHIP_PLACEMENT_SEPARATION"
            : groundingElsewhere.Length > 0
                ? "INVESTIGATE_SOURCE_GROUNDING_CONTRACT"
                : "DESIGN_TWO_STAGE_DECISION_CONTRACT";

        FreezeArtifact.AssertJson(DiffRoot, "masthead-e2-v2-causal-diff.v1.json", new
        {
            artifactKind = "a99_masthead_e2_v2_causal_diff",
            schemaVersion = "a99-masthead-e2-v2-causal-diff-v1",
            providerCalls = 0,
            modelCalls = 0,
            scoredFrom = "raw transport captures of both arms, replayed independently",

            lineage = new
            {
                baseline = new
                {
                    id = "STRUCTURED_V2_TARGET_BASELINE",
                    promptSha256 = BaselinePromptSha256,
                    root = BaselineRoot,
                },
                armV2 = new
                {
                    id = "EXP_MASTHEAD_METADATA_E2_V2",
                    promptSha256 = ArmPromptSha256,
                    root = ArmRoot,
                },
                shared = new
                {
                    protocolVersion = contract.ProtocolVersion,
                    contractSha256 = V2ContractSha256,
                    goldSha256 = GoldSha256,
                    packingPolicy = "COHERENT_REGION_SEGMENTATION_V1",
                    targetPacks = TargetPacks,
                    targetGoldCount = targetGold.Length,
                    repeats = 3,
                    model = "qwen/qwen3.7-flash",
                },
                intendedDelta = "the membership-before-placement clause, and nothing else",
            },

            integrity = new
            {
                unexpectedVerbatimTextBaseline = 0,
                unexpectedVerbatimTextArm = 0,
                note = "The coordinate confound that invalidated the v1 comparison is absent from both arms.",
            },

            membershipDelta = new
            {
                goldMembershipDeltaCount = membershipDelta,
                goldBindingDeltaCount = bindingDelta,
                rows = membershipRows,
                note = "Every one of the 14 target claims, in every repeat, in both arms.",
            },

            mastheadFamilyDelta = new
            {
                suppressions,
                persistences,
                rows = familyRows,
                note = "Compared as families. A composite claim that splits is the same family, not a new "
                    + "false-positive kind.",
            },

            falsePositiveDelta = fpRows,

            relationControlDelta = new
            {
                rootToNoneReproduced = rootToNone > 0,
                rootToNoneCount = rootToNone,
                rows = relationRows,
            },

            semanticRoleAttribution = new
            {
                armMismatches,
                preexistingInBaseline = preexisting,
                introducedByE1 = introduced,
                changedToDifferentWrongRole = changedWrong,
                e1CorrectedBaselineRole = corrected,
                regressionCausedByE1 = roleRegression,
                rows = roleRows,
                goldVocabulary = targetGold.Select(identity => gold[identity]).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray(),
                modelVocabularyBaseline = baseline.SelectMany(repeat => repeat.Claims).Select(claim => claim.Role)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                modelVocabularyArm = arm.SelectMany(repeat => repeat.Claims).Select(claim => claim.Role)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                interpretation = "The two vocabularies do not intersect. All 42 mismatches are present in "
                    + "both arms, and none is attributable to the clause. This is an unconstrained role "
                    + "field measured against a Gold ontology, and it is a finding of its own - separate "
                    + "from anything the masthead wording does.",
                note = "A mismatch against Gold that the baseline already had is not an effect of the clause. "
                    + "Only a claim the baseline got right and this arm got wrong is attributable to it.",
            },

            noneDelta = new { rows = noneRows, newNoneIdentities = newNone },

            sourceGrounding = new
            {
                control = GroundingControl,
                baselineRefusals = baselineGroundingRefusals,
                armRefusals = armGroundingRefusals,
                rows = groundingRows,
                spreadBeyondControl = groundingElsewhere,
                systematicIncrease = armGroundingRefusals > baselineGroundingRefusals,
                classification = "ALIAS_TEXT_MISALIGNMENT",
                note = "Under v2 this refusal is no longer a selection-mode defect. The model named one atom "
                    + "and quoted text that is not inside it, and the canonicalizer refused rather than "
                    + "widening the claim to the whole atom. That is the intended behaviour, and the "
                    + "canonicalizer is not weakened to make it disappear.",
            },

            scheduleControl = new { alias = ScheduleControl, delta = scheduleDelta, rows = scheduleRows },

            variance,

            malformedRelationHints = malformed,

            classification = new
            {
                final = classification,
                basis = "Masthead families, Gold membership, and attributable role/relation regression. "
                    + "Not the false-positive total, which mixes three mechanisms.",
            },

            recommendedNextDirection = direction,
            directionRationale = "Two interventions on the same lever - a category (E1) and an invariant "
                + "(E2) - each left all three families standing and Gold membership at 14/14. The lever "
                + "itself is the thing in question, so the next step is an explicit decision contract "
                + "rather than a third wording. This is a DOC-0252 result, not a corpus conclusion.",

            providerAccounting = new
            {
                thisAudit = 0,
                baselineCalls = 6,
                armCalls = 6,
                unusedTransferred = 0,
                note = "Each experiment's accounting stays its own.",
            },
        });

        // The audit must not have needed to invent a coordinate outcome to reach its conclusion.
        Assert.Equal(0, baseline.Sum(repeat => repeat.UnexpectedVerbatimText) + arm.Sum(repeat => repeat.UnexpectedVerbatimText));
    }

    // ---- replay ------------------------------------------------------------------------------------

    private static RepeatResult[] Replay(
        string root, PdfStructuredSourceAuthority plan, SemanticCoordinateContract contract,
        IReadOnlyDictionary<string, string> gold, IReadOnlySet<string> owned)
    {
        var results = new List<RepeatResult>();
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var claims = new List<ReplayedClaim>();
            var unexpected = 0;
            var rawHashes = new List<string>();

            foreach (var (packId, raw) in CapturedCalls($"{root}/r{repeat}"))
            {
                rawHashes.Add(CanonicalArtifactHash.OfText(raw));
                using var response = JsonDocument.Parse(raw);
                Assert.Empty(contract.Validate(response.RootElement));

                foreach (var entry in response.RootElement.GetProperty("headings").EnumerateArray())
                {
                    var decoded = contract.Decode(entry);
                    if (decoded.Proposals.Count == 0) continue;
                    var proposal = decoded.Proposals[0];
                    var parts = proposal.SourceParts!;
                    var alias = parts[0].SourceAlias;
                    var role = entry.TryGetProperty("semanticRole", out var value)
                        && value.ValueKind == JsonValueKind.String ? value.GetString()! : "(none)";
                    var relation = entry.TryGetProperty("relationHints", out var hints)
                        && hints.ValueKind == JsonValueKind.Array && hints.GetArrayLength() > 0
                        ? hints[0].GetString() ?? "(none)" : "(none)";
                    var isHeading = !entry.TryGetProperty("isHeading", out var flag) || flag.GetBoolean();
                    var quote = parts[0].VerbatimText;

                    if (!isHeading)
                    {
                        claims.Add(new ReplayedClaim(alias, null, role, relation, "NOT_A_HEADING", null, quote));
                        continue;
                    }
                    if (parts.Any(part => !owned.Contains(part.SourceAlias)))
                    {
                        claims.Add(new ReplayedClaim(alias, null, role, relation, "REFUSED_ON_SHAPE",
                            "OutOfOwnedSegment", quote));
                        continue;
                    }

                    var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, parts);
                    if (!canonical.IsCanonical)
                    {
                        claims.Add(new ReplayedClaim(alias, null, role, relation, "REFUSED_ON_SHAPE",
                            canonical.Status.ToString(), quote));
                        continue;
                    }

                    var bound = SemanticSourcePartBinder.Bind(
                        plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));
                    if (!bound.IsBound)
                    {
                        if (bound.Status == SemanticSourcePartsStatus.UnexpectedVerbatimText) unexpected++;
                        claims.Add(new ReplayedClaim(alias, null, role, relation, "REFUSED_ON_SHAPE",
                            bound.Status.ToString(), quote));
                        continue;
                    }
                    claims.Add(new ReplayedClaim(alias, bound.Identity, role, relation, "BOUND", null, quote));
                }
            }

            results.Add(new RepeatResult(claims, unexpected, rawHashes, gold));
        }

        Assert.Equal(3, results.Count);
        return [.. results];
    }

    private static IEnumerable<(string PackId, string Raw)> CapturedCalls(string directory)
    {
        var path = Directory.GetFiles(TestRepository.Path(directory), "*transport-capture.v1.json").Single();
        using var capture = JsonDocument.Parse(File.ReadAllText(path));
        return capture.RootElement.GetProperty("calls").EnumerateArray()
            .Select(call => (
                call.GetProperty("packId").GetString()!,
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!))))
            .ToArray();
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static bool Malformed(string? relation) =>
        relation is not null && relation.StartsWith("parent-node:parent-node:", StringComparison.Ordinal);

    private static bool IsRoot(string? relation) =>
        relation is not null && relation.Contains("ROOT", StringComparison.Ordinal);

    private static bool IsNone(string? relation) =>
        relation is not null && relation.Contains("NONE", StringComparison.Ordinal);

    private static bool RoleMatches(string goldRole, string? modelRole) =>
        modelRole is not null && string.Equals(Normalize(goldRole), Normalize(modelRole), StringComparison.Ordinal);

    /// <summary>
    /// Roles are compared on their words, not their punctuation: a model writing "section_heading"
    /// and Gold writing "section-heading" is not a semantic disagreement, and counting it as one
    /// would fill the attribution table with spelling.
    /// </summary>
    private static string Normalize(string role) =>
        role.Replace('_', '-').Trim().ToLowerInvariant();

    private static HashSet<string> AtomSet(IReadOnlyCollection<string> identities) =>
        identities.SelectMany(identity => identity.Split('|'))
            .Select(part => part[..part.LastIndexOf(':')])
            .ToHashSet(StringComparer.Ordinal);

    private static object? Describe(ReplayedClaim? claim) => claim is null
        ? null
        : new { claim.Outcome, claim.Identity, claim.Role, claim.Relation, claim.Reason };

    private static bool Equal(ReplayedClaim? a, ReplayedClaim? b) =>
        a?.Outcome == b?.Outcome && a?.Identity == b?.Identity
        && a?.Role == b?.Role && a?.Relation == b?.Relation;

    private static object Grounding(RepeatResult repeat, PdfStructuredSourceAuthority plan)
    {
        var claim = repeat.ClaimOn(GroundingControl);
        if (claim is null) return new { outcome = "NOT_EMITTED" };
        var atom = plan.Atoms.FirstOrDefault(item => item.Alias == GroundingControl);
        var neighbours = claim.Quote is null ? [] : plan.Atoms
            .Where(item => item.Text.Contains(claim.Quote, StringComparison.Ordinal))
            .Select(item => item.Alias).Take(4).ToArray();
        return new
        {
            claim.Outcome,
            claim.Reason,
            claim.Identity,
            claim.Role,
            claim.Relation,
            quotedLength = claim.Quote?.Length,
            quotedPrefix = claim.Quote is null ? null
                : claim.Quote[..Math.Min(80, claim.Quote.Length)],
            atomPrefix = atom is null ? null : atom.Text[..Math.Min(80, atom.Text.Length)],
            atomLength = atom?.Text.Length,
            atomsActuallyContainingQuote = neighbours,
        };
    }

    private static object Variance(RepeatResult[] repeats) => new
    {
        rawResponseHashDistinct = repeats.SelectMany(repeat => repeat.RawHashes)
            .Distinct(StringComparer.Ordinal).Count(),
        proposalCounts = repeats.Select(repeat => repeat.Claims.Count),
        boundIdentitySetsDistinct = repeats
            .Select(repeat => string.Join(",", repeat.BoundIdentities.OrderBy(x => x, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal).Count(),
        roleAssignmentsDistinct = repeats
            .Select(repeat => string.Join(",", repeat.Claims
                .Where(claim => claim.Identity is not null)
                .OrderBy(claim => claim.Identity, StringComparer.Ordinal)
                .Select(claim => $"{claim.Identity}={claim.Role}")))
            .Distinct(StringComparer.Ordinal).Count(),
        relationAssignmentsDistinct = repeats
            .Select(repeat => string.Join(",", repeat.Claims
                .Where(claim => claim.Identity is not null)
                .OrderBy(claim => claim.Identity, StringComparer.Ordinal)
                .Select(claim => $"{claim.Identity}={claim.Relation}")))
            .Distinct(StringComparer.Ordinal).Count(),
    };

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

    private sealed record ReplayedClaim(
        string Alias, string? Identity, string Role, string Relation,
        string Outcome, string? Reason, string? Quote);

    private sealed class RepeatResult(
        IReadOnlyList<ReplayedClaim> claims, int unexpectedVerbatimText,
        IReadOnlyList<string> rawHashes, IReadOnlyDictionary<string, string> gold)
    {
        public IReadOnlyList<ReplayedClaim> Claims { get; } = claims;
        public int UnexpectedVerbatimText { get; } = unexpectedVerbatimText;
        public IReadOnlyList<string> RawHashes { get; } = rawHashes;

        public HashSet<string> BoundIdentities { get; } = claims
            .Where(claim => claim.Identity is not null)
            .Select(claim => claim.Identity!).ToHashSet(StringComparer.Ordinal);

        public List<string> FalsePositives { get; } = claims
            .Where(claim => claim.Identity is not null && !gold.ContainsKey(claim.Identity))
            .Select(claim => claim.Identity!).Distinct(StringComparer.Ordinal).ToList();

        public string OutcomeOf(string identity) =>
            BoundIdentities.Contains(identity) ? "BOUND"
            : Claims.Any(claim => claim.Outcome == "REFUSED_ON_SHAPE"
                && identity.StartsWith(claim.Alias, StringComparison.Ordinal))
                ? "PROPOSED_BUT_REFUSED"
                : "NOT_PROPOSED";

        public ReplayedClaim? ClaimOfIdentity(string identity) =>
            Claims.FirstOrDefault(claim => claim.Identity == identity);

        public ReplayedClaim? ClaimOn(string alias) =>
            Claims.FirstOrDefault(claim => claim.Alias == alias);

        public bool FamilyPresent(string[] aliases) => Claims.Any(claim =>
            claim.Identity is not null && aliases.Contains(claim.Alias, StringComparer.Ordinal));

        public string[] FamilyIdentities(string[] aliases) => Claims
            .Where(claim => claim.Identity is not null && aliases.Contains(claim.Alias, StringComparer.Ordinal))
            .Select(claim => claim.Identity!).Order(StringComparer.Ordinal).ToArray();

        public HashSet<string> NoneIdentities() => Claims
            .Where(claim => claim.Identity is not null && IsNone(claim.Relation))
            .Select(claim => claim.Identity!).ToHashSet(StringComparer.Ordinal);
    }
}
