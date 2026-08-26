using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N1.6/Lane D: an offline causal diagnosis over Lane C's frozen, source-safe silver cohort.
/// It exactly reconstructs the existing rank score from exposed signals, then neutralizes one
/// existing component at a time. It never changes a production score, budget, label, or model call.
/// </summary>
public sealed class PdfN16RankScoreOwnerDiagnosisProbe
{
    private const int Budget = 160;
    private const string BaseLaneCRevision = "7bc4317";
    private static readonly string[] ScoreComponents =
    [
        "labelled_numbering_marker", "unlabelled_numbering_prefix", "standalone", "marker_title_composite",
        "canonical_marker_title", "layout_prominence", "opens_content", "table_scope", "running_page_scope",
        "header_footer_zone", "long_marker_body_window",
    ];

    [Fact]
    public void WriteDiagnosis()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("BENCH_N16_OUTPUT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;
        Directory.CreateDirectory(outputDirectory);
        var artifact = BuildArtifact(PdfExtractorQualityBenchmarkProbe.RepositoryRoot());
        File.WriteAllText(Path.Combine(outputDirectory, "rank-score-owner-029-042.v1.json"),
            JsonSerializer.Serialize(artifact, JsonOptions));
        File.WriteAllText(Path.Combine(outputDirectory, "rank-score-owner-029-042-summary.v1.md"), BuildSummary(artifact));
    }

    [Fact]
    public void CommittedDiagnosisReproducesByteForByte()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "benchmark-n0", "diagnostics", "rank-score-owner-029-042");
        var jsonPath = Path.Combine(directory, "rank-score-owner-029-042.v1.json");
        var markdownPath = Path.Combine(directory, "rank-score-owner-029-042-summary.v1.md");
        if (!File.Exists(jsonPath) || !File.Exists(markdownPath)) return;
        var artifact = BuildArtifact(root);
        Assert.Equal(Normalize(JsonSerializer.Serialize(artifact, JsonOptions)), Normalize(File.ReadAllText(jsonPath)));
        Assert.Equal(Normalize(BuildSummary(artifact)), Normalize(File.ReadAllText(markdownPath)));
    }

    [Fact]
    public void ExactReplayUsesSourceAuthorityAndReconcilesScoreComponents()
    {
        var artifact = BuildArtifact(PdfExtractorQualityBenchmarkProbe.RepositoryRoot());
        Assert.Equal("MODEL_ASSISTED_SILVER", artifact.LabelAuthority);
        Assert.False(artifact.HumanAdjudicated);
        Assert.Equal("SILVER_PROXY_ONLY", artifact.ClaimBoundary);
        Assert.Contains("sourceLineIds", artifact.Identity, StringComparison.Ordinal);
        Assert.Contains("candidateId is diagnostics-only", artifact.Identity, StringComparison.Ordinal);
        foreach (var document in artifact.Documents)
        {
            Assert.All(document.FullCandidateOccurrences, occurrence => Assert.NotEmpty(occurrence.SourceLineIds));
            Assert.All(document.ScoreReconciliation, item => Assert.Equal(item.ExposedCandidateScore, item.RecomputedCandidateScore, 12));
            Assert.Equal(document.PopulationCounts.FullCandidate, document.FullCandidateOccurrences.Count);
            Assert.Equal(document.PopulationCounts.SilverSelectedAt160, document.FullCandidateOccurrences.Count(o => o.BaselineRank <= Budget));
            Assert.All(document.Counterfactuals, result => Assert.Equal(document.FullCandidateOccurrences.Count, result.FullCandidate));
        }
    }

    private static RankScoreArtifact BuildArtifact(string root)
    {
        var laneCPath = Path.Combine(root, "eval", "benchmark-n0", "diagnostics", "ranking-029-042", "ranking-029-042.v1.json");
        using var laneC = JsonDocument.Parse(File.ReadAllText(laneCPath));
        var documents = laneC.RootElement.GetProperty("documents").EnumerateArray()
            .Select(document => BuildDocument(root, document)).ToArray();
        return new RankScoreArtifact(1, "n1_6_rank_score_owner_diagnosis", BaseLaneCRevision, Sha256(laneCPath),
            "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only within this replay and is never cross-run identity",
            "MODEL_ASSISTED_SILVER", false, "SILVER_PROXY_ONLY", documents);
    }

    private static RankScoreDocument BuildDocument(string root, JsonElement laneCDocument)
    {
        var documentId = laneCDocument.GetProperty("documentId").GetString()!;
        var relative = laneCDocument.GetProperty("documentRelativePath").GetString()!;
        var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(laneCDocument.GetProperty("documentSha256").GetString(), Sha256(path));

        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        Assert.Equal(laneCDocument.GetProperty("sourceLineExtractionFingerprint").GetString(), SourceFingerprint(snapshot.Lines));
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var baseline = snapshot.Audit.Candidates.Select((candidate, index) => CandidateFacts.Create(candidate, index + 1,
            snapshot.Provenance[candidate.SourceId], contexts[candidate.SourceId])).ToArray();
        var byId = baseline.ToDictionary(candidate => candidate.CandidateId, StringComparer.Ordinal);
        var lineIndex = snapshot.Lines.Select((line, index) => (Id: PdfCandidateProvenance.LineId(line), index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);

        var occurrences = laneCDocument.GetProperty("fullCandidateOccurrences").EnumerateArray()
            .Select(item => RankedSilverOccurrence.Read(item, lineIndex, snapshot, byId)).ToArray();
        var baselineRanks = baseline.ToDictionary(candidate => candidate.CandidateId, candidate => candidate.BaselineRank, StringComparer.Ordinal);
        Assert.All(occurrences, occurrence => Assert.Equal(occurrence.BaselineRank, baselineRanks[occurrence.BaselineCandidateIds[0]]));

        var silverCandidateIds = occurrences.SelectMany(item => item.BaselineCandidateIds).ToHashSet(StringComparer.Ordinal);
        var selectedSilver = occurrences.Where(item => item.BaselineRank <= Budget).ToArray();
        var displacedSilver = occurrences.Where(item => item.BaselineRank > Budget).ToArray();
        var topNonSilver = baseline.Where(item => item.BaselineRank <= Budget && !silverCandidateIds.Contains(item.CandidateId)).ToArray();

        var counterfactuals = ScoreComponents.Select(component => ReplayWithout(component, baseline, occurrences)).ToArray();
        var exactOwner = DetermineOwner(counterfactuals, displacedSilver.Length);
        return new RankScoreDocument(
            documentId,
            relative,
            laneCDocument.GetProperty("documentSha256").GetString()!,
            laneCDocument.GetProperty("silverArtifactSha256").GetString()!,
            laneCDocument.GetProperty("censusArtifactSha256").GetString()!,
            laneCDocument.GetProperty("sourceLineExtractionFingerprint").GetString()!,
            new PopulationCounts(occurrences.Length, selectedSilver.Length, displacedSilver.Length, topNonSilver.Length),
            BuildFeatureInventory(selectedSilver, displacedSilver, topNonSilver),
            baseline.Select(candidate => new ScoreReconciliation(candidate.CandidateId, candidate.BaselineRank,
                candidate.CandidateScore, candidate.RecomputedScore, candidate.Components)).ToArray(),
            occurrences,
            counterfactuals,
            exactOwner);
    }

    private static IReadOnlyList<FeatureInventory> BuildFeatureInventory(
        IReadOnlyList<RankedSilverOccurrence> selected,
        IReadOnlyList<RankedSilverOccurrence> displaced,
        IReadOnlyList<CandidateFacts> topNonSilver)
    {
        var populations = new[]
        {
            ("silver_selected_at_160", selected.Select(item => item.Candidate with
            {
                Kind = item.Kind,
                SilverConfidence = item.SilverConfidence,
            }).ToArray()),
            ("silver_rank_over_160", displaced.Select(item => item.Candidate with
            {
                Kind = item.Kind,
                SilverConfidence = item.SilverConfidence,
            }).ToArray()),
            ("top160_non_silver_competitor", topNonSilver.ToArray()),
        };
        return populations.Select(population => new FeatureInventory(
            population.Item1, population.Item2.Length,
            Distribution(population.Item2, item => item.Producer),
            Distribution(population.Item2, item => item.RepresentationKind),
            Distribution(population.Item2, item => item.Scope),
            Distribution(population.Item2, item => item.MarkerClass),
            Distribution(population.Item2, item => item.LayoutClass),
            Distribution(population.Item2, item => item.Kind),
            Distribution(population.Item2, item => item.SilverConfidence),
            ComponentRates(population.Item2))).ToArray();
    }

    private static IReadOnlyList<NamedCount> Distribution(IEnumerable<CandidateFacts> population, Func<CandidateFacts, string> value) => population
        .GroupBy(value, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new NamedCount(group.Key, group.Count())).ToArray();

    private static IReadOnlyList<NamedCount> ComponentRates(IEnumerable<CandidateFacts> population) => ScoreComponents
        .Select(component => new NamedCount(component, population.Count(item => item.Components.Any(value => value.Name == component))))
        .Where(item => item.Count > 0).ToArray();

    private static CounterfactualReplay ReplayWithout(string component, IReadOnlyList<CandidateFacts> baseline,
        IReadOnlyList<RankedSilverOccurrence> occurrences)
    {
        var replay = baseline.Select(candidate => candidate with { RecomputedScore = candidate.ScoreWithout(component) })
            .OrderByDescending(candidate => candidate.RecomputedScore)
            .ThenByDescending(candidate => candidate.EscalationScore)
            .ThenBy(candidate => candidate.Page)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Select((candidate, index) => (candidate.CandidateId, Rank: index + 1))
            .ToDictionary(item => item.CandidateId, item => item.Rank, StringComparer.Ordinal);

        var rows = occurrences.Select(occurrence =>
        {
            var rank = occurrence.BaselineCandidateIds.Min(id => replay[id]);
            return new CounterfactualOccurrence(occurrence.SilverStableId, occurrence.BaselineRank, rank,
                occurrence.BaselineRank > Budget && rank <= Budget,
                occurrence.BaselineRank <= Budget && rank > Budget);
        }).ToArray();
        return new CounterfactualReplay(component, rows.Length, rows.Count(row => row.BaselineRank <= Budget),
            rows.Count(row => row.ReplayedRank <= Budget), rows.Count(row => row.RecoveredFromRankingLoss),
            rows.Count(row => row.LostFromSelected), rows.OrderBy(row => row.ReplayedRank - row.BaselineRank)
                .ElementAt(rows.Length / 2).ReplayedRank - rows.OrderBy(row => row.ReplayedRank - row.BaselineRank)
                .ElementAt(rows.Length / 2).BaselineRank, rows);
    }

    private static OwnerConclusion DetermineOwner(IReadOnlyList<CounterfactualReplay> replays, int rankingLoss)
    {
        var materialThreshold = Math.Max(8, (int)Math.Ceiling(rankingLoss * .10));
        var material = replays.Where(replay => replay.RecoveredFromRankingLoss >= materialThreshold && replay.LostFromSelected == 0)
            .OrderByDescending(replay => replay.RecoveredFromRankingLoss).ToArray();
        return material.Length switch
        {
            0 => new OwnerConclusion("UNRESOLVED", "NOT_YET_JUSTIFIED", materialThreshold, 0, rankingLoss,
                "No single existing score component has a replayed, collateral-free material recovery under the frozen sensitivity rule."),
            1 => new OwnerConclusion("SCORE_COMPONENT_OVERREWARD", "NOT_YET_JUSTIFIED", materialThreshold,
                material[0].RecoveredFromRankingLoss, rankingLoss,
                $"{material[0].Component} has the strongest material, collateral-free sensitivity result; this proves a score-owner candidate, not a production remediation."),
            _ => new OwnerConclusion("MIXED", "NOT_YET_JUSTIFIED", materialThreshold, 0, rankingLoss,
                "More than one existing component has material sensitivity; no unique scoring owner is established."),
        };
    }

    private static string BuildSummary(RankScoreArtifact artifact)
    {
        var text = new StringBuilder();
        text.AppendLine("# N1.6 Exact Rank-Score Owner Diagnosis");
        text.AppendLine();
        text.AppendLine("Offline sensitivity only. It uses frozen `MODEL_ASSISTED_SILVER` occurrences and makes no remediation proposal.");
        text.AppendLine();
        foreach (var document in artifact.Documents)
        {
            var counts = document.PopulationCounts;
            var strongest = document.Counterfactuals.OrderByDescending(replay => replay.RecoveredFromRankingLoss).First();
            text.AppendLine($"## {document.DocumentId}");
            text.AppendLine();
            text.AppendLine($"- full candidate `{counts.FullCandidate}`, selected `<=160` `{counts.SilverSelectedAt160}`, ranking losses `{counts.SilverRankOver160}`, top-160 non-silver competitors `{counts.Top160NonSilverCompetitor}`");
            text.AppendLine($"- strongest single-component sensitivity: `{strongest.Component}`, recovered `{strongest.RecoveredFromRankingLoss}`, displaced selected `{strongest.LostFromSelected}`");
            text.AppendLine($"- exact-owner coverage: `{document.ExactOwner.ExplainedRankingLosses}/{document.ExactOwner.RankingLosses}` ranking losses");
            text.AppendLine($"- exact score owner: `{document.ExactOwner.Owner}`; ranker remediation: `{document.ExactOwner.RankerRemediation}`");
            text.AppendLine();
        }
        return text.ToString();
    }

    private static string SourceFingerprint(IReadOnlyList<PdfLine> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', lines.Select((line, index) => $"{index}|{PdfCandidateProvenance.LineId(line)}"))))).ToLowerInvariant();
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Normalize(string value) => value.Replace("\r\n", "\n");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record CandidateFacts(string CandidateId, int BaselineRank, int Page, double CandidateScore,
        double RecomputedScore, double EscalationScore, string Producer, string RepresentationKind, string Scope,
        string MarkerClass, string LayoutClass, string Kind, string SilverConfidence, IReadOnlyList<ScoreComponent> Components)
    {
        public static CandidateFacts Create(RankedCandidate candidate, int rank, PdfCandidateProvenance provenance, PdfCandidateContext context)
        {
            var components = BuildComponents(candidate);
            var recomputed = Clamp(components.Sum(component => component.Contribution));
            Assert.Equal(candidate.CandidateScore, recomputed, 12);
            return new CandidateFacts(candidate.SourceId, rank, candidate.Page, candidate.CandidateScore, recomputed,
                candidate.EscalationScore, "not_exposed_by_production_snapshot", provenance.RepresentationKind.ToString(),
                candidate.Scope, context.Source.Marker?.Family.ToString() ?? "none",
                ResolveLayoutClass(context.Source.ObservedEvidence), "not_applicable", "not_applicable", components);
        }

        public double ScoreWithout(string component) => Clamp(Components.Where(value => value.Name != component).Sum(value => value.Contribution));

        private static IReadOnlyList<ScoreComponent> BuildComponents(RankedCandidate candidate)
        {
            var values = new List<ScoreComponent> { new("base", .10) };
            void AddIf(string signal, double contribution) { if (candidate.PositiveSignals.Contains(signal)) values.Add(new(signal, contribution)); }
            void SubtractIf(string signal, double contribution) { if (candidate.NegativeSignals.Contains(signal)) values.Add(new(signal, contribution)); }
            AddIf("labelled_numbering_marker", .42);
            AddIf("unlabelled_numbering_prefix", .10);
            AddIf("standalone", .18);
            AddIf("marker_title_composite", .28);
            AddIf("canonical_marker_title", .22);
            AddIf("layout_prominence", .16);
            AddIf("opens_content", .12);
            SubtractIf("table_scope", -.60);
            SubtractIf("running_page_scope", -.75);
            SubtractIf("header_footer_zone", -.15);
            SubtractIf("long_marker_body_window", -.52);
            return values;
        }

        private static double Clamp(double value) => Math.Clamp(value, 0, 1);
        private static string ResolveLayoutClass(IReadOnlyList<string> evidence) => evidence.Contains("table_like") ? "table_like" :
            evidence.Contains("header_footer_zone") ? "header_footer_zone" :
            evidence.Contains("standalone_line") ? "standalone_line" : "multi_line_cluster";
    }

    private sealed record RankedSilverOccurrence(string SilverStableId, IReadOnlyList<string> SourceLineIds, JsonElement SourceSpan,
        int BaselineRank, CandidateFacts Candidate, IReadOnlyList<string> BaselineCandidateIds, string Kind, string SilverConfidence,
        IReadOnlyList<CompetitorDelta> CompetitorDeltas)
    {
        public static RankedSilverOccurrence Read(JsonElement item, IReadOnlyDictionary<string, int> lineIndex,
            PdfCandidateRankingSnapshot snapshot, IReadOnlyDictionary<string, CandidateFacts> byId)
        {
            var lineIds = item.GetProperty("sourceLineIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var indexes = lineIds.Select(id => lineIndex.TryGetValue(id, out var index) ? index : -1).ToArray();
            Assert.DoesNotContain(-1, indexes);
            var candidate = item.GetProperty("candidate");
            var candidateId = candidate.GetProperty("candidateId").GetString()!;
            Assert.True(snapshot.Provenance[candidateId].Covers(indexes));
            var facts = byId[candidateId];
            Assert.Equal(item.GetProperty("coveringRank").GetInt32(), facts.BaselineRank);
            var competitors = item.GetProperty("immediatelyAboveCompetitors").EnumerateArray().Select(competitor =>
            {
                var id = competitor.GetProperty("candidateId").GetString()!;
                var other = byId[id];
                return new CompetitorDelta(id, other.BaselineRank, other.CandidateScore, facts.CandidateScore,
                    other.CandidateScore - facts.CandidateScore, other.Components, facts.Components);
            }).ToArray();
            return new RankedSilverOccurrence(item.GetProperty("silverStableId").GetString()!, lineIds,
                item.GetProperty("sourceSpan").Clone(), facts.BaselineRank, facts, [candidateId],
                item.GetProperty("kind").GetString()!, item.GetProperty("silverConfidence").GetString()!, competitors);
        }
    }

    private sealed record RankScoreArtifact(int SchemaVersion, string ArtifactKind, string BaseLaneCRevision,
        string LaneCArtifactSha256, string Identity, string LabelAuthority, bool HumanAdjudicated, string ClaimBoundary,
        IReadOnlyList<RankScoreDocument> Documents);
    private sealed record RankScoreDocument(string DocumentId, string DocumentRelativePath, string DocumentSha256,
        string SilverArtifactSha256, string CensusArtifactSha256, string SourceLineExtractionFingerprint,
        PopulationCounts PopulationCounts, IReadOnlyList<FeatureInventory> FeatureInventory,
        IReadOnlyList<ScoreReconciliation> ScoreReconciliation, IReadOnlyList<RankedSilverOccurrence> FullCandidateOccurrences,
        IReadOnlyList<CounterfactualReplay> Counterfactuals, OwnerConclusion ExactOwner);
    private sealed record PopulationCounts(int FullCandidate, int SilverSelectedAt160, int SilverRankOver160, int Top160NonSilverCompetitor);
    private sealed record FeatureInventory(string Population, int Count, IReadOnlyList<NamedCount> Producer,
        IReadOnlyList<NamedCount> RepresentationKind, IReadOnlyList<NamedCount> Scope, IReadOnlyList<NamedCount> MarkerClass,
        IReadOnlyList<NamedCount> LayoutClass, IReadOnlyList<NamedCount> Kind, IReadOnlyList<NamedCount> SilverConfidence,
        IReadOnlyList<NamedCount> ScoreComponentPresence);
    private sealed record NamedCount(string Value, int Count);
    private sealed record ScoreComponent(string Name, double Contribution);
    private sealed record ScoreReconciliation(string CandidateId, int Rank, double ExposedCandidateScore,
        double RecomputedCandidateScore, IReadOnlyList<ScoreComponent> Components);
    private sealed record CompetitorDelta(string CandidateId, int Rank, double CompetitorScore, double SilverScore,
        double ScoreDelta, IReadOnlyList<ScoreComponent> CompetitorComponents, IReadOnlyList<ScoreComponent> SilverComponents);
    private sealed record CounterfactualReplay(string Component, int FullCandidate, int BaselineSelectedAt160,
        int ReplayedSelectedAt160, int RecoveredFromRankingLoss, int LostFromSelected, int MedianRankDelta,
        IReadOnlyList<CounterfactualOccurrence> Occurrences);
    private sealed record CounterfactualOccurrence(string SilverStableId, int BaselineRank, int ReplayedRank,
        bool RecoveredFromRankingLoss, bool LostFromSelected);
    private sealed record OwnerConclusion(string Owner, string RankerRemediation, int MaterialRecoveryThreshold,
        int ExplainedRankingLosses, int RankingLosses, string Basis);
}
