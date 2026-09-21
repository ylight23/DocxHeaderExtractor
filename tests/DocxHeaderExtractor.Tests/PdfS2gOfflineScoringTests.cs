using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfS2gOfflineScoringTests
{
    private const string Root =
        "eval/a99-closed-loop/semantic-text-replay-successor-v1-runtime-authority/DOC-0252";
    private const string OriginalRoot =
        "eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252";
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
    private const string OriginalManifestHash =
        "7c48d54c94e4ab60f022a2e5104b44eefb5e4639ac9aea31e8842abeda183cfb";
    private const string SuccessorManifestHash =
        "2ac4513d81d63fcaab23cf26d0fbacf4bd5ac741770eeb0f4c4ce9c0fcbd3474";

    [Fact]
    public void Score_captured_repeats_offline_and_emit_successor_report_when_requested()
    {
        using var census = JsonDocument.Parse(Read("bundle-census.v1.json"));
        var bundleRows = census.RootElement.GetProperty("bundles").EnumerateArray().ToArray();
        Assert.Equal(3, bundleRows.Length);

        var gold = JsonSerializer.Deserialize<PdfGoldDocument>(
            File.ReadAllText(RepositoryPath(GoldPath)))!;
        Assert.Equal(41, gold.Headings.Count);

        var scores = new List<RepeatScore>();
        foreach (var row in bundleRows)
        {
            var bundle = JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(
                File.ReadAllText(RepositoryPath(row.GetProperty("path").GetString()!)))!;
            Assert.Empty(SemanticAuthorityReplay.Validate(bundle, SourceHash, SourceUniverseHash));
            Assert.Equal(OriginalManifestHash, bundle.ManifestHash);
            Assert.Equal(PromptHash, bundle.PromptHash);
            Assert.Equal(SemanticContractHash, bundle.SemanticContractHash);

            var replay = SemanticAuthorityReplay.Replay(bundle, SourceHash, SourceUniverseHash);
            var predicted = replay.Pipeline.BindingObservations
                .Where(observation => observation.Status == CanonicalSemanticBindingStatus.Bound)
                .Select(observation => ToPredicted(observation.Proposal))
                .ToArray();
            var issues = PdfGoldValidator.Validate(gold, Catalog(bundle.AliasCatalog), bundle.AliasCatalog);
            Assert.Empty(issues);
            var evaluation = PdfGoldEvaluator.Evaluate(gold, predicted, issues);

            scores.Add(RepeatScore.From(
                row.GetProperty("repeat").GetString()!, bundle, predicted, evaluation));
        }

        var aggregate = Aggregate(scores);
        Assert.Equal(3, aggregate.Repeats);
        Assert.Equal(0, aggregate.ProviderCalls);
        Assert.Equal(0, aggregate.ModelCalls);
        Assert.True(aggregate.ScoringInputsComplete);

        if (string.Equals(Environment.GetEnvironmentVariable("A99_S2G_SCORE"), "1", StringComparison.Ordinal))
        {
            var report = new
            {
                artifactKind = "a99_pdf_s2g_offline_score",
                schemaVersion = "a99-pdf-s2g-offline-score-v1",
                experimentId = "DOC-0252-PDF-S2E-RUNTIME-B0-ADOPTION-V1",
                successorManifestHash = SuccessorManifestHash,
                originalExperimentId = "DOC-0252-PDF-S2E-RUNTIME-B0",
                originalManifestHash = OriginalManifestHash,
                sourceSha256 = SourceHash,
                sourceUniverseSha256 = SourceUniverseHash,
                semanticContractHash = SemanticContractHash,
                promptSha256 = PromptHash,
                goldSha256 = CanonicalArtifactHash.OfTextFile(RepositoryPath(GoldPath)),
                evaluator = "a99-pdf-gold-evaluator-v2-semantic-role",
                modelCalls = 0,
                providerCalls = 0,
                scoringPerformed = true,
                predictionIdentity =
                    "Bound observation proposals; omitted runtime selectionMode is represented as " +
                    "VERBATIM_TEXT for the frozen evaluator key. Proposals are not rewritten.",
                semanticAggregate = new
                {
                    truePositive = scores.Sum(score => score.Semantic.TruePositive),
                    falsePositive = scores.Sum(score => score.Semantic.FalsePositive),
                    falseNegative = scores.Sum(score => score.Semantic.FalseNegative),
                    precision = AggregatePrecision(scores),
                    recall = AggregateRecall(scores),
                    f1 = F1(AggregatePrecision(scores), AggregateRecall(scores)),
                },
                hierarchy = new { status = "NOT_ADJUDICATED", scored = false },
                repeats = scores,
                aggregate,
                residualAttribution = new
                {
                    falseNegative = new
                    {
                        persistent = aggregate.PersistentFalseNegatives,
                        stochastic = aggregate.StochasticFalseNegatives,
                    },
                    falsePositive = new
                    {
                        persistent = aggregate.PersistentFalsePositives,
                        stochastic = aggregate.StochasticFalsePositives,
                    },
                    definition = "persistent = intersection across r1/r2/r3; stochastic = union minus intersection",
                },
            };
            var path = RepositoryPath(Root + "/offline-score.v1.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));
        }
    }

    private static PdfPredictedHeading ToPredicted(CanonicalSemanticProposal proposal) =>
        new(
            proposal.SourceAlias,
            proposal.SelectionMode ?? CanonicalSemanticSelectionMode.VerbatimText,
            proposal.SelectionMode == CanonicalSemanticSelectionMode.WholeAlias
                ? null
                : proposal.VerbatimText,
            proposal.Occurrence,
            ParentFromHints(proposal.RelationHints),
            proposal.SemanticRole);

    private static string? ParentFromHints(IReadOnlyList<string>? hints)
    {
        var hint = hints?.FirstOrDefault(item => item.StartsWith("parent-node:", StringComparison.Ordinal));
        if (hint is null) return null;
        var parent = hint["parent-node:".Length..];
        return parent is "ROOT" or "NONE" ? null : parent;
    }

    private static DocumentSourceCatalog Catalog(IReadOnlyList<SemanticSourceAlias> aliases) =>
        new(aliases.Select(alias => new DocumentSourceUnit(
            alias.SourceId,
            alias.SourceOrdinal,
            alias.Text,
            new SourceAnchor { SourceType = "pdf", ParagraphId = alias.SourceId, ParagraphIndex = alias.SourceOrdinal },
            alias.SourceSpan)));

    private static AggregateScore Aggregate(IReadOnlyList<RepeatScore> scores)
    {
        var fnSets = scores.Select(score => score.FalseNegativeKeys.ToHashSet(StringComparer.Ordinal)).ToArray();
        var fpSets = scores.Select(score => score.FalsePositiveKeys.ToHashSet(StringComparer.Ordinal)).ToArray();
        var persistentFn = fnSets.Skip(1).Aggregate(
            new HashSet<string>(fnSets[0], StringComparer.Ordinal),
            (current, next) => { current.IntersectWith(next); return current; });
        var persistentFp = fpSets.Skip(1).Aggregate(
            new HashSet<string>(fpSets[0], StringComparer.Ordinal),
            (current, next) => { current.IntersectWith(next); return current; });
        var unionFn = fnSets.SelectMany(set => set).ToHashSet(StringComparer.Ordinal);
        var unionFp = fpSets.SelectMany(set => set).ToHashSet(StringComparer.Ordinal);
        return new AggregateScore(
            scores.Count,
            scores.Sum(score => score.ProviderCalls),
            scores.Sum(score => score.ModelCalls),
            scores.All(score => score.GoldIssues.Count == 0),
            scores.All(score => score.SourceUniverseHash == SourceUniverseHash),
            persistentFn.Order(StringComparer.Ordinal).ToArray(),
            unionFn.Except(persistentFn, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            persistentFp.Order(StringComparer.Ordinal).ToArray(),
            unionFp.Except(persistentFp, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static double AggregatePrecision(IReadOnlyList<RepeatScore> scores)
    {
        var truePositive = scores.Sum(score => score.Semantic.TruePositive);
        var falsePositive = scores.Sum(score => score.Semantic.FalsePositive);
        return truePositive + falsePositive == 0
            ? 1
            : (double)truePositive / (truePositive + falsePositive);
    }

    private static double AggregateRecall(IReadOnlyList<RepeatScore> scores)
    {
        var truePositive = scores.Sum(score => score.Semantic.TruePositive);
        var falseNegative = scores.Sum(score => score.Semantic.FalseNegative);
        return truePositive + falseNegative == 0
            ? 1
            : (double)truePositive / (truePositive + falseNegative);
    }

    private static double F1(double precision, double recall) =>
        precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

    private static string Key(string sourceAlias, string selectionMode, string? verbatimText, int? occurrence) =>
        $"{sourceAlias}|{selectionMode}|{verbatimText}|{occurrence}";

    private static string Read(string relativePath) => File.ReadAllText(RepositoryPath(Root + "/" + relativePath));

    private static string RepositoryPath(string relativePath) =>
        System.IO.Path.Combine(TestRepository.Root(), relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private sealed record RepeatScore(
        string Repeat,
        string BundleHash,
        string ProposalHash,
        int ProviderCalls,
        int ModelCalls,
        int GoldRows,
        int PredictedRows,
        PdfMembershipScore Semantic,
        PdfSemanticRoleScore SemanticRole,
        PdfRelationScore Structural,
        IReadOnlyList<string> FalseNegativeKeys,
        IReadOnlyList<string> FalsePositiveKeys,
        IReadOnlyList<string> GoldIssues,
        string SourceUniverseHash)
    {
        [JsonPropertyName("f1")]
        public double F1 => F1Score(Semantic.Precision, Semantic.Recall);

        public static RepeatScore From(
            string repeat,
            SemanticAuthorityReplayBundle bundle,
            IReadOnlyList<PdfPredictedHeading> predicted,
            PdfGoldEvaluation evaluation)
        {
            var goldKeys = evaluation.GoldRows == 0
                ? []
                : LoadGold().Headings.Select(item => Key(item.SourceAlias, item.SelectionMode, item.VerbatimText, item.Occurrence)).ToArray();
            var predictedKeys = predicted
                .Select(item => Key(item.SourceAlias, item.SelectionMode, item.VerbatimText, item.Occurrence))
                .ToHashSet(StringComparer.Ordinal);
            return new(
                repeat,
                bundle.BundleHash,
                bundle.ProposalHash,
                0,
                0,
                evaluation.GoldRows,
                evaluation.PredictedRows,
                evaluation.Semantic,
                evaluation.SemanticRole,
                evaluation.Structural,
                goldKeys.Except(predictedKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                predictedKeys.Except(goldKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                evaluation.GoldIssues,
                bundle.SourceUniverseHash);
        }

        private static PdfGoldDocument LoadGold() =>
            JsonSerializer.Deserialize<PdfGoldDocument>(File.ReadAllText(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", GoldPath))))!;

        private static double F1Score(double precision, double recall) =>
            precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    private sealed record AggregateScore(
        int Repeats,
        int ProviderCalls,
        int ModelCalls,
        bool ScoringInputsComplete,
        bool SourceUniverseConsistent,
        IReadOnlyList<string> PersistentFalseNegatives,
        IReadOnlyList<string> StochasticFalseNegatives,
        IReadOnlyList<string> PersistentFalsePositives,
        IReadOnlyList<string> StochasticFalsePositives);
}
