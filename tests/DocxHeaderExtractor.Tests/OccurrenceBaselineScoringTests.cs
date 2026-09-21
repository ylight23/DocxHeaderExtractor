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

    [Fact]
    public void Score_the_baseline_against_canonical_gold()
    {
        var documents = new List<object>();
        var microTp = 0;
        var microFp = 0;
        var microFn = 0;

        foreach (var id in Cohort)
        {
            var entry = CanonicalGoldRegistry.Entry(id);
            CanonicalGoldRegistry.RequireCapability(id, GoldCapability.Occurrence);

            var repeats = new List<object>();
            var missedPerRepeat = new List<HashSet<string>>();
            var spuriousPerRepeat = new List<HashSet<string>>();

            for (var repeat = 1; repeat <= 3; repeat++)
            {
                var bundle = LoadBundle(id, repeat);
                Assert.Equal(entry.SourceSha256, bundle.SourceHash);
                Assert.Equal(entry.GoldSha256, bundle.GoldHash);

                var goldBound = CanonicalGoldRegistry.ResolveBoundGold(id, bundle.AliasCatalog);
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
                        accuracy = Round(evaluation.SemanticRole.Accuracy),
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
