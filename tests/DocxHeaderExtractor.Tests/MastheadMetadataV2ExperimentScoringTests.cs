using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline score for EXP_MASTHEAD_METADATA_V2. The six transport captures are the immutable input;
/// this test never constructs a provider and never changes the transport artifacts.
/// </summary>
public sealed class MastheadMetadataV2ExperimentScoringTests
{
    private const string RunRoot =
        "eval/a99-closed-loop/exp-masthead-metadata-v2-experiment-v1/DOC-0252";
    private const string ScoreRoot = "eval/a99-closed-loop/exp-masthead-metadata-v2-score-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string E1ClauseSha256 =
        "81d2c37cdf8542d9948396e59c653d017f69a8d1591b86279b85114b4cf6bfa8";
    private const string ExperimentPromptSha256 =
        "fb46d62cb7ddbb469d56a54f3c90e37d955c8fce85b8f75257ac19a14b2c64fa";
    private const string ProviderModelInputPlanSha256 =
        "d788652788771cc6998fd5d94d531d07e939599bab2ae4b59b702aca7b3d037d";
    private const string Pack005ProviderRequestSha256 =
        "173a5172a58018ac616b4b06c79f20c6f314db214cc406610da2937fe03b1977";
    private const string Pack006ProviderRequestSha256 =
        "ac7f6905cfcf5377c92241d089815ab29bc4bb6db0fc40b17e67a953c0dbeec7";
    private const string ManifestSha256 =
        "fb62c1c696b4c30f0b71aed2e39f296ece3934c5870982fdc0e19bc62d64189c";

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    private static readonly IReadOnlyDictionary<string, string[]> MastheadFamilies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["international-program-and-tag"] =
            ["L0513:S0:0-38|L0514:S0:0-30", "L0513:S0:0-38", "L0514:S0:0-30"],
            ["hybrid-meeting"] = ["L0515:S0:0-14"],
            ["date-venue-address"] =
            ["L0516:S0:0-15|L0517:S0:0-22|L0518:S0:0-32", "L0516:S0:0-15", "L0517:S0:0-22", "L0518:S0:0-32"],
        };

    private static readonly IReadOnlyDictionary<string, string[][]> MastheadFamilyVariants =
        new Dictionary<string, string[][]>(StringComparer.Ordinal)
        {
            ["international-program-and-tag"] =
            [
                ["L0513:S0:0-38|L0514:S0:0-30"],
                ["L0513:S0:0-38", "L0514:S0:0-30"],
            ],
            ["hybrid-meeting"] = [["L0515:S0:0-14"]],
            ["date-venue-address"] =
            [
                ["L0516:S0:0-15|L0517:S0:0-22|L0518:S0:0-32"],
                ["L0516:S0:0-15", "L0517:S0:0-22", "L0518:S0:0-32"],
            ],
        };

    private static readonly string[] RelationControls =
        ["L0400", "L0420", "L0470", "L0507"];

    [Fact]
    public void Score_the_approved_masthead_metadata_v2_experiment_offline()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var contract = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        var segments = ComposeRequests(plan);
        var gold = GoldClaims();
        var goldByPack = TargetPacks.ToDictionary(
            pack => pack,
            pack => gold.Keys.Where(identity => segments[pack].Owned.Contains(
                    FirstAlias(identity)))
                .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Equal(14, goldByPack.Values.Sum(set => set.Count));

        var perRepeat = new List<object>();
        var cells = new List<object>();
        var refusalReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var roleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var relationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var outcomeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var unexpectedVerbatimText = 0;
        var allRoleMismatches = new List<object>();

        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var calls = CapturedCalls(repeat);
            Assert.Equal(TargetPacks, calls.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
            var repeatTp = 0;
            var repeatFp = 0;
            var repeatFn = 0;
            var repeatRefused = 0;
            var repeatOutcomes = new Dictionary<string, int>(StringComparer.Ordinal);
            var repeatFamilyOutcomes = new Dictionary<string, string>(StringComparer.Ordinal);
            var repeatBoundIdentities = new HashSet<string>(StringComparer.Ordinal);

            foreach (var pack in TargetPacks)
            {
                using var response = JsonDocument.Parse(calls[pack].Raw);
                Assert.Empty(contract.Validate(response.RootElement));

                var owned = segments[pack].Owned;
                var boundClaims = new Dictionary<string, BoundClaim>(StringComparer.Ordinal);
                var refusedAliases = new HashSet<string>(StringComparer.Ordinal);
                var refusalDetails = new List<object>();
                var notHeading = 0;
                var rawEntries = 0;

                foreach (var entry in response.RootElement.GetProperty("headings").EnumerateArray())
                {
                    rawEntries++;
                    var decoded = contract.Decode(entry);
                    if (decoded.Proposals.Count == 0)
                    {
                        Record(refusalReasons, "Undecodable");
                        refusalDetails.Add(new { reason = "Undecodable", detail = decoded.Failures.FirstOrDefault() });
                        continue;
                    }

                    var proposal = decoded.Proposals[0];
                    var parts = proposal.SourceParts!;
                    var role = entry.TryGetProperty("semanticRole", out var roleValue) &&
                        roleValue.ValueKind == JsonValueKind.String ? roleValue.GetString()! : "(none)";
                    var isHeading = !entry.TryGetProperty("isHeading", out var flag) || flag.GetBoolean();
                    if (!isHeading)
                    {
                        notHeading++;
                        Record(roleCounts, $"not-heading:{role}");
                        continue;
                    }

                    Record(roleCounts, role);
                    foreach (var hint in entry.TryGetProperty("relationHints", out var hints) &&
                        hints.ValueKind == JsonValueKind.Array
                            ? hints.EnumerateArray().Select(item => item.GetString() ?? "(null)")
                            : ["(none)"])
                        Record(relationCounts, hint);

                    foreach (var alias in parts.Select(part => part.SourceAlias))
                        if (!owned.Contains(alias)) refusedAliases.Add(alias);

                    var refusal = TryBind(plan, owned, parts, out var identity, out var text, out var reason);
                    if (!refusal)
                    {
                        Record(refusalReasons, reason!);
                        foreach (var alias in parts.Select(part => part.SourceAlias)) refusedAliases.Add(alias);
                        refusalDetails.Add(new { reason, aliases = parts.Select(part => part.SourceAlias).ToArray() });
                        if (reason == nameof(SemanticSourcePartsStatus.UnexpectedVerbatimText)) unexpectedVerbatimText++;
                        repeatRefused++;
                        continue;
                    }

                    var claim = new BoundClaim(identity!, role, text!,
                        entry.TryGetProperty("relationHints", out var relation) &&
                        relation.ValueKind == JsonValueKind.Array
                            ? relation.EnumerateArray().Select(item => item.GetString() ?? "(null)").ToArray()
                            : []);
                    boundClaims[identity!] = claim;
                }

                var goldInPack = goldByPack[pack];
                var outcomes = goldInPack.Select(identity =>
                {
                    var outcome = boundClaims.ContainsKey(identity)
                        ? "BOUND"
                        : refusedAliases.Contains(FirstAlias(identity))
                            ? "REFUSED_ON_SHAPE"
                            : "NOT_PROPOSED";
                    Record(outcomeCounts, outcome);
                    Record(repeatOutcomes, outcome);
                    if (outcome == "BOUND" && gold[identity] != boundClaims[identity].Role)
                        allRoleMismatches.Add(new { repeat, pack = pack.Split(':')[1], identity,
                            expected = gold[identity], actual = boundClaims[identity].Role });
                    return new { identity, outcome, expectedSemanticRole = gold[identity] };
                }).ToArray();

                var tp = boundClaims.Keys.Count(goldInPack.Contains);
                var fp = boundClaims.Keys.Count(identity => !goldInPack.Contains(identity));
                repeatBoundIdentities.UnionWith(boundClaims.Keys);
                var missing = outcomes.Where(item => item.outcome != "BOUND").ToArray();
                repeatTp += tp;
                repeatFp += fp;
                repeatFn += missing.Length;

                foreach (var alias in RelationControls)
                {
                    foreach (var claim in boundClaims.Values.Where(claim => claim.Identity.StartsWith(alias + ":", StringComparison.Ordinal)))
                        relationCounts[$"control:{alias}:{string.Join("|", claim.RelationHints)}"] =
                            relationCounts.GetValueOrDefault($"control:{alias}:{string.Join("|", claim.RelationHints)}") + 1;
                }

                cells.Add(new
                {
                    repeat,
                    pack = pack.Split(':')[1],
                    rawEntries,
                    notHeading,
                    goldInPack = goldInPack.Count,
                    bound = tp,
                    falsePositive = fp,
                    falseNegative = missing.Length,
                    refusedOnShape = refusalDetails.Count,
                    refusalReasons = refusalDetails,
                    outcomes,
                    falsePositiveIdentities = boundClaims.Keys.Where(identity => !goldInPack.Contains(identity))
                        .Select(identity => new
                        {
                            identity,
                            role = boundClaims[identity].Role,
                            relationHints = boundClaims[identity].RelationHints,
                        }).ToArray(),
                    responseSha256 = calls[pack].Sha256,
                    responseBytes = calls[pack].Raw.Length,
                });
            }

            foreach (var family in MastheadFamilyVariants)
                repeatFamilyOutcomes[family.Key] = FamilyOutcome(family.Value, repeatBoundIdentities);

            perRepeat.Add(new
            {
                repeat,
                truePositive = repeatTp,
                falsePositive = repeatFp,
                falseNegative = repeatFn,
                refusedOnShape = repeatRefused,
                outcomes = repeatOutcomes,
                mastheadFamilies = repeatFamilyOutcomes,
            });
        }

        Assert.Equal(0, unexpectedVerbatimText);

        FreezeArtifact.AssertJson(ScoreRoot, "exp-masthead-metadata-v2-score.v1.json", new
        {
            artifactKind = "a99_exp_masthead_metadata_v2_score",
            schemaVersion = "a99-exp-masthead-metadata-v2-score-v1",
            experimentId = "EXP_MASTHEAD_METADATA_V2",
            providerCalls = 0,
            modelCalls = 0,
            additionalProviderCallsDuringScoring = 0,
            scoredFrom = "the experiment's own replay-complete transport captures",
            authority = new
            {
                e1ClauseSha256 = E1ClauseSha256,
                experimentPromptSha256 = ExperimentPromptSha256,
                providerModelInputPlanSha256 = ProviderModelInputPlanSha256,
                pack005ProviderRequestSha256 = Pack005ProviderRequestSha256,
                pack006ProviderRequestSha256 = Pack006ProviderRequestSha256,
                manifestHash = ManifestSha256,
                goldSha256 = GoldSha256,
                protocolVersion = contract.ProtocolVersion,
                contractSha256 = contract.SchemaHash(),
                targetPacks = TargetPacks,
                targetGoldCount = 14,
                repeats = 3,
                primaryProviderCalls = 6,
                placementCalls = 0,
            },
            outcomeClasses = new
            {
                NOT_PROPOSED = "the Gold claim was not named by the model",
                REFUSED_ON_SHAPE = "the model named its source alias but binding rejected the shape",
                BOUND = "the proposal resolved to exact harness-owned coordinates",
                neverSummed = true,
            },
            integrity = new
            {
                unexpectedVerbatimText,
                mustBeZero = true,
                note = "v2 derives selection mode in the harness; UnexpectedVerbatimText is a contract/runtime defect.",
            },
            semanticFamilies = MastheadFamilyVariants.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            relationControls = RelationControls,
            outcomeCounts = outcomeCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            refusalReasons = refusalReasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            perRepeat,
            cells,
            semanticRoleMismatches = allRoleMismatches,
            providerAccounting = new
            {
                callsSpent = 6,
                hardCap = 9,
                unusedCapacity = 3,
                authorizedForMore = false,
            },
        });
    }

    private static bool TryBind(PdfStructuredSourceAuthority plan, HashSet<string> owned,
        IReadOnlyList<SemanticSourcePart> parts, out string? identity, out string? text, out string? reason)
    {
        identity = null;
        text = null;
        reason = null;
        if (parts.Any(part => !owned.Contains(part.SourceAlias)))
        {
            reason = "OutOfOwnedSegment";
            return false;
        }

        var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, parts);
        if (!canonical.IsCanonical)
        {
            reason = canonical.Status.ToString();
            return false;
        }

        var bound = SemanticSourcePartBinder.Bind(plan.Atoms,
            new SemanticSourcePartsProposal(canonical.Parts));
        if (!bound.IsBound)
        {
            reason = bound.Status.ToString();
            return false;
        }

        identity = bound.Identity;
        text = SemanticSourceProjection.Render(bound.Parts);
        return true;
    }

    private static Dictionary<string, MastheadMetadataV2ExperimentTransportTests.Segment> ComposeRequests(
        PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            SemanticCoordinateContract.PdfStructuredSourcePartsV2,
            CanonicalSemanticExperiment.NonStructuralMetadataConstrained,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment =>
                new MastheadMetadataV2ExperimentTransportTests.Segment(
                    segment.RequestBytes,
                    segment.Owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal)),
                StringComparer.Ordinal);
    }

    private static string FirstAlias(string identity)
    {
        var first = identity.Split('|')[0];
        return first[..first.LastIndexOf(':')];
    }

    private static string FamilyOutcome(IEnumerable<string[]> variants, ISet<string> bound) =>
        variants.Any(variant => variant.All(bound.Contains)) ? "BOUND" : "NOT_BOUND";

    private static void Record(Dictionary<string, int> counter, string key) =>
        counter[key] = counter.GetValueOrDefault(key) + 1;

    private static Dictionary<string, string> GoldClaims()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(claim => claim.GetProperty("identity").GetString()!,
                claim => claim.TryGetProperty("semanticRole", out var role) &&
                    role.ValueKind == JsonValueKind.String ? role.GetString()! : "(none)",
                StringComparer.Ordinal);
    }

    private static Dictionary<string, Captured> CapturedCalls(int repeat)
    {
        var directory = TestRepository.Path($"{RunRoot}/r{repeat}");
        var path = Directory.GetFiles(directory, "*transport-capture.v1.json").Single();
        using var capture = JsonDocument.Parse(File.ReadAllText(path));
        return capture.RootElement.GetProperty("calls").EnumerateArray().ToDictionary(
            call => call.GetProperty("packId").GetString()!,
            call =>
            {
                var raw = Encoding.UTF8.GetString(
                    Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!));
                Assert.Equal(call.GetProperty("rawResponseSha256").GetString(), CanonicalArtifactHash.OfText(raw));
                return new Captured(raw, CanonicalArtifactHash.OfText(raw));
            }, StringComparer.Ordinal);
    }

    private sealed record Captured(string Raw, string Sha256);
    private sealed record BoundClaim(string Identity, string Role, string Text, string[] RelationHints);

    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException("Scoring must not contact a provider.");
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage,
            CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("Scoring must not contact a provider.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
