using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The v2 baseline, scored from its transport captures.
/// <para>
/// Three outcomes are kept apart and never summed into one number. A claim the model never named is
/// NOT_PROPOSED - a semantic miss. A claim it named but whose coordinates the harness would not
/// accept is REFUSED_ON_SHAPE - an encoding loss. A claim that resolved is BOUND. Merging them is
/// how a coordinate defect spent a whole experiment looking like a semantic result.
/// </para>
/// <para>
/// UnexpectedVerbatimText is checked separately and must be zero. Under v2 the model cannot state a
/// selection mode, so that refusal is structurally unreachable; a non-zero count would be a
/// contract or runtime integrity defect, not a semantic miss.
/// </para>
/// </summary>
public sealed class StructuredV2TargetBaselineScoringTests
{
    private const string RunRoot = "eval/a99-closed-loop/structured-v2-target-baseline-v1/DOC-0252";
    private const string ScoreRoot = "eval/a99-closed-loop/structured-v2-target-baseline-score-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    [Fact]
    public void Score_the_structured_v2_target_baseline()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var contract = SemanticCoordinateContract.PdfStructuredSourcePartsV2;

        var segments = StructuredV2TargetBaselineTransportTests.ComposeRequests(plan);
        var gold = GoldClaims();
        var goldByPack = TargetPacks.ToDictionary(
            pack => pack,
            pack => gold.Keys
                .Where(identity => segments[pack].Owned.Contains(
                    StructuredV2TargetBaselineTransportTests.FirstAlias(identity)))
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        Assert.Equal(14, goldByPack.Values.Sum(set => set.Count));

        var cells = new List<object>();
        var perRepeat = new List<object>();
        var refusalReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var unexpectedVerbatimText = 0;
        var roleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var relationCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var calls = CapturedCalls(repeat);
            Assert.Equal(TargetPacks, calls.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
            var repeatTp = 0;
            var repeatFp = 0;
            var repeatFn = 0;
            var repeatRefusedOnShape = 0;

            foreach (var pack in TargetPacks)
            {
                using var response = JsonDocument.Parse(calls[pack].Raw);
                Assert.Empty(contract.Validate(response.RootElement));

                var owned = segments[pack].Owned;
                var boundIdentities = new HashSet<string>(StringComparer.Ordinal);
                var falsePositives = new List<object>();
                var refusals = new List<object>();
                var claimedNotHeading = 0;
                var raw = 0;

                foreach (var entry in response.RootElement.GetProperty("headings").EnumerateArray())
                {
                    raw++;
                    var decoded = contract.Decode(entry);
                    if (decoded.Proposals.Count == 0)
                    {
                        Record(refusalReasons, "Undecodable");
                        refusals.Add(new { reason = "Undecodable", detail = decoded.Failures.FirstOrDefault() });
                        continue;
                    }

                    var proposal = decoded.Proposals[0];
                    var parts = proposal.SourceParts!;
                    var role = entry.TryGetProperty("semanticRole", out var roleValue)
                        && roleValue.ValueKind == JsonValueKind.String ? roleValue.GetString()! : "(none)";
                    var isHeading = !entry.TryGetProperty("isHeading", out var flag) || flag.GetBoolean();

                    // A claim the model explicitly declines is not a coordinate outcome at all.
                    if (!isHeading) { claimedNotHeading++; Record(roleCounts, $"not-heading:{role}"); continue; }
                    Record(roleCounts, role);
                    foreach (var hint in entry.TryGetProperty("relationHints", out var hints)
                        && hints.ValueKind == JsonValueKind.Array
                        ? hints.EnumerateArray().Select(hint => hint.GetString() ?? "(null)")
                        : ["(none)"])
                    {
                        Record(relationCounts, hint);
                    }

                    if (parts.Any(part => !owned.Contains(part.SourceAlias)))
                    {
                        Record(refusalReasons, "OutOfOwnedSegment");
                        refusals.Add(new { reason = "OutOfOwnedSegment", alias = parts[0].SourceAlias });
                        repeatRefusedOnShape++;
                        continue;
                    }

                    var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, parts);
                    if (!canonical.IsCanonical)
                    {
                        Record(refusalReasons, canonical.Status.ToString());
                        refusals.Add(new
                        {
                            reason = canonical.Status.ToString(), alias = parts[0].SourceAlias,
                            detail = canonical.Reason,
                        });
                        repeatRefusedOnShape++;
                        continue;
                    }

                    var bound = SemanticSourcePartBinder.Bind(
                        plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));
                    if (!bound.IsBound)
                    {
                        Record(refusalReasons, bound.Status.ToString());
                        if (bound.Status == SemanticSourcePartsStatus.UnexpectedVerbatimText)
                            unexpectedVerbatimText++;
                        refusals.Add(new
                        {
                            reason = bound.Status.ToString(), alias = parts[0].SourceAlias,
                        });
                        repeatRefusedOnShape++;
                        continue;
                    }

                    if (gold.ContainsKey(bound.Identity)) boundIdentities.Add(bound.Identity);
                    else falsePositives.Add(new { identity = bound.Identity, role, text = Text(plan, bound.Identity) });
                }

                var tp = boundIdentities.Count;
                var fp = falsePositives.Count;
                var missing = goldByPack[pack].Except(boundIdentities, StringComparer.Ordinal)
                    .OrderBy(identity => identity, StringComparer.Ordinal).ToArray();
                repeatTp += tp;
                repeatFp += fp;
                repeatFn += missing.Length;

                cells.Add(new
                {
                    repeat,
                    pack = pack.Split(':')[1],
                    rawEntries = raw,
                    claimedNotHeading,
                    goldInPack = goldByPack[pack].Count,
                    tp, fp,
                    fn = missing.Length,
                    refusedOnShape = refusals.Count,
                    responseSha256 = calls[pack].Sha256,
                    falsePositives,
                    refusals,
                    notBound = missing.Select(identity => new
                    {
                        identity,
                        text = Text(plan, identity),
                        outcome = "NOT_PROPOSED",
                    }),
                });
            }

            perRepeat.Add(new
            {
                repeat,
                tp = repeatTp,
                fp = repeatFp,
                fn = repeatFn,
                refusedOnShape = repeatRefusedOnShape,
            });
        }

        // The integrity check: under v2 this refusal class cannot be reached by a well-formed reply.
        Assert.Equal(0, unexpectedVerbatimText);

        FreezeArtifact.AssertJson(ScoreRoot, "structured-v2-target-baseline-score.v1.json", new
        {
            artifactKind = "a99_structured_v2_target_baseline_score",
            schemaVersion = "a99-structured-v2-target-baseline-score-v1",
            baselineId = "STRUCTURED_V2_TARGET_BASELINE",
            providerCalls = 0,
            modelCalls = 0,
            scoredFrom = "transport captures; the captures are the authority and were not modified",

            authority = new
            {
                goldSha256 = GoldSha256,
                protocolVersion = contract.ProtocolVersion,
                contractSha256 = contract.SchemaHash(),
                packingPolicy = "COHERENT_REGION_SEGMENTATION_V1",
                targetPacks = TargetPacks,
                targetGoldCount = 14,
                repeats = 3,
                primaryProviderCalls = 6,
            },

            outcomeClasses = new
            {
                definition = new
                {
                    NOT_PROPOSED = "the model never named this Gold claim - a semantic miss",
                    REFUSED_ON_SHAPE = "the model named it but the harness would not accept the coordinates",
                    BOUND = "resolved to exact harness-owned coordinates",
                },
                neverSummed = "These three are different failures with different remedies and are not merged.",
            },

            integrity = new
            {
                unexpectedVerbatimText,
                structurallyUnreachable = true,
                note = "Under v2 no reply can state a selection mode, so this refusal class cannot be "
                    + "reached. A non-zero count would be a contract or runtime integrity defect, not a "
                    + "semantic miss.",
            },

            perRepeat,
            refusalReasons = refusalReasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            semanticRoles = roleCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            relationHints = relationCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            cells,

            purpose = "A comparator, not a hypothesis test. This baseline cannot pass or fail; it records "
                + "how the provider behaves under v2 so a later arm differs by exactly one clause.",
        });
    }

    private static void Record(Dictionary<string, int> counter, string key) =>
        counter[key] = counter.TryGetValue(key, out var value) ? value + 1 : 1;

    private static string Text(PdfStructuredSourceAuthority plan, string identity) =>
        string.Join(" ", identity.Split('|').Select(part =>
        {
            var alias = part[..part.LastIndexOf(':')];
            var span = part[(part.LastIndexOf(':') + 1)..].Split('-');
            var atom = plan.Atoms.FirstOrDefault(item => item.Alias == alias);
            if (atom is null) return "(unknown)";
            var start = int.Parse(span[0]);
            var end = Math.Min(int.Parse(span[1]), atom.Text.Length);
            return start >= end ? string.Empty : atom.Text[start..end];
        }));

    private static Dictionary<string, string> GoldClaims()
    {
        using var gold = CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256);
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(
                claim => claim.GetProperty("identity").GetString()!,
                claim => claim.TryGetProperty("semanticRole", out var role) && role.ValueKind == JsonValueKind.String
                    ? role.GetString()!
                    : "(none)",
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
                return new Captured(raw, CanonicalArtifactHash.OfText(raw));
            },
            StringComparer.Ordinal);
    }

    private sealed record Captured(string Raw, string Sha256);
}
