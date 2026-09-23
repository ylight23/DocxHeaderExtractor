using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EXP_MASTHEAD_METADATA, scored from its own captured bytes against the arm it was meant to improve.
/// <para>
/// The clause did not do what it was written to do, and it did something it was not written to do.
/// Both are recorded here in full, because an intervention that fails is only useful if the failure
/// is legible: the masthead claims it targeted are still emitted in every repeat, and one repeat
/// lost four approved headings to a reply shape the binder refuses.
/// </para>
/// <para>
/// Refusals are counted, not skipped. The first reading of this run reported one pack as finding
/// nothing, which was wrong - the model named all four headings correctly and the harness rejected
/// them - and that is exactly the confusion this file exists to prevent.
/// </para>
/// </summary>
public sealed class MastheadMetadataExperimentScoringTests
{
    private const string ExperimentRoot = "eval/a99-closed-loop/exp-masthead-metadata-experiment-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string ExperimentPromptSha256 =
        "78396aa66a9dad6baef0cb24ee3468d9bfc740a4216acf3c51659c89eba54e5d";
    private const string PredecessorPromptSha256 =
        "2207221eb8782c13296023aefe5b3cfe9a771eba652f029745948e2534fe580e";

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    /// <summary>The three claims this arm was authorized to remove.</summary>
    private static readonly string[] MastheadTargets =
    [
        "L0513:S0:0-38|L0514:S0:0-30",
        "L0515:S0:0-14",
        "L0516:S0:0-15|L0517:S0:0-22|L0518:S0:0-32",
    ];

    [Fact]
    public void Score_the_masthead_metadata_experiment()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));

        var owned = OwnedByPack(plan);
        var gold = GoldIdentities();
        var goldByPack = TargetPacks.ToDictionary(
            pack => pack,
            pack => gold.Where(identity => owned[pack].Contains(FirstAlias(identity)))
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        Assert.Equal(4, goldByPack[TargetPacks[0]].Count);
        Assert.Equal(10, goldByPack[TargetPacks[1]].Count);

        var cells = new List<object>();
        var perRepeat = new List<object>();
        var mastheadStillEmitted = new Dictionary<string, int>(StringComparer.Ordinal);
        var lostGold = new List<object>();

        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var calls = CapturedCalls(repeat);
            Assert.Equal(TargetPacks, calls.Keys);
            var repeatTp = 0;
            var repeatFp = 0;
            var repeatFn = 0;

            foreach (var pack in TargetPacks)
            {
                using var response = JsonDocument.Parse(calls[pack].Raw);
                Assert.Empty(SemanticCoordinateContract.PdfStructuredSourceParts.Validate(response.RootElement));

                var bound = new List<Claim>();
                var refusals = new List<string>();
                var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
                var headings = 0;

                foreach (var entry in response.RootElement.GetProperty("headings").EnumerateArray())
                {
                    var decoded = SemanticCoordinateContract.PdfStructuredSourceParts.Decode(entry);
                    Assert.Empty(decoded.Failures);
                    foreach (var proposal in decoded.Proposals.Where(item => item.IsHeading))
                    {
                        headings++;
                        foreach (var part in proposal.SourceParts!)
                        {
                            var shape = $"{part.SelectionMode}+verbatimText={part.VerbatimText is not null}";
                            shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
                        }
                        if (!owned[pack].Contains(proposal.SourceAlias)) { refusals.Add("OutOfOwnedSegment"); continue; }

                        var binding = SemanticSourcePartBinder.Bind(
                            plan.Atoms, new SemanticSourcePartsProposal(proposal.SourceParts!));
                        if (!binding.IsBound) { refusals.Add(binding.Status.ToString()); continue; }
                        bound.Add(new Claim(binding.Identity,
                            SemanticSourceProjection.Render(binding.Parts),
                            string.Join(",", proposal.RelationHints ?? [])));
                    }
                }

                var identities = bound.Select(claim => claim.Identity).ToHashSet(StringComparer.Ordinal);
                var truePositive = identities.Count(goldByPack[pack].Contains);
                var falsePositive = identities.Count - truePositive;
                var missed = goldByPack[pack].Except(identities, StringComparer.Ordinal).ToArray();

                repeatTp += truePositive;
                repeatFp += falsePositive;
                repeatFn += missed.Length;

                foreach (var target in MastheadTargets.Where(identities.Contains))
                    mastheadStillEmitted[target] = mastheadStillEmitted.GetValueOrDefault(target) + 1;

                if (missed.Length > 0)
                {
                    lostGold.Add(new
                    {
                        repeat,
                        pack = pack.Split(':')[1],
                        lost = missed,
                        cause = refusals.Count > 0 ? refusals.Distinct(StringComparer.Ordinal).ToArray() : ["notProposed"],
                    });
                }

                cells.Add(new
                {
                    repeat,
                    pack = pack.Split(':')[1],
                    headingProposals = headings,
                    bound = bound.Count,
                    refused = refusals.Count,
                    refusalReasons = refusals.GroupBy(reason => reason, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                    partShapes = shapes,
                    truePositive,
                    falsePositive,
                    falseNegative = missed.Length,
                    treeLessClaims = bound.Count(claim => claim.Hints.Contains("NONE", StringComparison.Ordinal)),
                    falsePositiveIdentities = identities.Where(identity => !goldByPack[pack].Contains(identity))
                        .Select(identity => new
                        {
                            identity,
                            text = bound.First(claim => claim.Identity == identity).Text,
                            hints = bound.First(claim => claim.Identity == identity).Hints,
                            isMastheadTarget = MastheadTargets.Contains(identity, StringComparer.Ordinal),
                        }).ToArray(),
                    responseSha256 = calls[pack].Sha256,
                    responseBytes = calls[pack].Raw.Length,
                });
            }

            perRepeat.Add(new { repeat, truePositive = repeatTp, falsePositive = repeatFp, falseNegative = repeatFn });
        }

        FreezeArtifact.AssertJson(ExperimentRoot, "exp-masthead-metadata-score.v1.json", new
        {
            artifactKind = "a99_exp_masthead_metadata_score",
            schemaVersion = "a99-exp-masthead-metadata-score-v1",
            experimentId = "EXP_MASTHEAD_METADATA",
            providerCalls = 0,
            modelCalls = 0,
            additionalProviderCallsDuringScoring = 0,
            scoredFrom = "the experiment's own transport captures",

            authority = new
            {
                experimentPromptSha256 = ExperimentPromptSha256,
                predecessorPromptSha256 = PredecessorPromptSha256,
                goldSha256 = GoldSha256,
                onlyVariable = "the appended system-prompt clause; evidence packets, packing, contract, "
                    + "binder, evaluator, Gold, model and settings are those of the arm being compared to",
            },

            baselineForComparison = new
            {
                artifact = "targeted-packing-rerun-v2-successor-score.v1.json",
                packsFiveAndSixOnly = new { truePositive = 14, falsePositive = 5, falseNegative = 0 },
                perRepeatIdentical = true,
                mastheadFalsePositives = MastheadTargets,
                otherFalsePositives = new[] { "L0396:S0:0-93 (body proposition)", "L0550:S0:0-56 (schedule item)" },
            },

            perRepeat,
            perCell = cells,

            primaryCausalCheck = new
            {
                question = "were the three masthead claims removed?",
                answer = "no",
                stillEmittedByRepeatCount = MastheadTargets.ToDictionary(
                    target => target,
                    target => mastheadStillEmitted.GetValueOrDefault(target),
                    StringComparer.Ordinal),
                note = "L0515 and the date-venue-address tuple are emitted in all three repeats. The "
                    + "programme/group tuple is emitted in the two repeats whose pack bound at all; in the "
                    + "third nothing from that pack bound, so its absence is not evidence of removal.",
            },

            regressionGate = new
            {
                question = "did any approved heading in packs 5 or 6 become a false negative?",
                answer = "yes, in one repeat",
                lostGold,
                cause = "UnexpectedVerbatimText. In repeat 1 the model returned every part as WHOLE_ALIAS "
                    + "carrying a redundant verbatimText, which the binder refuses by rule - a whole-alias "
                    + "selection names an occurrence and quotes nothing. All four approved headings of that "
                    + "pack were named correctly and refused on shape.",
                notASemanticLoss = "The model did not fail to find them. This is output-shape instability, "
                    + "and it appeared only under the new prompt: the arm it is compared against returned a "
                    + "well-formed shape in all three of its repeats.",
            },

            treeLessRelationUse = new
            {
                baseline = new { emitted = 6, approved = 1, mastheadFalsePositives = 3 },
                experiment = "unchanged in kind - the masthead claims still arrive under the tree-less "
                    + "relation, and in repeat 1 the clause appears to have pushed genuine section headings "
                    + "into it as well: L0400, L0420, L0470 and L0507 were all returned with parent-node:NONE "
                    + "where the compared arm returned them under ROOT.",
                reading = "The clause narrowed nothing the model was doing and unsettled something it had "
                    + "been doing correctly.",
            },

            outcomeClassification = new
            {
                verdict = "MIXED",
                insufficient = "the three targeted claims persist",
                regression = "four approved headings lost in one repeat, on reply shape rather than judgement",
                whyNotOverrestrictive = "no approved heading was rejected for being metadata; the clause did "
                    + "not make the model stricter about structure, it made its output less stable",
                conclusionScope = "DOC-0252 causal evidence. Six calls, one document, one clause.",
            },

            whatThisSuggestsNext = new
            {
                observation = "The clause was written as a paragraph of policy appended after the relation "
                    + "rules, and the model's behaviour under it changed most in how it filled in relations "
                    + "and part shapes - not in what it selected. A clause that argues about categories may "
                    + "be reaching the wrong part of the task.",
                notProposedHere = "No replacement wording is proposed from a single arm. What this run "
                    + "establishes is that the masthead distinction is not carried by this phrasing, and "
                    + "that prompt edits in this position can disturb reply shape.",
                unchangedQuestions = new[]
                {
                    "EXP_BODY_PROPOSITION and EXP_SCHEDULE_ITEM remain unrun and unaffected",
                    "cross-genre safety remains unestablished at 48 of 3955 materialized claims",
                },
            },

            providerAccounting = new
            {
                callsSpent = 6,
                hardCap = 9,
                unusedCapacity = 3,
                authorizedForMore = false,
            },
        });

        // The two findings this run turns on.
        Assert.Contains(MastheadTargets, target => mastheadStillEmitted.GetValueOrDefault(target) == 3);
        Assert.NotEmpty(lostGold);
    }

    private static Dictionary<string, HashSet<string>> OwnedByPack(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            SemanticCoordinateContract.PdfStructuredSourceParts,
            CanonicalSemanticExperiment.NonStructuralMetadataConstrained,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(
                segment => segment.PackId,
                segment => segment.Owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    private static Dictionary<string, Captured> CapturedCalls(int repeat)
    {
        var directory = TestRepository.Path($"{ExperimentRoot}/r{repeat}");
        var path = Directory.GetFiles(directory, "*transport-capture.v1.json").Single();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("calls").EnumerateArray().ToDictionary(
            call => call.GetProperty("packId").GetString()!,
            call => new Captured(
                Encoding.UTF8.GetString(Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!)),
                call.GetProperty("rawResponseSha256").GetString()!),
            StringComparer.Ordinal);
    }

    private static HashSet<string> GoldIdentities()
    {
        using var gold = CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256);
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("identity").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FirstAlias(string identity)
    {
        var first = identity.Split('|')[0];
        return first[..first.LastIndexOf(':')];
    }

    private sealed record Claim(string Identity, string Text, string Hints);

    private sealed record Captured(string Raw, string Sha256);

    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException();
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("Scoring must not transport.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
