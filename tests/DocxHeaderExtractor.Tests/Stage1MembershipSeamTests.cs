using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The offline Stage-1 membership seam: an internal representation and an evaluator that cannot see
/// a relation.
/// <para>
/// Nothing here is provider-visible. No prompt, schema or request byte changes, and production
/// inference does not route through it. What it buys is the ability to score membership on its own,
/// which the last two experiments could not do - their results moved with a placement field that
/// had no business affecting whether a span is a heading.
/// </para>
/// <para>
/// The load-bearing test is parity: projecting the nine frozen repeats must reproduce their
/// recorded scores exactly. A seam that improved them would not be a better measurement, it would
/// be an unauthorized policy change hiding inside infrastructure.
/// </para>
/// </summary>
public sealed class Stage1MembershipSeamTests
{
    private const string BaselineRoot = "eval/a99-closed-loop/structured-v2-target-baseline-v1/DOC-0252";
    private const string E1Root = "eval/a99-closed-loop/exp-masthead-metadata-v2-experiment-v1/DOC-0252";
    private const string E2Root = "eval/a99-closed-loop/exp-masthead-e2-v2-experiment-v1/DOC-0252";
    private const string ArtifactRoot = "eval/a99-closed-loop/stage1-membership-seam-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";

    private static readonly Dictionary<string, (int Tp, int Fp, int Fn)[]> HistoricalScores = new(StringComparer.Ordinal)
    {
        ["STRUCTURED_V2_TARGET_BASELINE"] = [(14, 4, 0), (14, 4, 0), (14, 6, 0)],
        ["EXP_MASTHEAD_METADATA_V2"] = [(14, 6, 0), (14, 6, 0), (14, 4, 0)],
        ["EXP_MASTHEAD_METADATA_E2_V2"] = [(14, 6, 0), (14, 3, 0), (14, 6, 0)],
    };

    // ---- identity ------------------------------------------------------------------------------

    [Fact]
    public void An_authority_id_is_deterministic_and_derived_only_from_the_ordered_tuple()
    {
        var coordinates = new[]
        {
            new Stage1SourceCoordinate("L0359:S0", 0, 99),
            new Stage1SourceCoordinate("L0360:S0", 0, 11),
        };

        Assert.Equal(
            Stage1AuthorityClaimId.For(SourceSha256, coordinates).Value,
            Stage1AuthorityClaimId.For(SourceSha256, [.. coordinates]).Value);
        Assert.StartsWith("C:", Stage1AuthorityClaimId.For(SourceSha256, coordinates).Value, StringComparison.Ordinal);
        Assert.Equal(66, Stage1AuthorityClaimId.For(SourceSha256, coordinates).Value.Length);  // C: + 64 hex
    }

    [Fact]
    public void Reversing_the_parts_changes_the_identity()
    {
        var forward = new[]
        {
            new Stage1SourceCoordinate("L0359:S0", 0, 99),
            new Stage1SourceCoordinate("L0360:S0", 0, 11),
        };
        var reversed = forward.Reverse().ToArray();

        Assert.NotEqual(
            Stage1AuthorityClaimId.For(SourceSha256, forward).Value,
            Stage1AuthorityClaimId.For(SourceSha256, reversed).Value);
    }

    [Fact]
    public void Removing_a_part_changes_the_identity()
    {
        var whole = new[]
        {
            new Stage1SourceCoordinate("L0359:S0", 0, 99),
            new Stage1SourceCoordinate("L0360:S0", 0, 11),
        };

        Assert.NotEqual(
            Stage1AuthorityClaimId.For(SourceSha256, whole).Value,
            Stage1AuthorityClaimId.For(SourceSha256, whole[..1]).Value);
    }

    [Fact]
    public void The_same_coordinates_in_another_document_are_another_claim()
    {
        var coordinates = new[] { new Stage1SourceCoordinate("L0400:S0", 0, 44) };

        Assert.NotEqual(
            Stage1AuthorityClaimId.For(SourceSha256, coordinates).Value,
            Stage1AuthorityClaimId.For(new string('0', 64), coordinates).Value);
    }

    [Fact]
    public void Field_framing_keeps_two_different_tuples_from_hashing_alike()
    {
        // Without length-prefixing, an alias ending in a digit beside an offset beginning with one
        // could serialize to the same bytes as a different alias and offset. This is the case a
        // wider hash would not have caught.
        var first = new[] { new Stage1SourceCoordinate("L1:S0", 12, 345) };
        var second = new[] { new Stage1SourceCoordinate("L1:S01", 2, 345) };

        Assert.NotEqual(
            Stage1AuthorityClaimId.For(SourceSha256, first).Value,
            Stage1AuthorityClaimId.For(SourceSha256, second).Value);
    }

    [Fact]
    public void A_display_id_is_short_and_is_not_the_identity()
    {
        var id = Stage1AuthorityClaimId.For(SourceSha256, [new Stage1SourceCoordinate("L0400:S0", 0, 44)]);

        Assert.Equal(9, id.Display.Length);
        Assert.NotEqual(id.Value, id.Display);
        Assert.StartsWith("C", id.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void One_id_arriving_with_two_different_tuples_fails_closed()
    {
        var real = Stage1AuthorityClaimId.For(SourceSha256, [new Stage1SourceCoordinate("L0400:S0", 0, 44)]);
        var honest = new Stage1AcceptedClaim(
            real, [new Stage1SourceCoordinate("L0400:S0", 0, 44)], SourceSha256,
            Stage1MembershipDisposition.StructuralUnit);

        // The same id presented for a different tuple - what a collision would look like.
        var impostor = honest with { Coordinates = [new Stage1SourceCoordinate("L0999:S0", 0, 7)] };

        var error = Assert.Throws<InvalidOperationException>(
            () => Stage1Projection.Collect([honest, impostor]));
        Assert.Equal("AUTHORITY_CLAIM_ID_COLLISION", error.Message);
    }

    [Fact]
    public void Role_and_relation_wording_cannot_move_the_identity()
    {
        // The claim record has nowhere to put them, which is the strongest form of this guarantee:
        // identity is computed from the tuple and the document, and there is no third input.
        var coordinates = new[] { new Stage1SourceCoordinate("L0400:S0", 0, 44) };
        var asLabel = new Stage1AcceptedClaim(
            Stage1AuthorityClaimId.For(SourceSha256, coordinates), coordinates, SourceSha256,
            Stage1MembershipDisposition.DocumentLabel);
        var asUnit = asLabel with { Disposition = Stage1MembershipDisposition.StructuralUnit };

        Assert.Equal(asLabel.ClaimId.Value, asUnit.ClaimId.Value);
        Assert.Equal(asLabel.CanonicalIdentity, asUnit.CanonicalIdentity);
    }

    // ---- representation ---------------------------------------------------------------------------

    [Fact]
    public void A_document_label_is_accepted_without_a_placement()
    {
        var coordinates = new[] { new Stage1SourceCoordinate("L0515:S0", 0, 14) };
        var label = new Stage1AcceptedClaim(
            Stage1AuthorityClaimId.For(SourceSha256, coordinates), coordinates, SourceSha256,
            Stage1MembershipDisposition.DocumentLabel);

        // No parent, no ROOT, no NONE, and nothing in the type that could carry one.
        Assert.Equal(Stage1MembershipDisposition.DocumentLabel, label.Disposition);
        Assert.Equal("L0515:S0:0-14", label.CanonicalIdentity);
        Assert.DoesNotContain("NONE", JsonSerializer.Serialize(label), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unbound_binding_projects_to_nothing()
    {
        var refused = new SemanticSourcePartsBinding(SemanticSourcePartsStatus.TextNotInAtom, []);

        Assert.Null(Stage1Projection.Project(SourceSha256, refused));
    }

    [Fact]
    public void The_evaluator_scores_from_accepted_claims_and_gold_alone()
    {
        // It has no parameter for a relation, a role or a graph - the isolation is structural.
        var accepted = new[] { Claim("L0400:S0", 0, 44), Claim("L0999:S0", 0, 7) };
        var target = new HashSet<string>(StringComparer.Ordinal) { "L0400:S0:0-44", "L0420:S0:0-40" };
        var all = new HashSet<string>(StringComparer.Ordinal) { "L0400:S0:0-44", "L0420:S0:0-40" };

        var score = Stage1MembershipEvaluator.Score(accepted, target, all);

        Assert.Equal(1, score.TruePositive);
        Assert.Equal(1, score.FalsePositive);
        Assert.Equal(1, score.FalseNegative);
        Assert.Equal(["L0400:S0:0-44"], score.TruePositiveIdentities);
        Assert.Equal(["L0999:S0:0-7"], score.FalsePositiveIdentities);
        Assert.Equal(["L0420:S0:0-40"], score.FalseNegativeIdentities);
    }

    [Fact]
    public void A_claim_never_proposed_and_one_refused_on_grounding_stay_different()
    {
        var accepted = new HashSet<string>(StringComparer.Ordinal) { "L0400:S0:0-44" };
        var refused = new HashSet<string>(StringComparer.Ordinal) { "L0396:S0" };

        Assert.Equal(Stage1ProposalState.Bound,
            Stage1MembershipEvaluator.StateOf("L0400:S0:0-44", accepted, refused));
        Assert.Equal(Stage1ProposalState.ProposedButRefused,
            Stage1MembershipEvaluator.StateOf("L0396:S0:0-30", accepted, refused));
        Assert.Equal(Stage1ProposalState.NotProposed,
            Stage1MembershipEvaluator.StateOf("L0777:S0:0-12", accepted, refused));
    }

    // ---- the parity proof ---------------------------------------------------------------------------

    [Fact]
    public void The_seam_reproduces_every_frozen_repeat_exactly()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceSha256, CanonicalArtifactHash.OfBytes(TestRepository.Path(Doc0252Pdf)));

        var contract = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        var owned = OwnedAliases(plan);
        var gold = GoldIdentities();
        var targetGold = gold.Where(identity => owned.Contains(FirstAlias(identity)))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(14, targetGold.Count);

        var arms = new (string Name, string Root)[]
        {
            ("STRUCTURED_V2_TARGET_BASELINE", BaselineRoot),
            ("EXP_MASTHEAD_METADATA_V2", E1Root),
            ("EXP_MASTHEAD_METADATA_E2_V2", E2Root),
        };

        var rows = new List<object>();
        var everyClaim = new List<Stage1AcceptedClaim>();

        foreach (var (name, root) in arms)
        {
            for (var repeat = 1; repeat <= 3; repeat++)
            {
                var bound = BoundBindings(root, repeat, plan, contract, owned);

                // §13 the bound set the current pipeline produces, derived on its own terms.
                var currentSet = bound.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);

                // §7 every bound claim yields exactly one accepted claim, and nothing else does.
                var accepted = Stage1Projection.Collect(
                    bound.Select(item => Stage1Projection.Project(SourceSha256, item)!));
                var acceptedSet = accepted.Select(claim => claim.CanonicalIdentity).ToHashSet(StringComparer.Ordinal);

                Assert.Empty(currentSet.Except(acceptedSet, StringComparer.Ordinal));
                Assert.Empty(acceptedSet.Except(currentSet, StringComparer.Ordinal));

                // §14 stripping relation and role before decoding changes nothing.
                var stripped = Stage1Projection.Collect(
                    BoundBindings(root, repeat, plan, contract, owned, stripFields: true)
                        .Select(item => Stage1Projection.Project(SourceSha256, item)!));
                Assert.Equal(
                    acceptedSet.Order(StringComparer.Ordinal),
                    stripped.Select(claim => claim.CanonicalIdentity).Order(StringComparer.Ordinal));

                // §12 the evaluator reproduces the frozen score.
                var score = Stage1MembershipEvaluator.Score(accepted, targetGold, gold);
                var expected = HistoricalScores[name][repeat - 1];
                Assert.Equal(expected.Tp, score.TruePositive);
                Assert.Equal(expected.Fp, score.FalsePositive);
                Assert.Equal(expected.Fn, score.FalseNegative);

                everyClaim.AddRange(accepted);
                rows.Add(new
                {
                    arm = name,
                    repeat,
                    currentBoundClaims = currentSet.Count,
                    stage1AcceptedClaims = acceptedSet.Count,
                    setEquality = true,
                    score = new { tp = score.TruePositive, fp = score.FalsePositive, fn = score.FalseNegative },
                    frozen = new { tp = expected.Tp, fp = expected.Fp, fn = expected.Fn },
                    identicalWithoutRelationAndRole = true,
                });
            }
        }

        // §15 identity statistics over everything projected.
        var identities = everyClaim.Select(claim => claim.CanonicalIdentity).ToArray();
        var ids = everyClaim.Select(claim => claim.ClaimId.Value).ToArray();
        var uniqueIdentities = identities.Distinct(StringComparer.Ordinal).Count();
        var uniqueIds = ids.Distinct(StringComparer.Ordinal).Count();

        // One identity always yields one id, wherever it appears.
        Assert.All(
            everyClaim.GroupBy(claim => claim.CanonicalIdentity, StringComparer.Ordinal),
            group => Assert.Single(group.Select(claim => claim.ClaimId.Value).Distinct(StringComparer.Ordinal)));
        Assert.Equal(uniqueIdentities, uniqueIds);

        // §8 the multi-part Gold claim, through the real projection.
        var multiPart = gold.Single(identity => identity.Contains('|'));
        var multiPartId = Stage1AuthorityClaimId.For(SourceSha256, CoordinatesOf(multiPart));
        var reversedId = Stage1AuthorityClaimId.For(SourceSha256, [.. CoordinatesOf(multiPart).Reverse()]);
        Assert.NotEqual(multiPartId.Value, reversedId.Value);

        FreezeArtifact.AssertJson(ArtifactRoot, "stage1-membership-seam.v1.json", new
        {
            artifactKind = "a99_stage1_membership_seam",
            schemaVersion = "a99-stage1-membership-seam-v1",
            phases = new[] { "A: internal Stage-1 model and projection", "B: Stage-1 membership evaluator" },
            providerCalls = 0,
            modelCalls = 0,
            providerVisible = false,
            providerRequestBytesChanged = false,
            productionRuntimeRouted = false,

            internalModel = new
            {
                types = new[]
                {
                    "Stage1MembershipDisposition", "Stage1SourceCoordinate", "Stage1AuthorityClaimId",
                    "Stage1AcceptedClaim", "Stage1Projection", "Stage1MembershipEvaluator",
                    "Stage1ProposalState", "Stage1MembershipScore",
                },
                acceptedClaimCarries = new[]
                {
                    "authority claim id", "ordered canonical coordinate tuple",
                    "document source sha256", "membership disposition",
                },
                acceptedClaimDoesNotCarry = new[]
                {
                    "semanticRole", "relationHints", "parent-node", "ROOT", "NONE",
                    "placement edge", "model-chosen selectionMode", "model-chosen offsets",
                },
                note = "The excluded fields have nowhere to live in the type, so independence is "
                    + "structural rather than a rule someone must remember.",
            },

            authorityIdentity = new
            {
                algorithm = "SHA-256",
                bits = 256,
                domainSeparator = Stage1AuthorityClaimId.DomainSeparator,
                input = new[]
                {
                    "domain separator", "document source sha256",
                    "ordered (sourceAlias, startUtf16, endUtf16)",
                },
                encoding = "each field length-prefixed and unit-separated (0x1F)",
                framingRationale = "Concatenation alone would let an alias ending in a digit and an "
                    + "offset beginning with one serialize identically to a different tuple. A wider "
                    + "hash does not fix an ambiguous encoding.",
                displayForm = "C + first 8 hex of the digest, diagnostics only, never authoritative",
                collisionPolicy = "AUTHORITY_CLAIM_ID_COLLISION, fail closed, never merge",
            },

            membershipDisposition = new
            {
                values = new[] { "DOCUMENT_LABEL", "STRUCTURAL_UNIT" },
                noneIntroduced = false,
                usedAsRetrospectiveFilter = false,
                rule = "Every bound structured-v2 claim projects to exactly one accepted claim. "
                    + "Disposition classifies the accepted set; it never removes anything from it.",
            },

            projectionInvariant = new
            {
                input = "harness-bound source identity, after validate, decode, canonicalize and bind",
                unboundInputAccepted = false,
                lossless = true,
                setEqualityBothDirections = true,
                rows,
            },

            independence = new
            {
                relationFieldsRequiredForStage1 = false,
                semanticRoleRequiredForStage1 = false,
                method = "Each repeat was projected twice - once from the full reply, once with "
                    + "relationHints, parent-node and semanticRole removed before decoding - and the "
                    + "accepted identity sets compared.",
                evaluatorRequiresStage2 = false,
                dependencyDirection = "structured-v2 bound result -> Stage1Projection -> Stage1MembershipEvaluator",
            },

            identityStatistics = new
            {
                totalProjectedClaims = everyClaim.Count,
                uniqueCoordinateIdentities = uniqueIdentities,
                uniqueAuthorityClaimIds = uniqueIds,
                authorityClaimIdCollisions = 0,
                stableAcrossArmsAndRepeats = true,
            },

            multiPartProof = new
            {
                identity = multiPart,
                authorityClaimId = multiPartId.Value,
                displayClaimId = multiPartId.Display,
                orderSensitive = true,
                documentNamespaced = true,
                derivedFromJoinedNormalizedText = false,
            },

            evaluatorSemantics = new
            {
                consumes = new[] { "accepted Stage-1 claims", "Gold membership authority" },
                emits = new[] { "TP", "FP", "FN", "and the identity set behind each" },
                diagnosticStatesKeptSeparate = new[] { "NOT_PROPOSED", "PROPOSED_BUT_REFUSED", "BOUND" },
                refusedProposalBecomesAcceptedClaim = false,
                placementCanAffectScore = false,
                goldMatching = "canonical ordered bound identity, never normalized text, role string, "
                    + "joined source text or model output order",
            },

            futureProtocolSeam = new
            {
                supports = "a99-semantic-membership-v1",
                implementedHere = false,
                newProviderPromptHash = false,
                newProviderSchemaAuthority = false,
                requestRebaseline = false,
            },

            firstProviderExperiment = new
            {
                id = "PHYSICAL_STAGE1_MEMBERSHIP_ONLY",
                executed = false,
                document = "DOC-0252",
                packs = new[] { "PACK_005", "PACK_006" },
                repeats = 3,
                stage1ProviderCalls = 6,
                stage2ProviderCalls = 0,
                sequence = "validate, canonicalize, bind, freeze immutable claim ids, score membership, stop",
                stage2RequestCreated = false,
                comparator = new
                {
                    arm = "STRUCTURED_V2_TARGET_BASELINE",
                    gold = "14/14 bound in every repeat",
                    mastheadFamilies = new { f1 = "PRESENT 3/3", f2 = "PRESENT 3/3", f3 = "PRESENT 3/3" },
                    measured = new[]
                    {
                        "Gold membership", "masthead family presence", "other false-positive identities",
                        "grounding refusals",
                    },
                    relationMetricExists = false,
                },
            },

            stage2 = new { deferred = true, batchingPolicy = "DEFERRED" },

            limitations = new
            {
                singleDocument = "DOC-0252",
                materializedGold = "48 / 3955",
                crossGenreGeneralization = "NOT_ESTABLISHED",
                whatThisDoesNotShow = "That separating the stages changes membership behaviour. This is "
                    + "infrastructure; the question is still open and needs the 6-call experiment.",
            },
        });
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static Stage1AcceptedClaim Claim(string alias, int start, int end)
    {
        var coordinates = new[] { new Stage1SourceCoordinate(alias, start, end) };
        return new Stage1AcceptedClaim(
            Stage1AuthorityClaimId.For(SourceSha256, coordinates), coordinates, SourceSha256,
            Stage1MembershipDisposition.StructuralUnit);
    }

    private static Stage1SourceCoordinate[] CoordinatesOf(string identity) =>
        identity.Split('|').Select(part =>
        {
            var split = part.LastIndexOf(':');
            var span = part[(split + 1)..].Split('-');
            return new Stage1SourceCoordinate(part[..split], int.Parse(span[0]), int.Parse(span[1]));
        }).ToArray();

    private static List<SemanticSourcePartsBinding> BoundBindings(
        string root, int repeat, PdfStructuredSourceAuthority plan, SemanticCoordinateContract contract,
        IReadOnlySet<string> owned, bool stripFields = false)
    {
        var bound = new List<SemanticSourcePartsBinding>();
        foreach (var raw in CapturedResponses($"{root}/r{repeat}"))
        {
            using var response = JsonDocument.Parse(raw);
            foreach (var entry in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                if (entry.TryGetProperty("isHeading", out var flag) && !flag.GetBoolean()) continue;
                var decoded = contract.Decode(stripFields ? StripFields(entry) : entry);
                if (decoded.Proposals.Count == 0) continue;
                var parts = decoded.Proposals[0].SourceParts!;
                if (parts.Any(part => !owned.Contains(part.SourceAlias))) continue;
                var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, parts);
                if (!canonical.IsCanonical) continue;
                var result = SemanticSourcePartBinder.Bind(
                    plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));
                if (result.IsBound) bound.Add(result);
            }
        }
        return bound;
    }

    private static JsonElement StripFields(JsonElement entry)
    {
        var stripped = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entry.EnumerateObject())
        {
            if (property.NameEquals("relationHints") || property.NameEquals("semanticRole")
                || property.NameEquals("parentNode"))
            {
                continue;
            }
            stripped[property.Name] = JsonSerializer.Deserialize<JsonElement>(property.Value.GetRawText());
        }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(stripped));
        return document.RootElement.Clone();
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

    private static HashSet<string> OwnedAliases(PdfStructuredSourceAuthority plan) =>
        StructuredV2TargetBaselineTransportTests.ComposeRequests(plan)
            .Values.SelectMany(segment => segment.Owned).ToHashSet(StringComparer.Ordinal);

    private static string FirstAlias(string identity) =>
        StructuredV2TargetBaselineTransportTests.FirstAlias(identity);

    private static HashSet<string> GoldIdentities()
    {
        using var gold = CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256);
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("identity").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
