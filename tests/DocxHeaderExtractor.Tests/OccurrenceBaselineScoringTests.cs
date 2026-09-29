using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Scores the A99-S2P occurrence baseline offline, from the replay bundles the run persisted.
/// <para>
/// No provider is contacted here. The bundles are the authority: each carries the alias catalog,
/// the source and universe hashes it ran against and the proposals the model returned, so the same
/// score can be recomputed from the repository at any time without asking the model again.
/// </para>
/// <para>
/// Membership is decided by bound occurrence identity alone. Role is scored only where Gold
/// recorded one - DOC-0001 identifies seven headings and names a role for none of them - and the
/// rest is reported as not adjudicated rather than counted wrong.
/// </para>
/// </summary>
public sealed class OccurrenceBaselineScoringTests
{
    private const string RunRoot = "eval/a99-closed-loop/occurrence-baseline-v1";
    private static readonly string[] Cohort = ["DOC-0001", "DOC-0252"];

    // The baseline this scores ran against DOC-0252's legacy occurrence authority; its own frozen
    // goldHash is that legacy hash. DOC-0252 has since migrated in the live registry, so scoring
    // this historical run has to keep naming the vintage it actually ran against.
    private const string LegacyDoc0252GoldPath =
        "eval/a99-closed-loop/gold-current/documents/DOC-0252.legacy-occurrence.gold.v1.json";
    private const string LegacyDoc0252GoldSha256 =
        "51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65";

    private static CanonicalGoldEntry EntryFor(string id) =>
        id == "DOC-0252"
            ? CanonicalGoldRegistry.EntryAt(LegacyDoc0252GoldPath, LegacyDoc0252GoldSha256)
            : CanonicalGoldRegistry.Entry(id);

    private static IReadOnlyList<PdfBoundOccurrence> BoundGoldFor(
        string id, IReadOnlyList<SemanticSourceAlias> aliases) =>
        id == "DOC-0252"
            ? CanonicalGoldRegistry.ResolveBoundGoldAt(LegacyDoc0252GoldPath, LegacyDoc0252GoldSha256, id, aliases)
            : CanonicalGoldRegistry.ResolveBoundGold(id, aliases);

    [Fact]
    public void Score_the_baseline_against_canonical_gold()
    {
        var documents = new List<object>();
        var microTp = 0;
        var microFp = 0;
        var microFn = 0;

        foreach (var id in Cohort)
        {
            var entry = EntryFor(id);
            CanonicalGoldRegistry.RequireCapability(id, GoldCapability.Occurrence);

            var repeats = new List<object>();
            var missedPerRepeat = new List<HashSet<string>>();
            var spuriousPerRepeat = new List<HashSet<string>>();

            for (var repeat = 1; repeat <= 3; repeat++)
            {
                var bundle = LoadBundle(id, repeat);
                Assert.Equal(entry.SourceSha256, bundle.SourceHash);
                Assert.Equal(entry.GoldSha256, bundle.GoldHash);

                var goldBound = BoundGoldFor(id, bundle.AliasCatalog);
                Assert.Equal(entry.SemanticHeadingTotal, goldBound.Count);

                var replay = SemanticAuthorityReplay.Replay(bundle, bundle.SourceHash, bundle.SourceUniverseHash);
                var predicted = replay.Pipeline.BoundHeadings
                    .Select(item => new PdfBoundOccurrence(
                        item.Parts, item.SemanticRole, item.Alias, ParentFromHints(item.RelationHints)))
                    .ToArray();

                var evaluation = PdfGoldBoundOccurrenceEvaluator.EvaluateBound(
                    goldBound, predicted, [], entry.SemanticHeadingTotal);

                microTp += evaluation.Semantic.TruePositive;
                microFp += evaluation.Semantic.FalsePositive;
                microFn += evaluation.Semantic.FalseNegative;
                missedPerRepeat.Add([.. evaluation.Semantic.MissedAliases]);
                spuriousPerRepeat.Add([.. evaluation.Semantic.SpuriousAliases]);

                repeats.Add(new
                {
                    repeat,
                    bundleHash = bundle.BundleHash,
                    proposals = bundle.Proposals.Count,
                    predicted = predicted.Length,
                    truePositive = evaluation.Semantic.TruePositive,
                    falsePositive = evaluation.Semantic.FalsePositive,
                    falseNegative = evaluation.Semantic.FalseNegative,
                    precision = Round(evaluation.Semantic.Precision),
                    recall = Round(evaluation.Semantic.Recall),
                    f1 = Round(F1(evaluation.Semantic.Precision, evaluation.Semantic.Recall)),
                    role = new
                    {
                        compared = evaluation.SemanticRole.Compared,
                        agreed = evaluation.SemanticRole.Agreed,
                        mismatched = evaluation.SemanticRole.Mismatched,
                        notAdjudicated = evaluation.SemanticRole.NotAdjudicated,
                        accuracy = evaluation.SemanticRole.Accuracy is { } value ? Round(value) : (double?)null,
                        status = evaluation.SemanticRole.Status,
                    },
                });
            }

            // A miss in every repeat is a property of the run; a miss in some is variance.
            var persistentMisses = missedPerRepeat.Skip(1)
                .Aggregate(new HashSet<string>(missedPerRepeat[0]), (all, next) => { all.IntersectWith(next); return all; });
            var anyMisses = missedPerRepeat.SelectMany(set => set).ToHashSet(StringComparer.Ordinal);
            var persistentSpurious = spuriousPerRepeat.Skip(1)
                .Aggregate(new HashSet<string>(spuriousPerRepeat[0]), (all, next) => { all.IntersectWith(next); return all; });
            var anySpurious = spuriousPerRepeat.SelectMany(set => set).ToHashSet(StringComparer.Ordinal);

            documents.Add(new
            {
                authorityId = id,
                goldSha256 = entry.GoldSha256,
                semanticHeadingTotal = entry.SemanticHeadingTotal,
                repeats,
                stability = new
                {
                    persistentFalseNegatives = persistentMisses.Order(StringComparer.Ordinal).ToArray(),
                    stochasticFalseNegatives = anyMisses.Except(persistentMisses, StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).ToArray(),
                    persistentFalsePositives = persistentSpurious.Order(StringComparer.Ordinal).ToArray(),
                    stochasticFalsePositives = anySpurious.Except(persistentSpurious, StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).ToArray(),
                },
            });
        }

        var precision = microTp + microFp == 0 ? 1 : (double)microTp / (microTp + microFp);
        var recall = microTp + microFn == 0 ? 1 : (double)microTp / (microTp + microFn);

        FreezeArtifact.AssertJson(RunRoot, "offline-score.v1.json", new
        {
            artifactKind = "a99_occurrence_baseline_score",
            schemaVersion = "a99-occurrence-baseline-score-v1",
            experimentId = "A99-S2P-OCCURRENCE-BASELINE-V1",
            scoredFrom = "persisted replay bundles",
            providerCalls = 0,
            modelCalls = 0,
            evaluatorId = "a99-pdf-gold-evaluator-v3-bound-occurrence-semantic-role",
            documents,
            micro = new
            {
                truePositive = microTp,
                falsePositive = microFp,
                falseNegative = microFn,
                precision = Round(precision),
                recall = Round(recall),
                f1 = Round(F1(precision, recall)),
            },
        });

        Assert.True(microTp > 0);
    }

    /// <summary>
    /// Why DOC-0252 loses what it loses, per occurrence, from the responses already paid for.
    /// <para>
    /// The question this answers is the one that decides the next intervention: did the model fail
    /// to propose these headings, or propose them with a different boundary? Those look identical
    /// in an FN count and call for opposite work.
    /// </para>
    /// </summary>
    [Fact]
    public void Characterize_where_DOC_0252_loses_each_occurrence()
    {
        const string Id = "DOC-0252";
        var entry = EntryFor(Id);
        using var gold = CanonicalGoldRegistry.ResolveAt(LegacyDoc0252GoldPath, LegacyDoc0252GoldSha256);
        var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims")
            .EnumerateArray().Select(claim => claim.Clone()).ToArray();

        var perRepeat = new List<Dictionary<string, string>>();
        var unpairedFp = new List<HashSet<string>>();
        SemanticAuthorityReplayBundle? first = null;

        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var bundle = LoadBundle(Id, repeat);
            first ??= bundle;
            var text = bundle.AliasCatalog.ToDictionary(alias => alias.Alias, alias => alias.Text, StringComparer.Ordinal);
            var proposalsByAlias = bundle.Proposals
                .GroupBy(proposal => proposal.SourceAlias, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

            var goldBound = BoundGoldFor(Id, bundle.AliasCatalog);
            var replay = SemanticAuthorityReplay.Replay(bundle, bundle.SourceHash, bundle.SourceUniverseHash);
            var predicted = replay.Pipeline.BoundHeadings
                .Select(item => new PdfBoundOccurrence(item.Parts, item.SemanticRole, item.Alias))
                .ToArray();
            var evaluation = PdfGoldBoundOccurrenceEvaluator.EvaluateBound(goldBound, predicted, [], claims.Length);
            var missed = evaluation.Semantic.MissedAliases.ToHashSet(StringComparer.Ordinal);

            var classified = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < claims.Length; index++)
            {
                var claim = claims[index];
                var alias = claim.GetProperty("sourceAlias").GetString()!;
                var key = $"{alias}#{index}";
                if (!missed.Contains(alias)) { classified[key] = "TRUE_POSITIVE"; continue; }

                if (!proposalsByAlias.TryGetValue(alias, out var proposals))
                {
                    classified[key] = "MODEL_NOT_EMITTED";
                    continue;
                }

                var occurrence = text.GetValueOrDefault(alias, string.Empty);
                var wholeAlias = claim.GetProperty("selectionMode").GetString() == "WHOLE_ALIAS";
                var goldText = wholeAlias ? occurrence : claim.GetProperty("verbatimText").GetString() ?? string.Empty;
                classified[key] = Classify(goldText, occurrence, proposals);
            }

            perRepeat.Add(classified);
            unpairedFp.Add(evaluation.Semantic.SpuriousAliases
                .Where(alias => !claims.Any(claim => claim.GetProperty("sourceAlias").GetString() == alias))
                .ToHashSet(StringComparer.Ordinal));
        }

        // Persistent means the same class in all three repeats, not merely lost in all three.
        var keys = perRepeat[0].Keys.Order(StringComparer.Ordinal).ToArray();
        var census = new Dictionary<string, int>(StringComparer.Ordinal);
        var occurrences = keys.Select(key =>
        {
            var signature = perRepeat.Select(repeat => repeat[key]).ToArray();
            var stable = signature.Distinct(StringComparer.Ordinal).Count() == 1;
            var primary = stable ? signature[0] : "NON_PERSISTENT";
            census[primary] = census.GetValueOrDefault(primary) + 1;
            return new
            {
                claim = key,
                signature,
                firstLoss = primary,
                persistent = stable && primary != "TRUE_POSITIVE",
            };
        }).ToArray();

        var lost = occurrences.Where(item => item.firstLoss != "TRUE_POSITIVE").ToArray();
        FreezeArtifact.AssertJson(RunRoot, "causal-forensic.v1.json", new
        {
            artifactKind = "a99_occurrence_baseline_causal_forensic",
            schemaVersion = "a99-occurrence-baseline-causal-forensic-v1",
            experimentId = "A99-S2P-OCCURRENCE-BASELINE-V1",
            authorityId = Id,
            providerCalls = 0,
            modelCalls = 0,
            goldSha256 = entry.GoldSha256,
            sourceSha256 = entry.SourceSha256,
            sourceUniverseSha256 = first!.SourceUniverseHash,
            goldClaims = claims.Length,
            roleScoringStatus = "NOT_CURRENTLY_COMPARABLE",
            roleNote = "Gold uses a closed ontology (MeetingSection, AgendaItem, LocalSubheading); " +
                       "the model returns open vocabulary (heading, section-title). Not a model " +
                       "error and not comparable until an ontology or mapping is adjudicated.",
            firstLossCensus = census.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value),
            lostOccurrences = lost,
            unpairedFalsePositivesPerRepeat = unpairedFp.Select(set => set.Count).ToArray(),
            occurrences,
        });

        Assert.Equal(claims.Length, occurrences.Length);
    }

    /// <summary>
    /// Where the first loss happened for one Gold claim, from what the model actually returned.
    /// The order matters: an occurrence the model proposed is never blamed on the binder.
    /// </summary>
    private static string Classify(
        string goldText, string occurrence, IReadOnlyList<CanonicalSemanticProposal> proposals)
    {
        foreach (var proposal in proposals)
        {
            var emitted = proposal.VerbatimText ?? string.Empty;
            if (string.Equals(emitted, goldText, StringComparison.Ordinal))
                return "MODEL_EMITTED_EXACT_LOST_DOWNSTREAM";
            if (emitted.Length == 0) continue;
            if (emitted.Contains(goldText, StringComparison.Ordinal))
                return "MODEL_EMITTED_SUPERSET";
            if (goldText.Contains(emitted, StringComparison.Ordinal))
                return "MODEL_EMITTED_SUBSPAN";
            if (!occurrence.Contains(emitted, StringComparison.Ordinal))
                return "MODEL_EMITTED_NON_VERBATIM_TEXT";
        }

        return "MODEL_EMITTED_DIFFERENT_REGION";
    }

    private static SemanticAuthorityReplayBundle LoadBundle(string id, int repeat)
    {
        var directory = Path.Combine(TestRepository.Path(RunRoot), id, $"r{repeat}");
        var file = Directory.EnumerateFiles(directory, "*.semantic-authority-replay.v1.json").Single();
        return JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(
            File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static string? ParentFromHints(IReadOnlyList<string>? hints) =>
        hints?.Select(hint => hint.StartsWith("parent-node:", StringComparison.Ordinal)
            ? hint["parent-node:".Length..]
            : null).FirstOrDefault(value => value is not null);

    private static double F1(double precision, double recall) =>
        precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

    private static double Round(double value) => Math.Round(value, 4);
}
