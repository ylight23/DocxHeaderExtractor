using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PHYSICAL_STAGE1_MEMBERSHIP_ONLY, scored from its transport captures.
/// <para>
/// This is the first measurement in which membership stands alone. The model was never shown a
/// role field, a relation field, a selection mode or an offset, and no placement request exists, so
/// nothing in this score can be moved by a placement decision. There are no relation metrics here
/// because there are no relation outputs.
/// </para>
/// <para>
/// The comparator is the clean structured-v2 baseline, where all three masthead families are
/// present in all three repeats and Gold is bound 14/14. The question is whether removing the other
/// two tasks changes what the model accepts.
/// </para>
/// </summary>
public sealed class SemanticMembershipV1ScoringTests
{
    private const string RunRoot = "eval/a99-closed-loop/physical-stage1-membership-only-retry-v1/DOC-0252";
    private const string ScoreRoot = "eval/a99-closed-loop/physical-stage1-membership-only-score-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SchemaSha256 = "baac47daadb1047e843b1358f7a37dfe3d0736c1130672829efc7beb80b48ff2";
    private const string PromptSha256 = "433b134d428bd9053157ea6bb5efab253de3f46fb8da19bc0d3e8fa16d453766";

    /// <summary>Compared as families: a claim that splits has not become a new kind of error.</summary>
    private static readonly (string Family, string[] Aliases)[] MastheadFamilies =
    [
        ("international-program-and-tag", ["L0513:S0", "L0514:S0"]),
        ("hybrid-meeting", ["L0515:S0"]),
        ("date-venue-address", ["L0516:S0", "L0517:S0", "L0518:S0"]),
    ];

    private static readonly string[] Controls = ["L0396:S0", "L0550:S0"];

    /// <summary>The comparator, as its own frozen artifacts record it.</summary>
    private static readonly (int Tp, int Fp, int Fn)[] BaselineScores = [(14, 4, 0), (14, 4, 0), (14, 6, 0)];

    [Fact]
    public void Score_the_physical_stage1_membership_experiment()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var contract = SemanticCoordinateContract.PdfSemanticMembershipV1;
        Assert.Equal(SchemaSha256, contract.SchemaHash());

        var gold = GoldIdentities();
        var owned = SemanticMembershipV1TransportTests.Compose(plan)
            .Values.Select(SemanticMembershipV1TransportTests.PacketAliases)
            .SelectMany(item => item).ToHashSet(StringComparer.Ordinal);
        var targetGold = gold.Where(identity => owned.Contains(FirstAlias(identity)))
            .OrderBy(identity => identity, StringComparer.Ordinal).ToArray();
        Assert.Equal(14, targetGold.Length);

        var rows = new List<object>();
        var familyRows = new List<object>();
        var controlRows = new List<object>();
        var refusalReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var idsByIdentity = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var dispositions = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var capture = Capture(repeat);
            Assert.Equal("TRANSPORT_CAPTURE_COMPLETE", capture.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, capture.RootElement.GetProperty("stage2Calls").GetInt32());
            Assert.Equal(PromptSha256, capture.RootElement.GetProperty("promptSha256").GetString());

            var accepted = new List<Stage1AcceptedClaim>();
            var proposed = 0;
            var refusals = new List<object>();

            foreach (var call in capture.RootElement.GetProperty("calls").EnumerateArray())
            {
                Assert.Null(call.GetProperty("integrityFault").ValueKind == JsonValueKind.Null
                    ? null : call.GetProperty("integrityFault").GetString());

                var raw = Encoding.UTF8.GetString(
                    Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!));
                using var reply = JsonDocument.Parse(raw);

                // The protocol's own integrity gate: a reply must be readable as this contract.
                Assert.Empty(contract.Validate(reply.RootElement));

                var packet = call.GetProperty("packId").GetString()!;
                var callOwned = SemanticMembershipV1TransportTests.PacketAliases(
                    capture.RootElement.GetProperty("userMessages").GetProperty(packet)
                        .GetProperty("utf8Base64").GetString() is { } encoded
                        ? Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
                        : throw new InvalidOperationException("user message missing from capture"));

                var decoded = SemanticMembershipV1.Decode(reply.RootElement);
                proposed += decoded.Claims.Count;
                var outcome = SemanticMembershipV1.Accept(plan.Atoms, SourceSha256, decoded, callOwned);

                accepted.AddRange(outcome.Accepted);
                foreach (var refusal in outcome.Refusals)
                {
                    Record(refusalReasons, refusal.Reason);
                    refusals.Add(new { refusal.Alias, refusal.Reason });
                }
            }

            var frozen = Stage1Projection.Collect(accepted);
            var acceptedIdentities = frozen.Select(claim => claim.CanonicalIdentity).ToHashSet(StringComparer.Ordinal);
            foreach (var claim in frozen)
            {
                if (!idsByIdentity.TryGetValue(claim.CanonicalIdentity, out var set))
                    idsByIdentity[claim.CanonicalIdentity] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(claim.ClaimId.Value);
                Record(dispositions, claim.Disposition.ToString());
            }

            var score = Stage1MembershipEvaluator.Score(frozen, targetGold.ToHashSet(StringComparer.Ordinal), gold);
            var refusedAliases = refusals
                .Select(item => (string)item.GetType().GetProperty("Alias")!.GetValue(item)!)
                .ToHashSet(StringComparer.Ordinal);

            var states = targetGold.ToDictionary(
                identity => identity,
                identity => Stage1MembershipEvaluator
                    .StateOf(identity, acceptedIdentities, refusedAliases).ToString(),
                StringComparer.Ordinal);

            foreach (var (family, aliases) in MastheadFamilies)
            {
                var present = frozen.Any(claim =>
                    aliases.Contains(claim.Coordinates[0].Alias, StringComparer.Ordinal));
                familyRows.Add(new
                {
                    family,
                    repeat,
                    stage1 = present ? "PRESENT" : "ABSENT",
                    baseline = "PRESENT",
                    identities = frozen
                        .Where(claim => aliases.Contains(claim.Coordinates[0].Alias, StringComparer.Ordinal))
                        .Select(claim => claim.CanonicalIdentity).Order(StringComparer.Ordinal),
                });
            }

            foreach (var alias in Controls)
            {
                var claim = frozen.FirstOrDefault(item => item.Coordinates[0].Alias == alias);
                controlRows.Add(new
                {
                    alias,
                    repeat,
                    outcome = claim is null ? "NOT_ACCEPTED" : "ACCEPTED",
                    identity = claim?.CanonicalIdentity,
                    disposition = claim?.Disposition.ToString(),
                });
            }

            rows.Add(new
            {
                repeat,
                claimsProposed = proposed,
                claimsAccepted = frozen.Count,
                refusedOnGrounding = refusals.Count,
                refusals,
                truePositive = score.TruePositive,
                falsePositive = score.FalsePositive,
                falseNegative = score.FalseNegative,
                baseline = new
                {
                    tp = BaselineScores[repeat - 1].Tp,
                    fp = BaselineScores[repeat - 1].Fp,
                    fn = BaselineScores[repeat - 1].Fn,
                },
                documentLabels = frozen.Count(claim => claim.Disposition == Stage1MembershipDisposition.DocumentLabel),
                structuralUnits = frozen.Count(claim => claim.Disposition == Stage1MembershipDisposition.StructuralUnit),
                falsePositiveIdentities = score.FalsePositiveIdentities,
                falseNegativeIdentities = score.FalseNegativeIdentities,
                outcomeStates = states.Values.GroupBy(state => state, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            });
        }

        // Claim ids are stable: one identity, one id, wherever it appeared.
        Assert.All(idsByIdentity.Values, set => Assert.Single(set));

        var suppressions = familyRows.Count(row =>
            (string)row.GetType().GetProperty("stage1")!.GetValue(row)! == "ABSENT");

        // Per family, because the answer is not uniform across them and a total would hide that.
        var perFamily = MastheadFamilies.ToDictionary(
            family => family.Family,
            family => familyRows.Count(row =>
                (string)row.GetType().GetProperty("family")!.GetValue(row)! == family.Family
                && (string)row.GetType().GetProperty("stage1")!.GetValue(row)! == "PRESENT"),
            StringComparer.Ordinal);

        // Gold intact and the two stable families untouched is the condition that decides this.
        var goldIntact = rows.All(row =>
            (int)row.GetType().GetProperty("truePositive")!.GetValue(row)! == 14
            && (int)row.GetType().GetProperty("falseNegative")!.GetValue(row)! == 0);
        var coreFamiliesIntact =
            perFamily["international-program-and-tag"] == 3 && perFamily["hybrid-meeting"] == 3;

        var classification = !goldIntact
            ? "STAGE1_MEMBERSHIP_CONTRACT_REGRESSIVE"
            : coreFamiliesIntact
                ? "TWO_STAGE_SEPARATION_DOES_NOT_FIX_MEMBERSHIP_POLICY"
                : "PHYSICAL_TASK_SEPARATION_AFFECTS_MEMBERSHIP";

        FreezeArtifact.AssertJson(ScoreRoot, "physical-stage1-membership-only-score.v1.json", new
        {
            artifactKind = "a99_physical_stage1_membership_only_score",
            schemaVersion = "a99-physical-stage1-membership-only-score-v1",
            experimentId = "PHYSICAL_STAGE1_MEMBERSHIP_ONLY",
            runLineage = "physical-stage1-membership-only-retry-v1",
            providerCalls = 0,
            modelCalls = 0,
            scoredFrom = "the retry lineage's transport captures, replayed from raw bytes",

            authority = new
            {
                protocol = contract.ProtocolVersion,
                schemaSha256 = SchemaSha256,
                promptSha256 = PromptSha256,
                goldSha256 = GoldSha256,
                targetGoldCount = targetGold.Length,
                repeats = 3,
            },

            providerAccounting = new
            {
                abortedAttemptCalls = 1,
                retryAuthorityCalls = 6,
                totalProviderCallsObserved = 7,
                usableAuthorityCalls = 6,
                usableRepeats = 3,
                stage2ProviderCalls = 0,
                placementCalls = 0,
                abortedAttemptUsedForInference = false,
            },

            whatTheModelSaw = new
            {
                shown = new[] { "source evidence", "the membership question" },
                notShown = new[]
                {
                    "semanticRole", "relationHints", "parent-node", "ROOT", "NONE",
                    "selectionMode", "offsets", "any sign that a placement stage exists",
                },
            },

            membership = rows,

            mastheadFamilies = new
            {
                comparator = "baseline: F1 3/3, F2 3/3, F3 3/3 present",
                suppressions,
                persistences = familyRows.Count - suppressions,
                rows = familyRows,
            },

            controls = new
            {
                note = "Tracked, not targeted. Their disappearance is not masthead success.",
                rows = controlRows,
            },

            grounding = new
            {
                refusalReasons = refusalReasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                note = "A refusal here is the canonicalizer declining to ground a claim, not a "
                    + "semantic miss. The two are never summed.",
            },

            identityStability = new
            {
                distinctIdentities = idsByIdentity.Count,
                identitiesWithMoreThanOneClaimId = 0,
                dispositions = dispositions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            },

            classification = new
            {
                final = classification,
                goldIntact,
                coreFamiliesIntact,
                presentRepeatsPerFamily = perFamily,
                basis = "Gold membership and per-family masthead presence. Not the false-positive "
                    + "total, which mixes mechanisms and moved in a direction this experiment does "
                    + "not claim credit for.",
                reading = "The two stable masthead families are untouched at 3/3 each with the "
                    + "placement and role tasks entirely removed from the request. Only the "
                    + "date-venue family moved, and that family was already the intermittent one "
                    + "across every previous arm. So placement coupling, role naming, selection mode "
                    + "and coordinate serialization are all eliminated as explanations for the "
                    + "masthead error, and what remains is Stage-1 semantic discrimination under the "
                    + "current membership policy.",
                whatThisDoesNotShow = "That the model cannot make this distinction. It shows that "
                    + "this policy, as written, does not get it to - a statement about the policy and "
                    + "this model, not about language models.",
            },

            falsePositiveNote = "Stage-1 false positives were 5/2/2 against the baseline's 4/4/6. "
                + "The totals are lower in two repeats, but the masthead families the experiment "
                + "targeted are still there, so the movement is not read as success.",

            crossArmFamilyContext = new
            {
                note = "Presence counts out of three repeats, from each arm's own frozen artifacts.",
                international_program_and_tag = new { baseline = 3, e1 = 3, e2 = 3, stage1 = perFamily["international-program-and-tag"] },
                hybrid_meeting = new { baseline = 3, e1 = 3, e2 = 3, stage1 = perFamily["hybrid-meeting"] },
                date_venue_address = new { baseline = 3, e1 = 2, e2 = 2, stage1 = perFamily["date-venue-address"] },
                reading = "The date-venue family has been the unstable one in every arm including "
                    + "the baseline's successors. Its further contraction here is not distinguishable "
                    + "from that instability on three repeats.",
            },

            metricsThatDoNotExist = new[]
            {
                "relation correctness", "ROOT correctness", "NONE usage", "semanticRole agreement",
            },

            limitations = new
            {
                singleDocument = "DOC-0252",
                materializedGold = "48 / 3955",
                crossGenreGeneralizationEstablished = false,
            },
        });
    }

    private static void Record(Dictionary<string, int> counter, string key) =>
        counter[key] = counter.TryGetValue(key, out var value) ? value + 1 : 1;

    private static JsonDocument Capture(int repeat)
    {
        var path = Path.Combine(
            TestRepository.Path($"{RunRoot}/r{repeat}"),
            "stage1-membership-retry-transport-capture.v1.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FirstAlias(string identity)
    {
        var first = identity.Split('|')[0];
        return first[..first.LastIndexOf(':')];
    }

    private static HashSet<string> GoldIdentities()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("identity").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
