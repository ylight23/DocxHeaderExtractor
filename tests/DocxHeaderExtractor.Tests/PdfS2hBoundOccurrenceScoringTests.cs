using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfS2hBoundOccurrenceScoringTests
{
    private const string Root =
        "eval/a99-closed-loop/semantic-text-replay-successor-v1-runtime-authority/DOC-0252";
    private const string GoldPath =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.occurrence-gold.v1.json";
    private const string SourceHash =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseHash =
        "cb3c9af67a7f17fd9560b56cf23bb9648a9fcfe3ea4eb5d333ac7282176bfc66";
    private const string PromptHash =
        "8b056f1722b356dd9e836908b8d05ad0850353a06fbc0a4aa39db568fd47f0a8";
    private const string SemanticContractHash =
        "21687e5a78d59b8c82124dcc567c7353024145d599b1294c41c888ffe4512365";
    private const string SuccessorManifestHash =
        "2ac4513d81d63fcaab23cf26d0fbacf4bd5ac741770eeb0f4c4ce9c0fcbd3474";

    [Fact]
    public void Rescore_immutable_replays_by_canonical_bound_occurrence_only()
    {
        var gold = JsonSerializer.Deserialize<PdfGoldDocument>(
            File.ReadAllText(RepositoryPath(GoldPath)))!;
        var oldScore = JsonDocument.Parse(Read("offline-score.v1.json"));
        var repeats = new List<RepeatScore>();

        var goldBound = PdfGoldBoundOccurrenceEvaluator.BindGold(
            gold, LoadBundle(1).AliasCatalog, out var goldBindingIssues);
        Assert.Empty(goldBindingIssues);
        Assert.Equal(41, goldBound.Count);

        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var bundle = LoadBundle(repeat);
            var replay = SemanticAuthorityReplay.Replay(bundle, SourceHash, SourceUniverseHash);
            var observations = replay.Pipeline.BindingObservations
                .Where(item => item.Status == CanonicalSemanticBindingStatus.Bound)
                .ToArray();
            var predicted = replay.Pipeline.BoundHeadings
                .Select((item, index) => new PdfBoundOccurrence(
                    item.Parts,
                    item.SemanticRole,
                    item.Alias,
                    ParentFromHints(item.RelationHints)))
                .ToArray();
            var issues = PdfGoldValidator.Validate(gold, Catalog(bundle.AliasCatalog), bundle.AliasCatalog);
            var evaluation = PdfGoldBoundOccurrenceEvaluator.Evaluate(
                gold, predicted, bundle.AliasCatalog, issues);
            var oldRepeat = oldScore.RootElement.GetProperty("repeats")
                .EnumerateArray().Single(item => item.GetProperty("Repeat").GetString() == $"r{repeat}");

            Assert.Equal(bundle.ProposalHash, SemanticAuthorityReplayHashing.ProposalHash(bundle.Proposals));

            var goldKeys = goldBound.Select(Key).ToArray();
            var predictedKeys = predicted.Select(Key).ToArray();
            var pairCounts = PairCounts(
                gold,
                goldBound,
                predicted,
                oldRepeat,
                observations.Select(item => item.Proposal).ToArray());
            repeats.Add(new RepeatScore(
                $"r{repeat}",
                bundle.BundleHash,
                bundle.ProposalHash,
                evaluation,
                goldKeys.Except(predictedKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                predictedKeys.Except(goldKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                ClassifyFalseNegatives(goldBound, predicted),
                pairCounts));
        }

        Assert.Equal([18, 19, 18], repeats.Select(item => item.Evaluation.Semantic.TruePositive).ToArray());
        Assert.Equal([23, 22, 23], repeats.Select(item => item.Evaluation.Semantic.FalseNegative).ToArray());
        Assert.Equal([9, 9, 10], repeats.Select(item => item.Evaluation.Semantic.FalsePositive).ToArray());
        Assert.All(repeats, item => Assert.Empty(item.Evaluation.GoldIssues));

        var persistentFn = Intersection(repeats.Select(item => item.FalseNegativeKeys));
        var persistentFp = Intersection(repeats.Select(item => item.FalsePositiveKeys));
        var stochasticFn = Union(repeats.Select(item => item.FalseNegativeKeys))
            .Except(persistentFn, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var stochasticFp = Union(repeats.Select(item => item.FalsePositiveKeys))
            .Except(persistentFp, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(22, persistentFn.Length);
        Assert.Single(stochasticFn);
        Assert.Equal(4, persistentFp.Length);
        Assert.Equal(11, stochasticFp.Length);

        if (string.Equals(Environment.GetEnvironmentVariable("A99_S2H_SCORE"), "1", StringComparison.Ordinal))
        {
            var report = new
            {
                artifactKind = "a99_pdf_s2h_bound_occurrence_offline_score",
                schemaVersion = "a99-pdf-s2h-bound-occurrence-offline-score-v1",
                evaluator = PdfGoldBoundOccurrenceEvaluator.ContractVersion,
                experimentId = "DOC-0252-PDF-S2E-RUNTIME-B0-ADOPTION-V1",
                successorManifestHash = SuccessorManifestHash,
                sourceSha256 = SourceHash,
                sourceUniverseSha256 = SourceUniverseHash,
                promptSha256 = PromptHash,
                semanticContractHash = SemanticContractHash,
                goldSha256 = CanonicalArtifactHash.OfTextFile(RepositoryPath(GoldPath)),
                modelCalls = 0,
                providerCalls = 0,
                scoringPerformed = true,
                identity = "ordered bound source parts: sourceId + exact UTF-16 start/end; representation is provenance only",
                repeats,
                aggregate = new
                {
                    truePositive = repeats.Sum(item => item.Evaluation.Semantic.TruePositive),
                    falsePositive = repeats.Sum(item => item.Evaluation.Semantic.FalsePositive),
                    falseNegative = repeats.Sum(item => item.Evaluation.Semantic.FalseNegative),
                    precision = Precision(repeats),
                    recall = Recall(repeats),
                    f1 = F1(Precision(repeats), Recall(repeats)),
                    roleComparisons = repeats.Sum(item => item.Evaluation.SemanticRole.Compared),
                    roleAgreed = repeats.Sum(item => item.Evaluation.SemanticRole.Agreed),
                    roleMismatched = repeats.Sum(item => item.Evaluation.SemanticRole.Mismatched),
                    roleAccuracy = RoleAccuracy(repeats),
                    hierarchy = "NOT_ADJUDICATED",
                },
                persistentFalseNegatives = persistentFn,
                stochasticFalseNegatives = stochasticFn,
                persistentFalsePositives = persistentFp,
                stochasticFalsePositives = stochasticFp,
                residualAttribution = new
                {
                    falseNegative = new
                    {
                        perRepeat = repeats.Select(item => new { repeat = item.Repeat, kinds = item.FalseNegativeKinds }),
                        persistentByKind = persistentFn
                            .GroupBy(key => repeats[0].FalseNegativeKinds[key])
                            .ToDictionary(group => group.Key, group => group.Count()),
                    },
                    falsePositive = new
                    {
                        note = "FP taxonomy is not inferred from source prose; unresolved extras remain UNRESOLVED unless span geometry proves a boundary relation.",
                    },
                },
                representationPairAudit = repeats.Select(item => new
                {
                    repeat = item.Repeat,
                    representationOnlyFnFpPairs = item.PairCounts.ExactSameBinding,
                    partialPrediction = item.PairCounts.PartialPrediction,
                    supersetPrediction = item.PairCounts.SupersetPrediction,
                    differentTextBoundary = item.PairCounts.DifferentTextBoundary,
                    differentOccurrence = item.PairCounts.DifferentOccurrence,
                    unrelated = item.PairCounts.Unrelated,
                }),
            };
            File.WriteAllText(RepositoryPath(Root + "/offline-score.v2.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static SemanticAuthorityReplayBundle LoadBundle(int repeat)
    {
        var path = Directory.GetFiles(RepositoryPath(
            $"eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252/r{repeat}"), "*.json")
            .Single();
        return JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(File.ReadAllText(path))!;
    }

    private static DocumentSourceCatalog Catalog(IReadOnlyList<SemanticSourceAlias> aliases) =>
        new(aliases.Select(alias => new DocumentSourceUnit(
            alias.SourceId,
            alias.SourceOrdinal,
            alias.Text,
            new SourceAnchor { SourceType = "pdf", ParagraphId = alias.SourceId, ParagraphIndex = alias.SourceOrdinal },
            alias.SourceSpan)));

    private static PairCount PairCounts(
        PdfGoldDocument gold,
        IReadOnlyList<PdfBoundOccurrence> goldBound,
        IReadOnlyList<PdfBoundOccurrence> predicted,
        JsonElement oldRepeat,
        IReadOnlyList<CanonicalSemanticProposal>? predictionProposals = null)
    {
        var goldInfos = gold.Headings.Select((item, index) => new BindingInfo(
            item.SourceAlias,
            RepresentationKey(item.SourceAlias, item.SelectionMode, item.VerbatimText, item.Occurrence),
            Key(goldBound[index]),
            goldBound[index])).ToDictionary(item => item.RepresentationKey, StringComparer.Ordinal);
        var predInfos = predicted.Select((item, index) => new BindingInfo(
            item.SourceAlias,
            RepresentationKey(
                item.SourceAlias,
                predictionProposals?[index].SelectionMode ?? "VERBATIM_TEXT",
                predictionProposals?[index].SelectionMode == "WHOLE_ALIAS"
                    ? null
                    : predictionProposals?[index].VerbatimText,
                predictionProposals?[index].Occurrence),
            Key(item),
            item)).ToList();
        var oldFns = oldRepeat.GetProperty("FalseNegativeKeys").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var oldFps = oldRepeat.GetProperty("FalsePositiveKeys").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var counts = new PairCount();
        foreach (var fn in oldFns)
        {
            if (!goldInfos.TryGetValue(fn, out var goldInfo)) continue;
            foreach (var fp in oldFps)
            {
                var prediction = predInfos.FirstOrDefault(item => item.RepresentationKey == fp);
                if (prediction is null || !prediction.Alias.Equals(goldInfo.Alias, StringComparison.Ordinal)) continue;
                if (prediction.CanonicalKey == goldInfo.CanonicalKey) counts.ExactSameBinding++;
                else
                {
                    var goldPart = goldInfo.Bound.Parts.Count == 1 ? goldInfo.Bound.Parts[0] : null;
                    var predictionPart = prediction.Bound.Parts.Count == 1 ? prediction.Bound.Parts[0] : null;
                    if (goldPart is null || predictionPart is null ||
                        !string.Equals(goldPart.SourceId, predictionPart.SourceId, StringComparison.Ordinal))
                        counts.DifferentOccurrence++;
                    else if (predictionPart.Start >= goldPart.Start && predictionPart.End <= goldPart.End)
                        counts.PartialPrediction++;
                    else if (goldPart.Start >= predictionPart.Start && goldPart.End <= predictionPart.End)
                        counts.SupersetPrediction++;
                    else if (predictionPart.Start < goldPart.End && predictionPart.End > goldPart.Start)
                        counts.DifferentTextBoundary++;
                    else counts.DifferentOccurrence++;
                }
            }
        }
        return counts;
    }

    private static IReadOnlyDictionary<string, string> ClassifyFalseNegatives(
        IReadOnlyList<PdfBoundOccurrence> gold,
        IReadOnlyList<PdfBoundOccurrence> predicted)
    {
        var classifications = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expected in gold)
        {
            var expectedKey = Key(expected);
            if (predicted.Any(item => Key(item) == expectedKey)) continue;
            var sameAlias = predicted.Where(item =>
                string.Equals(item.SourceAlias, expected.SourceAlias, StringComparison.Ordinal)).ToArray();
            if (sameAlias.Length == 0)
            {
                classifications[expectedKey] = "TRUE_MODEL_OMISSION";
                continue;
            }

            var expectedPart = expected.Parts.Count == 1 ? expected.Parts[0] : null;
            if (expectedPart is not null && sameAlias.Any(item =>
                item.Parts.Any(part =>
                    part.SourceId == expectedPart.SourceId &&
                    part.Start == expectedPart.Start &&
                    part.End == expectedPart.End) &&
                item.Parts.Count > 1))
            {
                classifications[expectedKey] = "SUPERSET_HEADING";
                continue;
            }

            if (expectedPart is not null && sameAlias.Any(item => item.Parts.Count == 1 &&
                item.Parts[0].SourceId == expectedPart.SourceId &&
                item.Parts[0].Start < expectedPart.End &&
                item.Parts[0].End > expectedPart.Start))
            {
                classifications[expectedKey] = "MODEL_WRONG_TEXT_BOUNDARY";
                continue;
            }

            classifications[expectedKey] = "MODEL_WRONG_SPAN";
        }
        return classifications;
    }

    private static string RepresentationKey(string alias, string mode, string? text, int? occurrence) =>
        $"{alias}|{mode}|{text}|{occurrence}";

    private static string Key(PdfBoundOccurrence item) =>
        string.Join(';', item.Parts.Select(part => $"{part.SourceId}:{part.Start}:{part.End}"));

    private static string? ParentFromHints(IReadOnlyList<string> hints)
    {
        var hint = hints.FirstOrDefault(item => item.StartsWith("parent-node:", StringComparison.Ordinal));
        if (hint is null) return null;
        var value = hint["parent-node:".Length..];
        return value is "ROOT" or "NONE" ? null : value;
    }

    private static string[] Intersection(IEnumerable<IReadOnlyList<string>> sets)
    {
        var result = new HashSet<string>(sets.First(), StringComparer.Ordinal);
        foreach (var set in sets.Skip(1)) result.IntersectWith(set);
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] Union(IEnumerable<IReadOnlyList<string>> sets) =>
        sets.SelectMany(set => set).ToHashSet(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static double Precision(IReadOnlyList<RepeatScore> repeats)
    {
        var tp = repeats.Sum(item => item.Evaluation.Semantic.TruePositive);
        var fp = repeats.Sum(item => item.Evaluation.Semantic.FalsePositive);
        return tp + fp == 0 ? 1 : (double)tp / (tp + fp);
    }

    private static double Recall(IReadOnlyList<RepeatScore> repeats)
    {
        var tp = repeats.Sum(item => item.Evaluation.Semantic.TruePositive);
        var fn = repeats.Sum(item => item.Evaluation.Semantic.FalseNegative);
        return tp + fn == 0 ? 1 : (double)tp / (tp + fn);
    }

    private static double F1(double precision, double recall) =>
        precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

    private static double RoleAccuracy(IReadOnlyList<RepeatScore> repeats)
    {
        var compared = repeats.Sum(item => item.Evaluation.SemanticRole.Compared);
        var agreed = repeats.Sum(item => item.Evaluation.SemanticRole.Agreed);
        return compared == 0 ? 1 : (double)agreed / compared;
    }

    private static string Read(string file) => File.ReadAllText(RepositoryPath(Root + "/" + file));

    private static string RepositoryPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", relativePath));

    private sealed record BindingInfo(
        string Alias,
        string RepresentationKey,
        string CanonicalKey,
        PdfBoundOccurrence Bound);

    private sealed class PairCount
    {
        public int ExactSameBinding { get; set; }
        public int PartialPrediction { get; set; }
        public int SupersetPrediction { get; set; }
        public int DifferentTextBoundary { get; set; }
        public int DifferentOccurrence { get; set; }
        public int Unrelated { get; set; }
    }

    private sealed record RepeatScore(
        string Repeat,
        string BundleHash,
        string ProposalHash,
        PdfGoldEvaluation Evaluation,
        IReadOnlyList<string> FalseNegativeKeys,
        IReadOnlyList<string> FalsePositiveKeys,
        IReadOnlyDictionary<string, string> FalseNegativeKinds,
        PairCount PairCounts);
}
