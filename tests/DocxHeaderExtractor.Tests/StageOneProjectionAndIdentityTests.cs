using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Two gates before the successor architecture is built: that projecting into Stage 1 changes no
/// membership, and that a claim id is safe to be an authority.
/// <para>
/// A projection that quietly dropped a claim would make the feasibility proof a policy change
/// wearing a proof's clothes - the false-positive counts would improve because the new
/// representation discarded something, not because anything was fixed. So every historical repeat
/// is checked for exact set equality against what the current pipeline binds, with no filtering by
/// relation, role or disposition.
/// </para>
/// <para>
/// The identity is widened for a plainer reason: eight hex characters is 32 bits, and birthday
/// collisions there start to matter in the thousands. An identity that is allowed to be wrong
/// occasionally is not an authority.
/// </para>
/// </summary>
public sealed class StageOneProjectionAndIdentityTests
{
    private const string AuditRoot = "eval/a99-closed-loop/stage1-projection-identity-audit-v1";
    private const string BaselineRoot = "eval/a99-closed-loop/structured-v2-target-baseline-v1/DOC-0252";
    private const string E1Root = "eval/a99-closed-loop/exp-masthead-metadata-v2-experiment-v1/DOC-0252";
    private const string E2Root = "eval/a99-closed-loop/exp-masthead-e2-v2-experiment-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";

    /// <summary>The scores the historical artifacts already record, as the parity target.</summary>
    private static readonly Dictionary<string, (int Tp, int Fp, int Fn)[]> HistoricalScores = new(StringComparer.Ordinal)
    {
        ["STRUCTURED_V2_TARGET_BASELINE"] = [(14, 4, 0), (14, 4, 0), (14, 6, 0)],
        ["EXP_MASTHEAD_METADATA_V2"] = [(14, 6, 0), (14, 6, 0), (14, 4, 0)],
        ["EXP_MASTHEAD_METADATA_E2_V2"] = [(14, 6, 0), (14, 3, 0), (14, 6, 0)],
    };

    private static readonly string[] MastheadAliases =
        ["L0513:S0", "L0514:S0", "L0515:S0", "L0516:S0", "L0517:S0", "L0518:S0"];

    [Fact]
    public void Verify_lossless_stage1_projection_and_harden_claim_identity()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var contract = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        var gold = GoldIdentities();
        var owned = OwnedAliases(plan);
        var targetGold = gold.Where(identity => owned.Contains(FirstAlias(identity)))
            .OrderBy(identity => identity, StringComparer.Ordinal).ToArray();
        Assert.Equal(14, targetGold.Length);

        var arms = new (string Name, string Root)[]
        {
            ("STRUCTURED_V2_TARGET_BASELINE", BaselineRoot),
            ("EXP_MASTHEAD_METADATA_V2", E1Root),
            ("EXP_MASTHEAD_METADATA_E2_V2", E2Root),
        };

        var rows = new List<object>();
        var allLossless = true;
        var allScoresMatch = true;
        var everyClaim = new List<(string Identity, string AuthorityId, string DisplayId)>();

        foreach (var (name, root) in arms)
        {
            for (var repeat = 1; repeat <= 3; repeat++)
            {
                // What the current pipeline binds, derived the way the existing scorers derive it.
                var current = CurrentBoundIdentities(root, repeat, plan, contract, owned);

                // What Stage 1 would accept, derived from the same bytes but with every placement
                // and role field stripped from the input before projection.
                var projected = ProjectStageOne(root, repeat, plan, contract, owned, stripPlacement: true);
                var projectedIdentities = projected.Select(claim => claim.Identity).ToHashSet(StringComparer.Ordinal);

                // §1 the invariant: projection is membership-lossless, in both directions.
                var lost = current.Except(projectedIdentities, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var gained = projectedIdentities.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (lost.Length > 0 || gained.Length > 0) allLossless = false;

                // §3 stripping relation and role changes nothing about which claims project.
                var withFields = ProjectStageOne(root, repeat, plan, contract, owned, stripPlacement: false);
                Assert.Equal(
                    withFields.Select(claim => claim.Identity).Order(StringComparer.Ordinal),
                    projected.Select(claim => claim.Identity).Order(StringComparer.Ordinal));

                // §2/§11 the Stage-1 evaluator, consuming only accepted claims and Gold.
                var tp = targetGold.Count(projectedIdentities.Contains);
                var fp = projectedIdentities.Count(identity => !gold.Contains(identity));
                var fn = targetGold.Length - tp;
                var expected = HistoricalScores[name][repeat - 1];
                if ((tp, fp, fn) != expected) allScoresMatch = false;

                foreach (var claim in projected)
                    everyClaim.Add((claim.Identity, claim.AuthorityId, claim.DisplayId));

                rows.Add(new
                {
                    arm = name,
                    repeat,
                    currentBoundClaims = current.Count,
                    projectedAcceptedClaims = projectedIdentities.Count,
                    lossless = lost.Length == 0 && gained.Length == 0,
                    lostByProjection = lost,
                    gainedByProjection = gained,
                    projectedScore = new { tp, fp, fn },
                    historicalScore = new { tp = expected.Tp, fp = expected.Fp, fn = expected.Fn },
                    scoreMatches = (tp, fp, fn) == expected,
                    documentLabels = projected.Count(claim => claim.Disposition == "DOCUMENT_LABEL"),
                    structuralUnits = projected.Count(claim => claim.Disposition == "STRUCTURAL_UNIT"),
                    mastheadFamilyClaims = projected.Count(claim =>
                        MastheadAliases.Contains(claim.Alias, StringComparer.Ordinal)),
                });
            }
        }

        Assert.True(allLossless, "projection dropped or invented a claim");
        Assert.True(allScoresMatch, "projected membership scores do not reproduce the historical ones");

        // ---- §10 identity proof over every projected claim in all nine repeats --------------------
        var byIdentity = everyClaim.GroupBy(claim => claim.Identity, StringComparer.Ordinal).ToArray();
        var byAuthority = everyClaim.GroupBy(claim => claim.AuthorityId, StringComparer.Ordinal).ToArray();

        // The same canonical identity must produce a byte-identical id everywhere it appears.
        Assert.All(byIdentity, group => Assert.Single(group.Select(claim => claim.AuthorityId).Distinct(StringComparer.Ordinal)));

        // And no two different identities may share one id.
        var collisions = byAuthority.Count(group =>
            group.Select(claim => claim.Identity).Distinct(StringComparer.Ordinal).Count() > 1);
        Assert.Equal(0, collisions);

        // §6 the short form is a display convenience and is demonstrably not safe alone.
        var displayCollisions = everyClaim.GroupBy(claim => claim.DisplayId, StringComparer.Ordinal)
            .Count(group => group.Select(claim => claim.Identity).Distinct(StringComparer.Ordinal).Count() > 1);

        // ---- §8 multi-part identity, order-sensitive -----------------------------------------------
        var multiPart = MultiPartGoldClaim();
        var reversed = string.Join("|", multiPart.Split('|').Reverse());
        var multiPartId = AuthorityClaimId(multiPart);
        Assert.NotEqual(multiPartId, AuthorityClaimId(reversed));
        Assert.Equal(multiPartId, AuthorityClaimId(multiPart));   // deterministic

        // §9 the same coordinates under a different document must not be the same claim.
        Assert.NotEqual(multiPartId, AuthorityClaimId(multiPart, documentSourceHash: new string('0', 64)));

        FreezeArtifact.AssertJson(AuditRoot, "stage1-projection-identity-audit.v1.json", new
        {
            artifactKind = "a99_stage1_projection_identity_audit",
            schemaVersion = "a99-stage1-projection-identity-audit-v1",
            providerCalls = 0,
            modelCalls = 0,

            projectionInvariant = new
            {
                statement = "Stage1AcceptedClaims == CurrentV2BoundClaims, per repeat, both directions.",
                lossless = allLossless,
                scoresReproduced = allScoresMatch,
                noFilteringBy = new[]
                {
                    "relation", "ROOT/NONE", "semanticRole", "membership disposition",
                    "masthead policy", "structural placement",
                },
                why = "A projection that dropped a claim would improve false-positive counts by "
                    + "discarding evidence rather than by fixing anything, and the feasibility proof "
                    + "would then be a policy change in disguise.",
                rows,
            },

            designArtifactCorrection = new
            {
                claim = "The design artifact's doc0252Projection reported 14 TP / 4 FP / 0 FN.",
                scope = "That figure is the baseline r1 repeat only, and the artifact labels it as such. "
                    + "It was restated without that scope in the accompanying report, where it reads as a "
                    + "claim about all nine repeats. It is not one: false positives range from 3 to 6.",
                corrected = "Per-repeat scores are recorded in full above.",
                historicalArtifactsRewritten = false,
            },

            stage1Independence = new
            {
                relationFieldsRequired = false,
                semanticRoleRequired = false,
                method = "Every repeat was projected twice - once from the full reply and once with "
                    + "relationHints, parent-node, ROOT, NONE and semanticRole removed before projection "
                    + "- and the accepted identity sets were compared.",
            },

            membershipDisposition = new
            {
                values = new[] { "DOCUMENT_LABEL", "STRUCTURAL_UNIT" },
                usedToDiscardHistoricalClaims = false,
                rule = "Every bound v2 claim projects to an accepted Stage-1 claim first; the disposition "
                    + "classifies the accepted set afterwards. The new ontology is never used to "
                    + "retroactively improve an old false-positive count.",
            },

            claimIdentity = new
            {
                authority = new
                {
                    algorithm = "SHA-256",
                    bits = 256,
                    namespaceInput = new[]
                    {
                        "domain separator: a99-semantic-membership-claim-v1",
                        "document source sha256",
                        "ordered sequence of (sourceAlias, startUtf16, endUtf16)",
                    },
                    canonicalEncoding = "each field length-prefixed and unit-separated, so no alias or "
                        + "offset sequence can be re-parsed as a different tuple",
                    properties = new[]
                    {
                        "deterministic", "source-coordinate derived", "ordered multi-part sensitive",
                        "no normalized-text dependence", "no relation dependence",
                        "no semanticRole dependence", "no pack or repeat dependence",
                        "document-namespaced",
                    },
                },
                display = new
                {
                    form = "C + first 8 hex of the authority id",
                    authoritative = false,
                    observedDisplayCollisionsInThisCorpus = displayCollisions,
                    rule = "A display id is for logs and UI. It is never accepted as an identity without "
                        + "resolving it to an authority id, and its absence of collisions in one small "
                        + "corpus is not evidence that it is safe.",
                },
                supersedes = "The design artifact proposed C + 8 hex as the authority id. That is 32 bits, "
                    + "where birthday collisions become likely in the thousands of claims - and this corpus "
                    + "is 3955 approved headings before any false positive is counted.",
                collisionPolicy = new
                {
                    state = "AUTHORITY_CLAIM_ID_COLLISION",
                    behaviour = "fail closed; never silently merge two different coordinate tuples",
                    rationale = "A cryptographic id makes collision improbable, not impossible, and the "
                        + "cost of the check is nothing next to merging two claims.",
                },
            },

            identityProof = new
            {
                totalProjectedClaims = everyClaim.Count,
                uniqueCoordinateIdentities = byIdentity.Length,
                uniqueAuthorityClaimIds = byAuthority.Length,
                claimIdCollisions = collisions,
                stableAcrossRepeats = true,
                repeats = 9,
            },

            multiPartProof = new
            {
                identity = multiPart,
                authorityClaimId = multiPartId,
                displayClaimId = DisplayClaimId(multiPartId),
                orderSensitive = true,
                reversedProducesDifferentId = true,
                documentNamespaced = true,
                normalizedTextJoinForbidden = true,
            },

            stage1Evaluator = new
            {
                consumes = new[] { "accepted bound ClaimIds", "Gold membership authority" },
                metrics = new[] { "TP", "FP", "FN" },
                separately = new[] { "NOT_PROPOSED", "PROPOSED_BUT_REFUSED", "BOUND" },
                relationCanAffectScore = false,
                why = "The evaluator never sees a relation field, so a placement error cannot be counted "
                    + "as a membership miss and a membership error cannot be hidden by placement.",
            },

            firstProviderExperiment = new
            {
                id = "PHYSICAL_STAGE1_MEMBERSHIP_ONLY",
                packs = new[] { "PACK_005", "PACK_006" },
                repeats = 3,
                stage1Calls = 6,
                stage2Calls = 0,
                sequence = "source evidence -> Stage-1 provider call -> harness bind -> immutable "
                    + "membership -> stop and score. No Stage-2 request exists.",
                question = "Does removing placement from the model's available task change semantic "
                    + "membership behaviour?",
                whyStrongerThanTwoStage = "Asking the placement question at all, even in a later call, "
                    + "leaves open whether the membership result depended on it. Not creating the request "
                    + "is a cleaner intervention than creating it and ignoring it - and it costs 6 calls "
                    + "instead of 9 or 12.",
                comparator = new
                {
                    arm = "STRUCTURED_V2_TARGET_BASELINE",
                    mastheadFamilies = "F1 3/3, F2 3/3, F3 3/3 present",
                    gold = "14/14 bound each repeat",
                    measured = new[]
                    {
                        "Gold membership", "masthead family presence", "other false-positive identities",
                        "grounding refusals",
                    },
                    noRelationMetricExists = true,
                },
                interpretation = new
                {
                    ifMastheadPersists = "TWO_STAGE_SEPARATION_DOES_NOT_FIX_MEMBERSHIP_POLICY - the "
                        + "architecture remains right for isolation and correctness, but the masthead "
                        + "problem lives inside Stage 1 and no amount of separation will move it.",
                    ifMastheadContracts = "PHYSICAL_TASK_SEPARATION_AFFECTS_MEMBERSHIP - then Stage 2 is "
                        + "worth introducing, in its own experiment.",
                },
            },

            stage2 = new
            {
                deferred = true,
                batchingPolicy = "DEFERRED",
                why = "Document-batched placement changes the context relative to per-pack execution, "
                    + "which is a semantic decision. It should not be settled before Stage-1 behaviour is "
                    + "measured.",
            },

            architecturalConclusion = new
            {
                recommendedSuccessorDesign = "OPTION_B",
                unchanged = true,
                note = "This audit tested the projection and the identity, not the architecture. Nothing "
                    + "here falsifies the physical two-call recommendation; the first experiment under it "
                    + "is simply narrower than first proposed.",
            },

            limitations = new
            {
                singleDocument = "DOC-0252",
                materializedGold = "48 / 3955",
                crossGenreGeneralization = "NOT_ESTABLISHED",
                identityCorpusSize = "Collision freedom here is over a few hundred projected claims. It "
                    + "demonstrates determinism and stability, not the collision resistance of the hash, "
                    + "which rests on SHA-256 rather than on this measurement.",
            },
        });
    }

    // ---- identity -----------------------------------------------------------------------------------

    /// <summary>
    /// The authoritative claim identity: SHA-256 over a domain separator, the document's source
    /// hash, and the ordered coordinate tuple, each field length-prefixed so no two different
    /// tuples can serialize to the same bytes.
    /// </summary>
    private static string AuthorityClaimId(string identity, string? documentSourceHash = null)
    {
        var builder = new StringBuilder();
        Append(builder, "a99-semantic-membership-claim-v1");
        Append(builder, documentSourceHash ?? SourceSha256);
        foreach (var part in identity.Split('|'))
        {
            var split = part.LastIndexOf(':');
            var alias = part[..split];
            var span = part[(split + 1)..].Split('-');
            Append(builder, alias);
            Append(builder, span[0]);
            Append(builder, span[1]);
        }
        return "C:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();

        // Length-prefixing is what makes the encoding unambiguous: without it, an alias ending in a
        // digit and an offset beginning with one could be re-read as a different split.
        static void Append(StringBuilder target, string field) =>
            target.Append(field.Length).Append(':').Append(field).Append('\u001f');
    }

    private static string DisplayClaimId(string authorityId) =>
        "C" + authorityId[2..10].ToUpperInvariant();

    // ---- projection ---------------------------------------------------------------------------------

    /// <summary>
    /// What the current pipeline binds for one repeat: decode, canonicalize, bind, keeping the
    /// owned-segment rule the existing scorers apply. This is the set the projection must match.
    /// </summary>
    private static HashSet<string> CurrentBoundIdentities(
        string root, int repeat, PdfStructuredSourceAuthority plan,
        SemanticCoordinateContract contract, IReadOnlySet<string> owned)
    {
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in CapturedResponses($"{root}/r{repeat}"))
        {
            using var response = JsonDocument.Parse(raw);
            foreach (var entry in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                if (entry.TryGetProperty("isHeading", out var flag) && !flag.GetBoolean()) continue;
                var decoded = contract.Decode(entry);
                if (decoded.Proposals.Count == 0) continue;
                var parts = decoded.Proposals[0].SourceParts!;
                if (parts.Any(part => !owned.Contains(part.SourceAlias))) continue;
                var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, parts);
                if (!canonical.IsCanonical) continue;
                var result = SemanticSourcePartBinder.Bind(
                    plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));
                if (result.IsBound) bound.Add(result.Identity);
            }
        }
        return bound;
    }

    /// <summary>
    /// The same replies expressed as Stage-1 claims. With <paramref name="stripPlacement"/> the
    /// placement and role fields are removed from each entry before it is read, which is how this
    /// proves Stage-1 membership does not depend on them.
    /// </summary>
    private static List<ProjectedClaim> ProjectStageOne(
        string root, int repeat, PdfStructuredSourceAuthority plan, SemanticCoordinateContract contract,
        IReadOnlySet<string> owned, bool stripPlacement)
    {
        var claims = new List<ProjectedClaim>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in CapturedResponses($"{root}/r{repeat}"))
        {
            using var response = JsonDocument.Parse(raw);
            foreach (var entry in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                if (entry.TryGetProperty("isHeading", out var flag) && !flag.GetBoolean()) continue;

                var role = !stripPlacement && entry.TryGetProperty("semanticRole", out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString()! : "(none)";

                var decoded = contract.Decode(stripPlacement ? StripFields(entry) : entry);
                if (decoded.Proposals.Count == 0) continue;
                var parts = decoded.Proposals[0].SourceParts!;
                if (parts.Any(part => !owned.Contains(part.SourceAlias))) continue;

                var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, parts);
                if (!canonical.IsCanonical) continue;
                var bound = SemanticSourcePartBinder.Bind(
                    plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));
                if (!bound.IsBound || !seen.Add(bound.Identity)) continue;

                // Disposition classifies the accepted set; it never removes anything from it.
                var disposition = role.Contains("title", StringComparison.OrdinalIgnoreCase)
                    || role.Contains("subtitle", StringComparison.OrdinalIgnoreCase)
                        ? "DOCUMENT_LABEL"
                        : "STRUCTURAL_UNIT";

                var authorityId = AuthorityClaimId(bound.Identity);
                claims.Add(new ProjectedClaim(
                    authorityId, DisplayClaimId(authorityId), bound.Identity,
                    parts[0].SourceAlias, disposition));
            }
        }
        return claims;
    }

    /// <summary>A heading entry with every placement and role field removed.</summary>
    private static JsonElement StripFields(JsonElement entry)
    {
        var stripped = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entry.EnumerateObject())
        {
            if (property.NameEquals("relationHints") || property.NameEquals("semanticRole")
                || property.NameEquals("parentNode") || property.NameEquals("parent-node"))
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

    private static string MultiPartGoldClaim() =>
        GoldIdentities().Single(identity => identity.Contains('|'));

    private sealed record ProjectedClaim(
        string AuthorityId, string DisplayId, string Identity, string Alias, string Disposition);
}
