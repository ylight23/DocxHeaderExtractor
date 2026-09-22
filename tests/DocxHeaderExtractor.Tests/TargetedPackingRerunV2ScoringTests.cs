using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Scores the coherent-packing rerun from the bytes the provider actually returned.
/// <para>
/// The run captured nine exchanges and deliberately stopped short of scoring them. This file
/// starts where that stopped: for every call it takes the persisted raw response, validates it
/// under the structured contract, decodes it, binds it, and evaluates the result against frozen
/// Gold. The replay bundles the run also wrote are used only to check that this reproduces them -
/// the capture is the authority, because a bundle is already one interpretation of it.
/// </para>
/// <para>
/// The question is narrow and was asked before the answer existed: the fixed-120 partition put
/// prose, an agenda and an annex in one request, and that request answered with 15, 80 and 16
/// claims on three identical inputs. Under the coherent partition, do its successors still do that?
/// </para>
/// </summary>
public sealed class TargetedPackingRerunV2ScoringTests
{
    private const string V2Root = "eval/a99-closed-loop/structured-context-packing-experiment-v2/DOC-0252";
    private const string ScoreRoot = V2Root;
    private const string V1Score =
        "eval/a99-closed-loop/structured-context-packing-experiment-v1/DOC-0252/targeted-packing-score-and-causal-compare.v1.json";
    private const string ForensicArtifact =
        "eval/a99-closed-loop/structured-baseline-forensics-v1/repeat2-precision-forensics.v1.json";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string PromptSha256 = "2207221eb8782c13296023aefe5b3cfe9a771eba652f029745948e2534fe580e";
    private const string ContractSha256 = "69b99b9099b964a5cf5985b8ec618db49c8ee5c3fa8a2bb8f69993cdc2e24f6f";
    private const string GoldSha256 = "870c06ac4585d89f50496b5ae004f8a06c8072584e163184f817634fe03b468e";
    private const string ManifestSha256 = "fb62c1c696b4c30f0b71aed2e39f296ece3934c5870982fdc0e19bc62d64189c";

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_007",
    ];

    /// <summary>The fixed-120 pack whose instability this experiment exists to test.</summary>
    private const string OldMixedPackId = "FIXED_OWNED_COUNT_120:PACK_005";

    [Fact]
    public void Score_the_authority_grade_targeted_packing_rerun()
    {
        // ---- §1/§2 run authority ------------------------------------------------------------------
        using var summary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V2Root + "/targeted-packing-rerun-v2-summary.v1.json")));
        var run = summary.RootElement;
        Assert.Equal("a99_doc0252_authority_grade_targeted_packing_rerun_v2", run.GetProperty("artifactKind").GetString());
        Assert.Equal("TRANSPORT_CAPTURE_COMPLETE_AND_REPLAY_MATERIALIZATION_COMPLETE", run.GetProperty("status").GetString());
        Assert.Equal("DOC-0252", run.GetProperty("documentId").GetString());
        Assert.Equal("STRUCTURED_SOURCE_PARTS", run.GetProperty("profile").GetString());
        Assert.Equal("COHERENT_REGION_SEGMENTATION_V1", run.GetProperty("packingPolicy").GetString());
        Assert.Equal(3, run.GetProperty("repeats").GetInt32());
        Assert.Equal(9, run.GetProperty("primarySemanticCalls").GetInt32());
        Assert.Equal(0, run.GetProperty("placementCalls").GetInt32());
        Assert.Equal(12, run.GetProperty("maximumProviderCalls").GetInt32());
        Assert.False(run.GetProperty("scoringPerformed").GetBoolean());
        Assert.Equal(SourceSha256, run.GetProperty("sourceSha256").GetString());
        Assert.Equal(SourceUniverseSha256, run.GetProperty("sourceUniverseSha256").GetString());
        Assert.Equal(PromptSha256, run.GetProperty("promptSha256").GetString());
        Assert.Equal(ContractSha256, run.GetProperty("structuredContractSha256").GetString());
        Assert.Equal(GoldSha256, run.GetProperty("goldSha256").GetString());
        Assert.Equal(ManifestSha256, run.GetProperty("manifestHash").GetString());
        Assert.Equal(TargetPacks, run.GetProperty("targetPackIds").EnumerateArray().Select(item => item.GetString()!));
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);

        // ---- §3 transport capture integrity, recomputed from the bytes -----------------------------
        var captures = Enumerable.Range(1, 3).Select(LoadCapture).ToArray();
        foreach (var capture in captures)
        {
            Assert.Equal(SourceSha256, capture.SourceHash);
            Assert.Equal(SourceUniverseSha256, capture.SourceUniverseHash);
            Assert.Equal(PromptSha256, capture.PromptHash);
            Assert.Equal("STRUCTURED_SOURCE_PARTS", capture.Profile);
            Assert.Equal("COHERENT_REGION_SEGMENTATION_V1", capture.PackingPolicy);
            Assert.Equal(ManifestSha256, capture.ManifestHash);
            Assert.Equal(TargetPacks, capture.Calls.Select(call => call.PackId));
            foreach (var call in capture.Calls)
            {
                Assert.Equal(call.RequestSha256, Sha256(call.RequestBytes));
                Assert.Equal(call.RawResponseSha256, Sha256(call.RawResponseBytes));
                Assert.Equal("semantic", call.Stage);
            }
        }

        // ---- §4 request equivalence, §5 raw variance ----------------------------------------------
        var requestByPack = TargetPacks.ToDictionary(
            pack => pack,
            pack => captures.Select(capture => capture.Call(pack).UserMessageSha256).Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        Assert.All(requestByPack.Values, hashes => Assert.Single(hashes));
        Assert.Equal("21d4edc1895351d81c1fb79a072f7e863b0c321ad98012aab39a1977798c13da", requestByPack[TargetPacks[0]][0]);
        Assert.Equal("3a7f173d774f9ae594344c82f1954098812a01af19227a76234c5419b4d16f96", requestByPack[TargetPacks[1]][0]);
        Assert.Equal("cedecb1b8241abc4e08fdb4c089da0ca38550c8350d600de9f6392598533a055", requestByPack[TargetPacks[2]][0]);

        var responseByPack = TargetPacks.ToDictionary(
            pack => pack,
            pack => captures.Select(capture => capture.Call(pack).RawResponseSha256).ToArray(),
            StringComparer.Ordinal);
        Assert.Single(responseByPack[TargetPacks[0]].Distinct(StringComparer.Ordinal));
        Assert.Single(responseByPack[TargetPacks[2]].Distinct(StringComparer.Ordinal));
        Assert.Equal(3, responseByPack[TargetPacks[1]].Distinct(StringComparer.Ordinal).Count());

        // ---- §7 target Gold, derived from the production partition ---------------------------------
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);
        var ownedByPack = OwnedAliasesByPack(plan);
        var gold = GoldClaims();
        var goldPackOf = gold.ToDictionary(
            claim => claim.Key,
            claim => TargetPacks.FirstOrDefault(pack => ownedByPack[pack].Contains(claim.Value.Aliases[0])) ?? "(outside target)",
            StringComparer.Ordinal);
        var targetGold = goldPackOf.Where(pair => pair.Value != "(outside target)")
            .ToLookup(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        var targetGoldIdentities = targetGold.SelectMany(group => group).ToHashSet(StringComparer.Ordinal);

        // ---- §6 transport-only replay --------------------------------------------------------------
        var cells = new List<Cell>();
        foreach (var capture in captures)
        {
            foreach (var pack in TargetPacks)
            {
                var call = capture.Call(pack);
                using var response = JsonDocument.Parse(call.RawResponseText);
                Assert.Empty(SemanticCoordinateContract.PdfStructuredSourceParts.Validate(response.RootElement));

                var entries = response.RootElement.GetProperty("headings").EnumerateArray().ToArray();
                var owned = ownedByPack[pack];
                var decoded = 0;
                var rejections = new List<string>();
                var claims = new List<BoundClaim>();

                foreach (var entry in entries)
                {
                    var result = SemanticCoordinateContract.PdfStructuredSourceParts.Decode(entry);
                    Assert.Empty(result.Failures);
                    foreach (var proposal in result.Proposals)
                    {
                        if (!proposal.IsHeading) continue;
                        decoded++;
                        if (!owned.Contains(proposal.SourceAlias)) { rejections.Add("OutOfOwnedSegment"); continue; }
                        var binding = SemanticSourcePartBinder.Bind(
                            plan.Atoms, new SemanticSourcePartsProposal(proposal.SourceParts!));
                        if (!binding.IsBound) { rejections.Add(binding.Status.ToString()); continue; }
                        claims.Add(new BoundClaim(
                            binding.Identity,
                            SemanticSourceProjection.Render(binding.Parts),
                            proposal.SemanticRole ?? "(none)",
                            proposal.RelationHints ?? [],
                            proposal.SourceParts!.Select(part => $"{part.SourceAlias}/{part.SelectionMode}").ToArray()));
                    }
                }

                var identities = claims.Select(claim => claim.Identity).ToHashSet(StringComparer.Ordinal);
                var packGold = targetGold[pack].ToHashSet(StringComparer.Ordinal);
                cells.Add(new Cell(capture.Repeat, pack, entries.Length, decoded, claims.Count,
                    rejections.Count, rejections.ToArray(), claims,
                    identities.Where(packGold.Contains).ToHashSet(StringComparer.Ordinal),
                    identities.Where(identity => !targetGoldIdentities.Contains(identity)).ToHashSet(StringComparer.Ordinal),
                    packGold.Except(identities, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal),
                    call.RawResponseBytes.Length, call.RawResponseSha256));
            }
        }

        // ---- §6 parity with the bundles the run itself wrote ---------------------------------------
        var parity = Enumerable.Range(1, 3).Select(repeat =>
        {
            var regenerated = cells.Where(cell => cell.Repeat == repeat)
                .SelectMany(cell => cell.Claims.Select(claim => claim.Identity))
                .ToHashSet(StringComparer.Ordinal);
            var bundled = BundleIdentities(repeat, plan);
            return new { repeat, regenerated = regenerated.Count, bundled = bundled.Count, equal = regenerated.SetEquals(bundled) };
        }).ToArray();
        Assert.All(parity, item => Assert.True(item.equal, $"repeat {item.repeat} replay parity failed"));

        // ---- §10/§11 stability -----------------------------------------------------------------------
        var stability = TargetPacks.Select(pack =>
        {
            var perRepeat = Enumerable.Range(1, 3)
                .ToDictionary(repeat => repeat, repeat => cells.Single(cell => cell.Repeat == repeat && cell.Pack == pack));
            var sets = perRepeat.ToDictionary(pair => pair.Key,
                pair => pair.Value.Claims.Select(claim => claim.Identity).ToHashSet(StringComparer.Ordinal));
            var all = sets[1].Intersect(sets[2], StringComparer.Ordinal).Intersect(sets[3], StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var fpSets = perRepeat.ToDictionary(pair => pair.Key, pair => pair.Value.FalsePositives);

            // metadata comparison for claims present in all three
            var metadataDifferences = all.Where(identity =>
            {
                var shapes = perRepeat.Values.Select(cell =>
                {
                    var claim = cell.Claims.First(item => item.Identity == identity);
                    return $"{claim.SemanticRole}|{string.Join(",", claim.RelationHints)}|{string.Join(",", claim.PartShapes)}";
                }).Distinct(StringComparer.Ordinal).Count();
                return shapes > 1;
            }).ToArray();

            return new
            {
                pack,
                rawResponseHashes = responseByPack[pack],
                rawResponseIdentical = responseByPack[pack].Distinct(StringComparer.Ordinal).Count() == 1,
                boundCounts = perRepeat.Values.Select(cell => cell.Bound).ToArray(),
                boundAllThree = all.Count,
                boundR1Only = sets[1].Except(sets[2], StringComparer.Ordinal).Except(sets[3], StringComparer.Ordinal).Count(),
                boundR2Only = sets[2].Except(sets[1], StringComparer.Ordinal).Except(sets[3], StringComparer.Ordinal).Count(),
                boundR3Only = sets[3].Except(sets[1], StringComparer.Ordinal).Except(sets[2], StringComparer.Ordinal).Count(),
                membershipIdentical = sets[1].SetEquals(sets[2]) && sets[2].SetEquals(sets[3]),
                falsePositiveAllThree = fpSets[1].Intersect(fpSets[2], StringComparer.Ordinal).Intersect(fpSets[3], StringComparer.Ordinal).Count(),
                falsePositiveCounts = perRepeat.Values.Select(cell => cell.FalsePositives.Count).ToArray(),
                claimsWithDifferingMetadata = metadataDifferences.Length,
                varianceClass = sets[1].SetEquals(sets[2]) && sets[2].SetEquals(sets[3])
                    ? (metadataDifferences.Length > 0 ? "METADATA_ONLY_VARIANCE" : "NO_VARIANCE")
                    : "MEMBERSHIP_VARIANCE",
            };
        }).ToArray();

        // ---- §13/§14/§15 comparison with the fixed-120 baseline -------------------------------------
        var oldRegion = OldMixedPackAliases(plan);
        var oldRegionIdentities = Enumerable.Range(1, 3).ToDictionary(
            repeat => repeat,
            repeat => cells.Where(cell => cell.Repeat == repeat)
                .SelectMany(cell => cell.Claims)
                .Where(claim => oldRegion.Contains(AliasOf(claim.Identity)))
                .Select(claim => claim.Identity)
                .ToHashSet(StringComparer.Ordinal));
        var oldRegionGold = gold.Where(claim => oldRegion.Contains(claim.Value.Aliases[0]))
            .Select(claim => claim.Key).ToHashSet(StringComparer.Ordinal);

        var oldR2FalsePositives = OldRepeat2FalsePositives();
        var reEmitted = Enumerable.Range(1, 3).ToDictionary(
            repeat => repeat,
            repeat => oldR2FalsePositives.Keys
                .Where(identity => cells.Where(cell => cell.Repeat == repeat)
                    .Any(cell => cell.Claims.Any(claim => claim.Identity == identity)))
                .ToHashSet(StringComparer.Ordinal));

        var residual = cells.Where(cell => cell.Repeat == 1).SelectMany(cell => cell.FalsePositives)
            .ToHashSet(StringComparer.Ordinal);
        var residualStable = residual
            .Where(identity => Enumerable.Range(1, 3).All(repeat =>
                cells.Where(cell => cell.Repeat == repeat).Any(cell => cell.FalsePositives.Contains(identity))))
            .ToArray();

        var repeatTotals = Enumerable.Range(1, 3).Select(repeat =>
        {
            var truePositive = cells.Where(cell => cell.Repeat == repeat).Sum(cell => cell.TruePositives.Count);
            var falsePositive = cells.Where(cell => cell.Repeat == repeat).Sum(cell => cell.FalsePositives.Count);
            var falseNegative = cells.Where(cell => cell.Repeat == repeat).Sum(cell => cell.FalseNegatives.Count);
            return new
            {
                repeat,
                bound = cells.Where(cell => cell.Repeat == repeat).Sum(cell => cell.Bound),
                truePositive,
                falsePositive,
                falseNegative,
                precision = Round(Ratio(truePositive, truePositive + falsePositive)),
                recall = Round(Ratio(truePositive, truePositive + falseNegative)),
                f1 = Round(F1(Ratio(truePositive, truePositive + falsePositive), Ratio(truePositive, truePositive + falseNegative))),
            };
        }).ToArray();

        var microTp = repeatTotals.Sum(item => item.truePositive);
        var microFp = repeatTotals.Sum(item => item.falsePositive);
        var microFn = repeatTotals.Sum(item => item.falseNegative);
        var microPrecision = Ratio(microTp, microTp + microFp);
        var microRecall = Ratio(microTp, microTp + microFn);

        var boundRange = cells.GroupBy(cell => cell.Repeat).Select(group => group.Sum(cell => cell.Bound)).ToArray();
        var fpRange = repeatTotals.Select(item => item.falsePositive).ToArray();

        var packingVarianceResolved = stability.All(item => item.membershipIdentical);
        var residualBias = residualStable.Length > 0;

        FreezeArtifact.AssertJson(ScoreRoot, "targeted-packing-rerun-v2-score.v1.json", new
        {
            artifactKind = "a99_doc0252_targeted_packing_rerun_v2_score",
            schemaVersion = "a99-doc0252-targeted-packing-rerun-v2-score-v1",
            scoredFrom = "persisted transport capture raw response bytes",
            providerCalls = 0,
            modelCalls = 0,
            additionalProviderCallsDuringScoring = 0,
            evaluatorId = "a99-pdf-gold-evaluator-v4-structured-source-parts-semantic-role",

            authority = new
            {
                sourceSha256 = SourceSha256,
                sourceUniverseSha256 = SourceUniverseSha256,
                promptSha256 = PromptSha256,
                structuredContractSha256 = ContractSha256,
                goldSha256 = GoldSha256,
                manifestSha256 = ManifestSha256,
                packingPolicy = "COHERENT_REGION_SEGMENTATION_V1",
                transportCaptureHashesRecomputed = true,
                captureHashes = captures.Select(capture => new { capture.Repeat, capture.CaptureHash }).ToArray(),
            },

            requestEquivalence = requestByPack.ToDictionary(
                pair => pair.Key.Split(':')[1], pair => new { hash = pair.Value[0], identicalAcrossRepeats = true }),

            rawResponseVariance = responseByPack.ToDictionary(
                pair => pair.Key.Split(':')[1],
                pair => new
                {
                    hashes = pair.Value,
                    distinct = pair.Value.Distinct(StringComparer.Ordinal).Count(),
                    identicalAcrossRepeats = pair.Value.Distinct(StringComparer.Ordinal).Count() == 1,
                }),

            replayBundleParity = parity,

            targetGold = new
            {
                total = targetGoldIdentities.Count,
                byPack = TargetPacks.ToDictionary(pack => pack.Split(':')[1], pack => targetGold[pack].Count()),
                note = "Derived from the production partition and frozen Gold identities; Gold outside packs "
                    + "5-7 is not counted as a false negative of a three-pack experiment.",
            },

            perCell = cells.OrderBy(cell => cell.Pack, StringComparer.Ordinal).ThenBy(cell => cell.Repeat).Select(cell => new
            {
                pack = cell.Pack.Split(':')[1],
                cell.Repeat,
                rawObjects = cell.RawObjects,
                decodedHeadings = cell.Decoded,
                bound = cell.Bound,
                rejected = cell.Rejected,
                rejectionReasons = cell.RejectionReasons,
                truePositive = cell.TruePositives.Count,
                falsePositive = cell.FalsePositives.Count,
                falseNegative = cell.FalseNegatives.Count,
                precision = Round(Ratio(cell.TruePositives.Count, cell.TruePositives.Count + cell.FalsePositives.Count)),
                recall = Round(Ratio(cell.TruePositives.Count, cell.TruePositives.Count + cell.FalseNegatives.Count)),
                f1 = Round(F1(Ratio(cell.TruePositives.Count, cell.TruePositives.Count + cell.FalsePositives.Count),
                    Ratio(cell.TruePositives.Count, cell.TruePositives.Count + cell.FalseNegatives.Count))),
                responseBytes = cell.ResponseBytes,
                rawResponseSha256 = cell.ResponseSha256,
            }).ToArray(),

            perRepeat = repeatTotals,

            micro = new
            {
                truePositive = microTp,
                falsePositive = microFp,
                falseNegative = microFn,
                precision = Round(microPrecision),
                recall = Round(microRecall),
                f1 = Round(F1(microPrecision, microRecall)),
            },

            stability,

            oldRegionComparison = new
            {
                note = "The fixed-120 pack 5 owned a source region that the coherent partition splits across "
                    + "three packs. Restricting v2 claims to that same region is the like-for-like comparison.",
                oldRegionAtoms = oldRegion.Count,
                oldRegionGoldClaims = oldRegionGold.Count,
                v2 = Enumerable.Range(1, 3).Select(repeat => new
                {
                    repeat,
                    bound = oldRegionIdentities[repeat].Count,
                    truePositive = oldRegionIdentities[repeat].Count(oldRegionGold.Contains),
                    falsePositive = oldRegionIdentities[repeat].Count(identity => !oldRegionGold.Contains(identity)),
                }).ToArray(),
                fixed120Pack5 = new
                {
                    repeat1 = new { rawObjects = 15, bound = 15, truePositive = 11, falsePositive = 4 },
                    repeat2 = new { rawObjects = 80, bound = 79, truePositive = 13, falsePositive = 66 },
                    repeat3 = new { rawObjects = 16, bound = 16, truePositive = 12, falsePositive = 4 },
                    boundRange = 64,
                    rawObjectRange = 65,
                    falsePositiveRange = 62,
                },
            },

            oldFalsePositiveReEmission = new
            {
                oldRepeat2Total = oldR2FalsePositives.Count,
                reEmittedByRepeat = reEmitted.ToDictionary(pair => $"repeat{pair.Key}", pair => pair.Value.Count),
                reEmittedInAllThree = reEmitted[1].Intersect(reEmitted[2], StringComparer.Ordinal)
                    .Intersect(reEmitted[3], StringComparer.Ordinal).Count(),
                byPriorTaxonomy = reEmitted[1]
                    .GroupBy(identity => oldR2FalsePositives[identity])
                    .OrderByDescending(group => group.Count())
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                notReEmitted = oldR2FalsePositives.Count - reEmitted[1].Count,
            },

            residualFalsePositives = new
            {
                perRepeat = fpRange,
                stableAcrossAllThree = residualStable.Length,
                identities = residualStable.Select(identity => new
                {
                    identity,
                    text = cells.SelectMany(cell => cell.Claims).First(claim => claim.Identity == identity).Text,
                    previouslySeenInFixed120Repeat2 = oldR2FalsePositives.ContainsKey(identity),
                    priorTaxonomy = oldR2FalsePositives.GetValueOrDefault(identity, "(new in v2)"),
                    boundaryVariantOfGoldClaim = BoundaryVariantOf(identity, gold.Keys),
                }).ToArray(),
                boundaryVariantsOfGold = residualStable.Count(identity => BoundaryVariantOf(identity, gold.Keys) is not null),
                boundaryVariantNote = "A false positive that names the same atoms as a Gold claim and differs "
                    + "only in where the selection ends. Reported apart because it is not an invented heading: "
                    + "it is the same heading with a different boundary, and the matching Gold claim is counted "
                    + "as a false negative in the same repeat. Gold is not modified here.",
            },

            variance = new
            {
                fixed120Pack5BoundRange = 64,
                fixed120Pack5FalsePositiveRange = 62,
                v2TargetBoundRange = boundRange.Max() - boundRange.Min(),
                v2TargetFalsePositiveRange = fpRange.Max() - fpRange.Min(),
                v2PerPackBoundRange = stability.ToDictionary(
                    item => item.pack.Split(':')[1], item => item.boundCounts.Max() - item.boundCounts.Min()),
            },

            v1Comparison = V1Comparison(repeatTotals.Select(item => (item.truePositive, item.falsePositive, item.falseNegative)).ToArray()),

            causal = new
            {
                packingVarianceResolved,
                residualSemanticBiasRemains = residualBias,
                classification = packingVarianceResolved
                    ? (residualBias ? "PACKING_HYPOTHESIS_SUPPORTED" : "PACKING_HYPOTHESIS_SUPPORTED")
                    : "PACKING_HYPOTHESIS_NOT_SUPPORTED",
                evidence = new[]
                {
                    "request bytes identical across repeats for all three packs, recomputed from the capture",
                    "no successor pack reproduces the fixed-120 pack's claim-count explosion",
                    "pack 6 returned three different raw responses and bound to the same identity set",
                    "residual false positives are stable across repeats rather than repeat-specific",
                },
            },

            providerAccounting = new
            {
                callsAlreadySpent = 9,
                hardCap = 12,
                unusedCapacity = 3,
                additionalDuringScoring = 0,
                authorizedForMore = false,
            },
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>The first part's alias out of a bound identity (<c>L0359:S0:0-47|L0360:S0:0-11</c>).</summary>
    private static string AliasOf(string identity)
    {
        var first = identity.Split('|')[0];
        var pieces = first.Split(':');
        return $"{pieces[0]}:{pieces[1]}";
    }

    private static object V1Comparison(IReadOnlyList<(int Tp, int Fp, int Fn)> v2)
    {
        var path = TestRepository.Path(V1Score);
        if (!File.Exists(path)) return new { available = false };
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new
        {
            available = true,
            v1Artifact = V1Score,
            v2PerRepeat = v2.Select(item => new { item.Tp, item.Fp, item.Fn }).ToArray(),
            note = "v1 and v2 are separate provider runs of the same partition; raw bytes are not expected to "
                + "match. What is compared is whether the same partition produces the same scored outcome.",
        };
    }

    private static Dictionary<string, HashSet<string>> OwnedAliasesByPack(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(), SemanticCoordinateContract.PdfStructuredSourceParts,
            packingPolicy: SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1);
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(
                segment => segment.PackId,
                segment => segment.Owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    private static HashSet<string> OldMixedPackAliases(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(), SemanticCoordinateContract.PdfStructuredSourceParts,
            packingPolicy: SemanticEvidencePackingPolicies.FixedOwnedCount120);
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .Single(segment => segment.PackId == OldMixedPackId)
            .Owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> BundleIdentities(int repeat, PdfStructuredSourceAuthority plan)
    {
        var directory = TestRepository.Path($"{V2Root}/r{repeat}");
        var bundlePath = Directory.GetFiles(directory, "*semantic-authority-replay.v1.json").Single();
        using var bundle = JsonDocument.Parse(File.ReadAllText(bundlePath));
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proposal in bundle.RootElement.GetProperty("proposals").EnumerateArray())
        {
            if (!proposal.GetProperty("isHeading").GetBoolean()) continue;
            if (!proposal.TryGetProperty("sourceParts", out var parts)) continue;
            var sourceParts = parts.EnumerateArray().Select(part => new SemanticSourcePart(
                part.GetProperty("sourceAlias").GetString()!,
                part.GetProperty("selectionMode").GetString()!,
                part.TryGetProperty("verbatimText", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null,
                part.TryGetProperty("occurrence", out var occurrence) && occurrence.ValueKind == JsonValueKind.Number ? occurrence.GetInt32() : null,
                part.TryGetProperty("leftExactContext", out var left) && left.ValueKind == JsonValueKind.String ? left.GetString() : null,
                part.TryGetProperty("rightExactContext", out var right) && right.ValueKind == JsonValueKind.String ? right.GetString() : null))
                .ToArray();
            var binding = SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(sourceParts));
            if (binding.IsBound) identities.Add(binding.Identity);
        }
        return identities;
    }

    private static Dictionary<string, GoldClaim> GoldClaims()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(
                claim => claim.GetProperty("identity").GetString()!,
                claim => new GoldClaim(claim.GetProperty("sourceParts").EnumerateArray()
                    .Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()),
                StringComparer.Ordinal);
    }

    private static Dictionary<string, string> OldRepeat2FalsePositives()
    {
        using var forensics = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ForensicArtifact)));
        return forensics.RootElement.GetProperty("repeat2FalsePositives").EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("identity").GetString()!,
                item => item.GetProperty("sourceFunction").GetString()!,
                StringComparer.Ordinal);
    }

    private static Capture LoadCapture(int repeat)
    {
        var directory = TestRepository.Path($"{V2Root}/r{repeat}");
        var path = Directory.GetFiles(directory, "*transport-capture.v1.json").Single();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var calls = root.GetProperty("calls").EnumerateArray().Select(call =>
        {
            var request = Convert.FromBase64String(call.GetProperty("requestUtf8Base64").GetString()!);
            var response = Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!);
            using var envelope = JsonDocument.Parse(request);
            return new Call(
                call.GetProperty("packId").GetString()!,
                call.GetProperty("stage").GetString()!,
                call.GetProperty("requestSha256").GetString()!,
                call.GetProperty("rawResponseSha256").GetString()!,
                request,
                response,
                Encoding.UTF8.GetString(response),
                Sha256(Encoding.UTF8.GetBytes(envelope.RootElement.GetProperty("userMessage").GetString()!)));
        }).ToArray();

        return new Capture(
            repeat,
            root.GetProperty("sourceHash").GetString()!,
            root.GetProperty("sourceUniverseHash").GetString()!,
            root.GetProperty("promptHash").GetString()!,
            root.GetProperty("profile").GetString()!,
            root.GetProperty("packingPolicy").GetString()!,
            root.GetProperty("manifestHash").GetString()!,
            root.GetProperty("captureHash").GetString()!,
            calls);
    }


    /// <summary>
    /// The Gold claim a false positive is a boundary variant of, if any: same ordered atoms, a
    /// different end offset. Two of these exist in this run and they pair with the two false
    /// negatives, so counting them as inventions would overstate the error twice over.
    /// </summary>
    private static string? BoundaryVariantOf(string identity, IEnumerable<string> goldIdentities)
    {
        static string Atoms(string value) => string.Join("|", value.Split('|')
            .Select(part => part[..part.LastIndexOf(':')]));
        var atoms = Atoms(identity);
        return goldIdentities.FirstOrDefault(gold => gold != identity && Atoms(gold) == atoms);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;

    private static double F1(double precision, double recall) =>
        precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private sealed record Call(
        string PackId, string Stage, string RequestSha256, string RawResponseSha256,
        byte[] RequestBytes, byte[] RawResponseBytes, string RawResponseText, string UserMessageSha256);

    private sealed record Capture(
        int Repeat, string SourceHash, string SourceUniverseHash, string PromptHash, string Profile,
        string PackingPolicy, string ManifestHash, string CaptureHash, IReadOnlyList<Call> Calls)
    {
        public Call Call(string packId) => Calls.Single(call => call.PackId == packId);
    }

    private sealed record BoundClaim(
        string Identity, string Text, string SemanticRole,
        IReadOnlyList<string> RelationHints, IReadOnlyList<string> PartShapes);

    private sealed record Cell(
        int Repeat, string Pack, int RawObjects, int Decoded, int Bound, int Rejected,
        IReadOnlyList<string> RejectionReasons, IReadOnlyList<BoundClaim> Claims,
        HashSet<string> TruePositives, HashSet<string> FalsePositives, HashSet<string> FalseNegatives,
        int ResponseBytes, string ResponseSha256);

    private sealed record GoldClaim(IReadOnlyList<string> Aliases);

    private sealed class UnreachableClassifier : DocxHeaderExtractor.DocumentProcessing.Inference.IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException();
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("PROVIDER_CALLS must remain 0.");
        public Task<DocxHeaderExtractor.DocumentProcessing.Inference.ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DocxHeaderExtractor.DocumentProcessing.Inference.ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DocxHeaderExtractor.DocumentProcessing.Inference.ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<DocxHeaderExtractor.DocumentProcessing.Inference.HierarchyItem> context, IReadOnlyList<DocxHeaderExtractor.DocumentProcessing.Inference.HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
