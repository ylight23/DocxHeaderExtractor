using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The coherence packing plan and its frozen experiment provenance.
/// <para>
/// The repeat-2 collapse was local: 66 of 73 false positives came from one request, whose reply
/// grew from 15 claims to 80 while the other five requests stayed put. That request is the one that
/// happens to straddle flowing prose, an agenda laid out as rows, and the start of an annex - which
/// is a property of how atoms are cut into requests, not of what the model was told. So the first
/// thing to try is a different cut with every other input held still.
/// </para>
/// <para>
/// The test consumes the same production packing capability used by the structured PDF route. It
/// freezes only the plan; it does not authorize or transport provider calls.
/// </para>
/// </summary>
public sealed class StructuredContextPackingPreflightTests
{
    private const string PreflightRoot = "eval/a99-closed-loop/structured-context-packing-preflight-v1";
    private const string RawResponses =
        "eval/a99-closed-loop/occurrence-baseline-structured-v1/raw-responses.v1.json";
    private const string ForensicArtifact =
        "eval/a99-closed-loop/structured-baseline-forensics-v1/repeat2-precision-forensics.v1.json";
    private const string RawResponsesSha256 =
        "c1122ec1361c7e2895e95261b7c83dd27e4c25933397987ecc4be490833b5df6";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Doc0252GoldSha256 =
        "870c06ac4585d89f50496b5ae004f8a06c8072584e163184f817634fe03b468e";
    private const string SourceAliasUniverseSha256 =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";

    /// <summary>
    /// The existing ownership ceiling, unchanged. The rule below only decides where a segment may
    /// begin; how many atoms one request may own is still
    /// <c>HeaderClassifierCanonicalTextModel.OwnedPerSegment</c>.
    /// </summary>
    private const int OwnedPerSegment = 120;

    /// <summary>
    /// The shortest run that is allowed to open a segment of its own, reusing the existing
    /// <c>VisibleMargin</c> rather than introducing a tuning constant. Below it, a run is an
    /// interruption inside its neighbour - a page number between paragraphs, a stray ruled line -
    /// and cutting a request at every one of those would shatter the document into fragments
    /// smaller than the context each claim needs.
    /// </summary>
    private const int MinimumSegmentRun = 20;

    [Fact]
    public void Plan_a_coherence_partition_of_the_structured_context_packs()
    {
        Assert.Equal(RawResponsesSha256, CanonicalArtifactHash.OfTextFile(TestRepository.Path(RawResponses)));
        Assert.Equal(Doc0252GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);

        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceAliasUniverseSha256, plan.SourceUniverseSha256);

        var scopeByAlias = plan.Evidence.ToDictionary(
            item => item.SourceAlias, item => item.StructuralScope, StringComparer.Ordinal);
        var blockByIndex = plan.Atoms
            .Select(atom => plan.LayoutBlockByAtom.GetValueOrDefault(atom.SourceId, atom.SourceId))
            .ToArray();
        var successor = Partition(plan);

        // Every atom the baseline showed the model is still shown to it, in exactly one pack.
        Assert.Equal(plan.Atoms.Count, successor.Sum(pack => pack.End - pack.Start + 1));
        Assert.Equal(
            Enumerable.Range(0, plan.Atoms.Count),
            successor.SelectMany(pack => Enumerable.Range(pack.Start, pack.End - pack.Start + 1)));
        Assert.All(successor, pack => Assert.True(pack.End - pack.Start + 1 <= OwnedPerSegment));

        // ---- Gold, overlaid only now that the boundaries are fixed --------------------------------
        var gold = GoldClaims();
        var atomIndexByAlias = plan.Atoms
            .Select((atom, index) => (atom.Alias, index))
            .ToDictionary(item => item.Alias, item => item.index, StringComparer.Ordinal);
        var goldByPack = new Dictionary<int, List<string>>();
        var goldSplitAcrossPacks = new List<string>();

        foreach (var claim in gold)
        {
            var packs = claim.Value.Aliases
                .Where(atomIndexByAlias.ContainsKey)
                .Select(alias => PackOf(successor, atomIndexByAlias[alias]))
                .Distinct()
                .ToArray();
            Assert.NotEmpty(packs);
            if (packs.Length > 1) goldSplitAcrossPacks.Add(claim.Key);
            goldByPack.TryAdd(packs[0], []);
            goldByPack[packs[0]].Add(claim.Key);
        }
        Assert.Equal(41, gold.Count);
        Assert.Equal(41, goldByPack.Values.Sum(list => list.Count));

        // ---- where the old repeat-2 false positives land -------------------------------------------
        var oldFalsePositives = OldRepeat2FalsePositives();
        var fpByPack = oldFalsePositives
            .Select(item => PackOf(successor, atomIndexByAlias.GetValueOrDefault(item.Alias, -1)))
            .GroupBy(pack => pack)
            .OrderBy(group => group.Key)
            .ToDictionary(group => $"pack{group.Key + 1}", group => group.Count());

        var oldPack5 = plan.Packs[4].OwnedAliases.Select(alias => atomIndexByAlias[alias]).ToArray();
        var oldPack5Range = (Start: oldPack5.Min(), End: oldPack5.Max());
        var successorsOfOldPack5 = successor
            .Where(pack => pack.Start <= oldPack5Range.End && pack.End >= oldPack5Range.Start)
            .ToArray();

        FreezeArtifact.AssertJson(PreflightRoot, "context-packing-intervention.v1.json", new
        {
            artifactKind = "a99_structured_context_packing_preflight",
            schemaVersion = "a99-structured-context-packing-preflight-v1",
            experimentLineage = "successor to A99-S2P-STRUCTURED-BASELINE-V1; the baseline is not replaced",
            providerCalls = 0,
            modelCalls = 0,
            providerAuthorized = false,

            hypothesis = new
            {
                claim = "The repeat-2 precision collapse is a property of one request's composition, not of "
                    + "the model's instructions. One request owned flowing prose, an agenda laid out as rows, "
                    + "and the opening of an annex; its reply grew from 15 claims to 80 while the other five "
                    + "requests stayed within one claim of each other.",
                intervention = "Change where requests begin. Nothing else: same atoms, same evidence, same "
                    + "prompt, same contract, same candidate hints, same binder, same evaluator, same model.",
                falsifiable = "If a successor pack derived from old pack 5 still explodes, composition was not "
                    + "the cause and the next thing to test is what the prompt says about repeated structural "
                    + "rows - which this experiment deliberately does not touch, so that the answer means something.",
            },

            predecessorAuthority = new
            {
                sourceAliasUniverseSha256 = SourceAliasUniverseSha256,
                modelVisibleEvidenceSha256 = plan.ModelVisibleEvidenceHash,
                callPlanSha256 = "e532682353ca2ea0c73ec660baedb880fdfdafa9a5de7a6e61b242ddbdb648ed",
                providerModelInputPlanSha256 = "a73e9e3c1fbeb4a83f1937e864fb310e4a0402b993252b82bb6a741ce4ff7dc3",
                status = "BASELINE_PREDECESSOR - these two describe the six-pack partition and the bytes it "
                    + "produced. A successor partition changes both by construction; they remain the identity "
                    + "of the run already scored and are not carried forward as active gates.",
                unchangedByThisIntervention = new[]
                {
                    "SOURCE_ALIAS_UNIVERSE_HASH", "MODEL_VISIBLE_EVIDENCE content universe",
                    "GOLD", "PROMPT", "CONTRACT", "CANDIDATE_HINTS", "BINDER", "EVALUATOR", "MODEL",
                },
                changedByThisIntervention = new[] { "CALL_PLAN_HASH", "PROVIDER_MODEL_INPUT_PLAN_HASH" },
            },

            rule = new
            {
                id = "COHERENT_REGION_SEGMENTATION_V1",
                statement = "A request may not span a sustained change of layout regime or of annex membership. "
                    + "Two signals, both already computed for every atom, and neither derived from this document: "
                    + "(1) layout regime - whether the atom's layout block holds only that atom, which "
                    + "distinguishes a region set as rows from one set as wrapped paragraphs; (2) annex "
                    + "membership - whether the parser's structural scope is an appendix scope. A run shorter "
                    + "than the visible margin is an interruption and stays inside its neighbour. Each segment "
                    + "is then cut into requests by the unchanged ownership ceiling.",
                signals = new[] { "layout block cardinality (existing LayoutBlockByAtom)", "structural scope family (existing PdfCandidateContext scope)" },
                constants = new { ownedPerSegment = OwnedPerSegment, minimumSegmentRun = MinimumSegmentRun, note = "Both already exist; MinimumSegmentRun reuses VisibleMargin's value rather than adding a tuned threshold." },
                forbiddenInputs = new[]
                {
                    "document id", "file name", "heading text or keywords", "Gold aliases",
                    "known false-positive aliases", "page numbers specific to a document",
                },
                goldUsedForBoundaries = false,
            },

            sourceIntegrity = new
            {
                sourceAtomsBefore = plan.Atoms.Count,
                sourceAtomsAfter = successor.Sum(pack => pack.End - pack.Start + 1),
                sourceAliasUniverseSha256 = plan.SourceUniverseSha256,
                everyAtomOwnedExactlyOnce = true,
                atomsRemoved = 0,
                candidateHintsChanged = false,
                hintedAtoms = plan.Evidence.Count(item => item.CandidateAttention.HeuristicMatch),
                totalAtoms = plan.Atoms.Count,
                hintNote = "650/650 atoms are hinted today, so the hint separates nothing. Left exactly as it "
                    + "is: changing packing and hints together would make the next result unattributable.",
                responseProtocolChanged = false,
                responseProtocolNote = "Captured replies carried only isHeading=true entries. The contract "
                    + "permits a positives-only list, so this is recorded rather than corrected here.",
            },

            oldPartition = plan.Packs.Select(pack => new
            {
                pack = pack.Index + 1,
                atoms = pack.OwnedAliases.Count,
                firstAlias = pack.OwnedAliases[0],
                lastAlias = pack.OwnedAliases[^1],
            }).ToArray(),

            successorPartition = successor.Select((pack, index) => Describe(
                plan, scopeByAlias, blockByIndex, pack, index)).ToArray(),

            oldPack5 = new
            {
                atomRange = new { start = oldPack5Range.Start, end = oldPack5Range.End },
                atoms = oldPack5.Length,
                splitInto = successorsOfOldPack5.Select(pack => new
                {
                    pack = Array.IndexOf(successor, pack) + 1,
                    atomRange = new { pack.Start, pack.End },
                    key = pack.Key,
                    atomsFromOldPack5 = Math.Min(pack.End, oldPack5Range.End) - Math.Max(pack.Start, oldPack5Range.Start) + 1,
                }).ToArray(),
            },

            oldRepeat2FalsePositiveMapping = new
            {
                total = oldFalsePositives.Count,
                bySuccessorPack = fpByPack,
                note = "Diagnostic only. The captured replies answered the six-pack partition and are not "
                    + "replies to these packs; this says where those source rows now sit, not what the model "
                    + "would say about them.",
            },

            goldOverlay = new
            {
                claims = gold.Count,
                representable = gold.Count,
                identitiesChanged = false,
                claimsSplitAcrossPacks = goldSplitAcrossPacks.Count,
                byPack = goldByPack.OrderBy(pair => pair.Key)
                    .ToDictionary(pair => $"pack{pair.Key + 1}", pair => pair.Value.Count),
            },

            coherence = new
            {
                note = "Raw counts, not a score: how many structural scopes and layout regimes each request "
                    + "would own. The baseline's pack 5 owned five scopes and both regimes.",
                oldPack5Scopes = plan.Packs[4].OwnedAliases
                    .GroupBy(alias => scopeByAlias.GetValueOrDefault(alias, "?"))
                    .OrderByDescending(group => group.Count())
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                successorScopeCounts = successor.Select((pack, index) => new
                {
                    pack = index + 1,
                    distinctScopes = Enumerable.Range(pack.Start, pack.End - pack.Start + 1)
                        .Select(atom => scopeByAlias.GetValueOrDefault(plan.Atoms[atom].Alias, "?"))
                        .Distinct(StringComparer.Ordinal).Count(),
                    regime = pack.Key,
                }).ToArray(),
            },

            callPlan = new
            {
                doc0252 = new
                {
                    semanticCallsPerRepeat = successor.Length,
                    placementCallsPerRepeat = "NOT_DETERMINABLE_OFFLINE",
                    placementObservedUnderOldPacking = 1,
                    placementAllowancePerRepeat = 2,
                    totalCallsPerRepeatAtAllowance = successor.Length + 2,
                },
                doc0001 = new
                {
                    semanticCallsPerRepeat = 1,
                    placementObservedOffline = new[] { 0, 0, 1 },
                    placementAllowancePerRepeat = 1,
                    totalCallsPerRepeatAtAllowance = 2,
                    note = "Unchanged by this intervention: DOCX packing is not touched. Measured by replaying "
                        + "its three captured replies through the production DOCX adapter.",
                },
                repeats = 3,
                fullSuccessorPrimaryCalls = (successor.Length + 1) * 3,
                fullSuccessorCallsAtAllowance = ((successor.Length + 2) + 2) * 3,
                proposedHardCap = ((successor.Length + 2) + 2) * 3 + 6,
                capRationale = "Semantic calls are deterministic from the partition. Placement is not: it fires "
                    + "only when headings remain unplaced, so it depends on replies that do not exist yet. The "
                    + "cap is the deterministic count plus a stated placement allowance plus six, and it is not "
                    + "inherited from the previous run: 21/27 described extraction under a six-pack partition "
                    + "before placement was reachable at all.",
                previousAuthorization = new
                {
                    calls = 21,
                    cap = 27,
                    unusedCapacity = 6,
                    transferable = false,
                    note = "The six unused calls are not authorization for this experiment.",
                },
            },

            targetedExperiment = new
            {
                question = "Does splitting old pack 5 remove its instability?",
                packs = successorsOfOldPack5.Select(pack => Array.IndexOf(successor, pack) + 1).ToArray(),
                targetPacksPerRepeat = successorsOfOldPack5.Length,
                repeats = 3,
                targetPrimaryCalls = successorsOfOldPack5.Length * 3,
                targetProposedHardCap = successorsOfOldPack5.Length * 3 + 3,
                placement = "not exercised - a partial-document extraction has no document-wide placement pass",
                note = "Chosen as the successors of the request that failed, not sized to fit any leftover "
                    + "capacity.",
            },

            successCriteria = new
            {
                declaredBefore = "any successor provider output exists",
                primaryComparison = "each successor pack derived from old pack 5, against old pack 5's own "
                    + "three repeats: 15 claims / 11 TP / 4 FP, 80 / 13 / 66, 16 / 12 / 4.",
                measures = new[]
                {
                    "heading proposal count per pack per repeat",
                    "bound proposal count per pack per repeat",
                    "TP, FP, FN per pack per repeat",
                    "false-positive identities per pack per repeat",
                    "response size in characters per call",
                    "cross-repeat variance of claim count per pack",
                },
                rules = new[]
                {
                    "no repeat is discarded as an outlier",
                    "no repeat is selected for being the best",
                    "all three repeats enter the result",
                    "the baseline is not rescored or replaced",
                },
                whatWouldFalsifyTheHypothesis = "a successor pack derived from old pack 5 showing the same "
                    + "order-of-magnitude claim-count jump in any repeat",
            },
        });
    }

    /// <summary>
    /// The preflight consumes the production policy directly. This keeps the frozen plan and the
    /// execution path on one deterministic partition implementation.
    /// </summary>
    private static Segment[] Partition(PdfStructuredSourceAuthority plan)
    {
        return SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1
            .BuildPacks(plan.Evidence, plan.LayoutBlockByAtom)
            .Select(pack => new Segment(
                pack.Owned[0].SourceOrdinal,
                pack.Owned[^1].SourceOrdinal,
                pack.RegionKey))
            .ToArray();
    }

    private static object Describe(
        PdfStructuredSourceAuthority plan,
        IReadOnlyDictionary<string, string> scopeByAlias,
        IReadOnlyList<string> blockByIndex,
        Segment pack,
        int index)
    {
        var range = Enumerable.Range(pack.Start, pack.End - pack.Start + 1).ToArray();
        return new
        {
            pack = index + 1,
            atomCount = range.Length,
            atomRange = new { pack.Start, pack.End },
            firstAlias = plan.Atoms[pack.Start].Alias,
            lastAlias = plan.Atoms[pack.End].Alias,
            pageRange = new { first = plan.Atoms[pack.Start].Page, last = plan.Atoms[pack.End].Page },
            regime = pack.Key,
            layoutBlocks = range.Select(atom => blockByIndex[atom]).Distinct(StringComparer.Ordinal).Count(),
            scopes = range.GroupBy(atom => scopeByAlias.GetValueOrDefault(plan.Atoms[atom].Alias, "?"))
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            characters = range.Sum(atom => plan.Atoms[atom].Text.Length),
            predecessorPacks = plan.Packs
                .Where(old => old.OwnedAliases.Any(alias => range.Any(atom => plan.Atoms[atom].Alias == alias)))
                .Select(old => old.Index + 1).ToArray(),
        };
    }

    private static int PackOf(IReadOnlyList<Segment> packs, int atomIndex)
    {
        for (var index = 0; index < packs.Count; index++)
            if (atomIndex >= packs[index].Start && atomIndex <= packs[index].End) return index;
        return -1;
    }

    private static Dictionary<string, GoldClaim> GoldClaims()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(
                claim => claim.GetProperty("identity").GetString()!,
                claim => new GoldClaim(
                    claim.GetProperty("sourceParts").EnumerateArray()
                        .Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()),
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<(string Alias, int OldPack)> OldRepeat2FalsePositives()
    {
        using var forensics = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ForensicArtifact)));
        return forensics.RootElement.GetProperty("repeat2FalsePositives").EnumerateArray()
            .Select(item => (
                item.GetProperty("aliases")[0].GetString()!,
                item.GetProperty("Pack").GetInt32()))
            .ToArray();
    }

    private sealed record Segment(int Start, int End, string Key);

    private sealed record GoldClaim(IReadOnlyList<string> Aliases);
}
