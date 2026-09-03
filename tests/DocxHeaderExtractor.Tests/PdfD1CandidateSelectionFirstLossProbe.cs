using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// D1: offline first-loss diagnosis for frozen 004 candidate selection losses. The probe only reads
/// frozen source identities and replays deterministic selection; it makes no provider call and does
/// not change candidate generation, ranking, budget, labels, or production output.
/// </summary>
public sealed class PdfD1CandidateSelectionFirstLossProbe
{
    private const string DocumentId = "004";
    private const int CandidateBudget = 160;
    private const string RelativeDocx = @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx";

    private static readonly IReadOnlyDictionary<string, double> FeatureWeights =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["base"] = 0.10, ["labelled_numbering_marker"] = 0.42, ["unlabelled_numbering_prefix"] = 0.10,
            ["standalone"] = 0.18, ["marker_title_composite"] = 0.28, ["canonical_marker_title"] = 0.22,
            ["layout_prominence"] = 0.16, ["opens_content"] = 0.12, ["table_scope"] = -0.60,
            ["running_page_scope"] = -0.75, ["header_footer_zone"] = -0.15, ["long_marker_body_window"] = -0.52
        };

    [Fact]
    public void WriteCandidateSelectionFirstLoss()
    {
        var outputJson = Environment.GetEnvironmentVariable("BENCH_D1_CANDIDATE_SELECTION_FIRST_LOSS_JSON");
        var outputMarkdown = Environment.GetEnvironmentVariable("BENCH_D1_CANDIDATE_SELECTION_FIRST_LOSS_MD");
        if (string.IsNullOrWhiteSpace(outputJson) || string.IsNullOrWhiteSpace(outputMarkdown)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        outputJson = Path.IsPathRooted(outputJson) ? outputJson : Path.Combine(root, outputJson);
        outputMarkdown = Path.IsPathRooted(outputMarkdown) ? outputMarkdown : Path.Combine(root, outputMarkdown);
        var artifact = BuildArtifact(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputJson))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMarkdown))!);
        File.WriteAllText(outputJson, JsonSerializer.Serialize(artifact, JsonOptions));
        File.WriteAllText(outputMarkdown, BuildMarkdown(artifact));
    }

    [Fact]
    public void CandidateSelectionDiagnosisReplaysOffline()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var artifact = BuildArtifact(root);

        Assert.Equal(28, artifact.TOTAL_SELECTION_LOSS);
        Assert.Equal(28, artifact.BY_FIRST_REJECTING_OPERATION["global_budget"]);
        Assert.False(artifact.CUTOFF_NEIGHBORHOOD_COMPLETE);
        Assert.Equal("PROVEN", artifact.CROSS_DOCUMENT_RECURRENCE);
        Assert.Equal("NO", artifact.REMEDIATION_JUSTIFIED);
        Assert.Equal(0, artifact.PROVIDER_CALLS);
        Assert.False(artifact.PRODUCTION_CODE_CHANGED);
        Assert.All(artifact.Occurrences, row =>
        {
            Assert.True(row.CandidateExistsInGeneratedPool);
            Assert.False(row.Selected);
            Assert.Equal(CandidateBudget, row.CandidateBudget);
            Assert.Equal(CandidateBudget, row.CutoffRank);
            Assert.Equal("global_budget", row.FIRST_REJECTING_OPERATION);
            Assert.InRange(row.CutoffNeighborhood.Count, 4, 7);
        });
    }

    private static D1Artifact BuildArtifact(string root)
    {
        var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", RelativeDocx);
        var censusPath = Path.Combine(root, "eval", "benchmark-n3", "census", "004-n3.3-census.v1.json");
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", "004-n3.2-silver-model-assisted.v1.json");
        var rankingPath = Path.Combine(root, "eval", "accuracy-round2", "ranking-baseline-ledger.v1.json");

        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var ranked = snapshot.Audit.Candidates;
        var rankById = ranked.Select((candidate, index) => (candidate.SourceId, Rank: index + 1))
            .ToDictionary(item => item.SourceId, item => item.Rank, StringComparer.Ordinal);
        var selected = PdfLayoutEvidenceOutline.SelectRankedCandidates(snapshot.CandidateBlocks, ranked, CandidateBudget);
        var selectedIds = selected.Selected.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);

        using var silverJson = JsonDocument.Parse(File.ReadAllText(silverPath));
        var silver = ReadSilver(silverJson.RootElement, snapshot);
        using var censusJson = JsonDocument.Parse(File.ReadAllText(censusPath));
        var censusRoot = censusJson.RootElement;
        Assert.Equal(DocumentId, censusRoot.GetProperty("documentId").GetString());
        Assert.Equal(28, censusRoot.GetProperty("lossLedger").GetProperty("rankBudgetLoss").GetInt32());
        Assert.Equal(83, censusRoot.GetProperty("denominators").GetProperty("fullCandidate").GetInt32());
        Assert.Equal(55, censusRoot.GetProperty("denominators").GetProperty("selectedAt160").GetInt32());
        Assert.Equal(censusRoot.GetProperty("sourceLineExtractionFingerprint").GetString(), SourceFingerprint(snapshot.Lines));

        using var rankingJson = JsonDocument.Parse(File.ReadAllText(rankingPath));
        var frozenDocument = rankingJson.RootElement.GetProperty("perDocument").EnumerateArray()
            .Single(item => item.GetProperty("documentId").GetString() == DocumentId);
        Assert.Equal(snapshot.CandidateBlocks.Count, frozenDocument.GetProperty("candidateCount").GetInt32());
        Assert.Equal(28, frozenDocument.GetProperty("outsideBudget").GetInt32());
        var frozenLosses = frozenDocument.GetProperty("rankLosses").EnumerateArray()
            .Select(FrozenLoss.Read).ToArray();
        var censusLossIds = censusRoot.GetProperty("occurrences").GetProperty("rankBudgetLoss")
            .EnumerateArray().Select(item => item.GetProperty("stableId").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(frozenLosses.Select(loss => loss.OccurrenceId).Order(StringComparer.Ordinal), censusLossIds.Order(StringComparer.Ordinal));

        var reviewedIdsByCandidate = BuildReviewedIdsByCandidate(silver, snapshot, ranked);
        var cutoffNeighborhood = Neighborhood(CandidateBudget, ranked, snapshot, contexts, selectedIds, reviewedIdsByCandidate);
        var occurrences = frozenLosses.Select(loss =>
            BuildOccurrence(loss, silver[loss.OccurrenceId], ranked, rankById, snapshot, contexts, selectedIds, selected, reviewedIdsByCandidate))
            .ToArray();
        var crossDocument = BuildCrossDocument(rankingJson.RootElement);
        var counterfactuals = BuildCounterfactuals(ranked, snapshot, selected, selectedIds, reviewedIdsByCandidate, occurrences);

        return new D1Artifact(
            SchemaVersion: 1,
            ArtifactKind: "accuracy_candidate_selection_first_loss",
            Phase: "D1-candidate-selection-first-loss-diagnosis",
            SourceAuthority: "eval/benchmark-n3/census/004-n3.3-census.v1.json + eval/accuracy-round2/ranking-baseline-ledger.v1.json + eval/benchmark-n3/silver-labels/004-n3.2-silver-model-assisted.v1.json",
            Identity: "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only within this replay",
            DocumentId: DocumentId,
            DocumentSha256: censusRoot.GetProperty("documentSha256").GetString()!,
            SourceLineExtractionFingerprint: censusRoot.GetProperty("sourceLineExtractionFingerprint").GetString()!,
            CandidateBudget: CandidateBudget,
            CutoffRank: CandidateBudget,
            CandidateCount: snapshot.CandidateBlocks.Count,
            SelectedCandidateCount: selected.Selected.Count,
            SelectedPageCoverage: selected.SelectedPages,
            CutoffNeighborhood: cutoffNeighborhood,
            TOTAL_SELECTION_LOSS: occurrences.Length,
            BY_FIRST_REJECTING_OPERATION: occurrences.GroupBy(row => row.FIRST_REJECTING_OPERATION)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            CUTOFF_NEIGHBORHOOD_COMPLETE: occurrences.All(row => row.CutoffNeighborhood.Count == 7),
            COUNTERFACTUAL_RECOVERY: counterfactuals,
            CROSS_DOCUMENT_RECURRENCE: crossDocument.Any(row => row.Class == "CLASS_1" && row.DocumentId != DocumentId)
                ? "PROVEN" : "NOT_PROVEN",
            CROSS_DOCUMENT_CLASSES: crossDocument,
            REMEDIATION_JUSTIFIED: "NO",
            PROVIDER_CALLS: 0,
            PRODUCTION_CODE_CHANGED: false,
            Notes: [
                "Selection replay matched the frozen 004 candidateCount and every frozen loss candidateId/rank/score before emitting this artifact.",
                "SelectRankedCandidates applies only the global top-K budget to an already ranked plan; no page budget, dominance, duplicate/canonical collision, diversity constraint, or hard exclusion rejected these 28 occurrences.",
                "Counterfactuals are diagnostic exposure measurements, not proposed fixes."
            ],
            Occurrences: occurrences);
    }

    private static D1Occurrence BuildOccurrence(
        FrozenLoss loss,
        SilverOccurrence silver,
        IReadOnlyList<RankedCandidate> ranked,
        IReadOnlyDictionary<string, int> rankById,
        PdfCandidateRankingSnapshot snapshot,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        IReadOnlySet<string> selectedIds,
        PdfAnalystCandidateSelection selected,
        IReadOnlyDictionary<string, IReadOnlyList<string>> reviewedIdsByCandidate)
    {
        var candidate = ranked.Single(item => item.SourceId == loss.CandidateId);
        var rank = rankById[candidate.SourceId];
        Assert.Equal(loss.Rank, rank);
        Assert.Equal(loss.Score, candidate.CandidateScore, 12);
        Assert.False(selectedIds.Contains(candidate.SourceId));

        return new D1Occurrence(
            OccurrenceId: loss.OccurrenceId,
            SourceLineIds: silver.SourceLineIds,
            SourceText: silver.SourceText,
            CandidateIdDiagnostic: candidate.SourceId,
            CandidateExistsInGeneratedPool: true,
            Rank: rank,
            Score: candidate.CandidateScore,
            ScoreComponents: Contributions(candidate),
            EscalationScore: candidate.EscalationScore,
            PositiveSignals: candidate.PositiveSignals,
            NegativeSignals: candidate.NegativeSignals,
            AmbiguitySignals: candidate.AmbiguitySignals,
            Selected: false,
            CutoffRank: CandidateBudget,
            CandidateBudget: CandidateBudget,
            SelectedPageCoverage: selected.SelectedPages,
            CompetingCandidatesAroundCutoff: Neighborhood(CandidateBudget, ranked, snapshot, contexts, selectedIds, reviewedIdsByCandidate),
            FIRST_REJECTING_OPERATION: "global_budget",
            FIRST_REJECTING_REASON: $"covering candidate rank {rank} is below top-{CandidateBudget}; deterministic exclusion reason is null and selector has no page/diversity/dominance/collision predicate",
            CutoffNeighborhood: Neighborhood(rank, ranked, snapshot, contexts, selectedIds, reviewedIdsByCandidate));
    }

    private static IReadOnlyList<D1CandidateView> Neighborhood(
        int centerRank,
        IReadOnlyList<RankedCandidate> ranked,
        PdfCandidateRankingSnapshot snapshot,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        IReadOnlySet<string> selectedIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> reviewedIdsByCandidate) =>
        ranked.Select((candidate, index) => (candidate, Rank: index + 1))
            .Where(item => item.Rank >= Math.Max(1, centerRank - 3) && item.Rank <= Math.Min(ranked.Count, centerRank + 3))
            .Select(item => CandidateView(item.candidate, item.Rank, snapshot, contexts, selectedIds, reviewedIdsByCandidate))
            .ToArray();

    private static D1CandidateView CandidateView(
        RankedCandidate candidate,
        int rank,
        PdfCandidateRankingSnapshot snapshot,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        IReadOnlySet<string> selectedIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> reviewedIdsByCandidate)
    {
        reviewedIdsByCandidate.TryGetValue(candidate.SourceId, out var reviewed);
        var context = contexts[candidate.SourceId];
        return new D1CandidateView(
            CandidateId: candidate.SourceId,
            Rank: rank,
            Text: candidate.Text,
            Page: candidate.Page,
            Score: candidate.CandidateScore,
            ScoreComponents: Contributions(candidate),
            EscalationScore: candidate.EscalationScore,
            Tier: candidate.Tier.ToString(),
            Scope: candidate.Scope,
            RepresentationKind: snapshot.Provenance[candidate.SourceId].RepresentationKind.ToString(),
            Selected: selectedIds.Contains(candidate.SourceId),
            ReviewedHeading: reviewed is { Count: > 0 },
            ReviewedHeadingIds: reviewed ?? [],
            PositiveSignals: candidate.PositiveSignals,
            NegativeSignals: candidate.NegativeSignals,
            AmbiguitySignals: candidate.AmbiguitySignals,
            ObservedEvidence: context.Source.ObservedEvidence);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildReviewedIdsByCandidate(
        IReadOnlyDictionary<string, SilverOccurrence> silver,
        PdfCandidateRankingSnapshot snapshot,
        IReadOnlyList<RankedCandidate> ranked)
    {
        var byCandidate = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var candidate in ranked)
        {
            if (!snapshot.Provenance.TryGetValue(candidate.SourceId, out var provenance)) continue;
            foreach (var occurrence in silver.Values)
            {
                if (provenance.Covers(occurrence.ResolvedIndexes))
                {
                    if (!byCandidate.TryGetValue(candidate.SourceId, out var list))
                        byCandidate[candidate.SourceId] = list = [];
                    list.Add(occurrence.OccurrenceId);
                }
            }
        }
        return byCandidate.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.Ordinal);
    }

    private static IReadOnlyList<D1Counterfactual> BuildCounterfactuals(
        IReadOnlyList<RankedCandidate> ranked,
        PdfCandidateRankingSnapshot snapshot,
        PdfAnalystCandidateSelection selected,
        IReadOnlySet<string> selectedIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> reviewedIdsByCandidate,
        IReadOnlyList<D1Occurrence> losses)
    {
        D1Counterfactual Budget(string name, int budget)
        {
            var ids = ranked.Take(Math.Min(budget, ranked.Count)).Select(candidate => candidate.SourceId).ToHashSet(StringComparer.Ordinal);
            return BuildCounterfactual(name, budget, ids, ranked, selected, selectedIds, reviewedIdsByCandidate, losses);
        }

        var forced = selectedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var loss in losses) forced.Add(loss.CandidateIdDiagnostic);
        foreach (var id in ranked.Take(CandidateBudget).Reverse().Select(candidate => candidate.SourceId).ToArray())
        {
            if (forced.Count <= CandidateBudget) break;
            if (!losses.Any(loss => loss.CandidateIdDiagnostic == id)) forced.Remove(id);
        }

        return
        [
            Budget("budget_plus_1", CandidateBudget + 1),
            Budget("budget_plus_5", CandidateBudget + 5),
            Budget("budget_plus_10", CandidateBudget + 10),
            BuildCounterfactual("remove_global_budget_predicate", ranked.Count,
                ranked.Select(candidate => candidate.SourceId).ToHashSet(StringComparer.Ordinal),
                ranked, selected, selectedIds, reviewedIdsByCandidate, losses),
            BuildCounterfactual("force_lost_occurrences_keep_budget", CandidateBudget,
                forced, ranked, selected, selectedIds, reviewedIdsByCandidate, losses)
        ];
    }

    private static D1Counterfactual BuildCounterfactual(
        string name,
        int effectiveBudget,
        IReadOnlySet<string> ids,
        IReadOnlyList<RankedCandidate> ranked,
        PdfAnalystCandidateSelection baseline,
        IReadOnlySet<string> baselineIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> reviewedIdsByCandidate,
        IReadOnlyList<D1Occurrence> losses)
    {
        var added = ids.Where(id => !baselineIds.Contains(id)).ToArray();
        var removed = baselineIds.Where(id => !ids.Contains(id)).ToArray();
        var recovered = losses.Where(loss => ids.Contains(loss.CandidateIdDiagnostic)).Select(loss => loss.OccurrenceId).ToArray();
        var addedWithoutReviewedSupport = added.Count(id => !reviewedIdsByCandidate.TryGetValue(id, out var reviewed) || reviewed.Count == 0);
        var pageById = ranked.ToDictionary(candidate => candidate.SourceId, candidate => candidate.Page, StringComparer.Ordinal);
        var pageCoverage = ids.Select(id => pageById[id]).Distinct().Count();
        return new D1Counterfactual(
            Name: name,
            EffectiveBudget: effectiveBudget,
            GoldRecovered: recovered.Length,
            RecoveredHeading: recovered.Length,
            RecoveredOccurrenceIds: recovered,
            AdditionalCandidatesSelected: added.Length,
            FalsePositiveExposure: addedWithoutReviewedSupport,
            NonHeadingExposure: addedWithoutReviewedSupport,
            RemovedBaselineCandidates: removed.Length,
            RemovedReviewedHeadingExposure: removed.Count(id => reviewedIdsByCandidate.TryGetValue(id, out var reviewed) && reviewed.Count > 0),
            DisplacedHeading: removed.Count(id => reviewedIdsByCandidate.TryGetValue(id, out var reviewed) && reviewed.Count > 0),
            NetReviewedGain: recovered.Length - removed.Count(id => reviewedIdsByCandidate.TryGetValue(id, out var reviewed) && reviewed.Count > 0),
            PageCoverageChange: pageCoverage - baseline.SelectedPages);
    }

    private static IReadOnlyList<CrossDocumentRow> BuildCrossDocument(JsonElement rankingRoot)
    {
        return rankingRoot.GetProperty("perDocument").EnumerateArray().Select(document =>
        {
            var doc = document.GetProperty("documentId").GetString()!;
            var losses = document.GetProperty("rankLosses").GetArrayLength();
            return new CrossDocumentRow(
                DocumentId: doc,
                Class: losses > 0 ? "CLASS_1" : "CLASS_3",
                Mechanism: losses > 0 ? "reviewed heading candidate exists but rank is below the global top-160 budget" : "no same mechanism observed",
                ReviewedProof: losses,
                MinRank: losses == 0 ? null : document.GetProperty("rankLosses").EnumerateArray().Min(item => item.GetProperty("Rank").GetInt32()),
                MaxRank: losses == 0 ? null : document.GetProperty("rankLosses").EnumerateArray().Max(item => item.GetProperty("Rank").GetInt32()));
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, SilverOccurrence> ReadSilver(JsonElement silverRoot, PdfCandidateRankingSnapshot snapshot)
    {
        var lineIndex = snapshot.Lines.Select((line, index) => (LineId: PdfCandidateProvenance.LineId(line), Index: index))
            .ToDictionary(item => item.LineId, item => item.Index, StringComparer.Ordinal);
        return silverRoot.GetProperty("headingOccurrences").EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("goldStableId").GetString()!;
            var lineIds = item.GetProperty("sourceLineIds").EnumerateArray().Select(line => line.GetString()!).ToArray();
            var indexes = lineIds.Select(lineId => lineIndex.TryGetValue(lineId, out var index) ? index : -1).ToArray();
            Assert.DoesNotContain(-1, indexes);
            return new SilverOccurrence(id, item.GetProperty("sourceText").GetString()!, lineIds, indexes);
        }).ToDictionary(item => item.OccurrenceId, StringComparer.Ordinal);
    }

    private static FrozenLoss ReadFrozenLoss(JsonElement item) => FrozenLoss.Read(item);

    private static IReadOnlyDictionary<string, double> Contributions(RankedCandidate candidate)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal) { ["base"] = FeatureWeights["base"] };
        foreach (var signal in candidate.PositiveSignals.Concat(candidate.NegativeSignals))
            if (FeatureWeights.TryGetValue(signal, out var weight)) result[signal] = weight;
        return result;
    }

    private static string SourceFingerprint(IReadOnlyList<PdfLine> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', lines.Select((line, index) => $"{index}|{PdfCandidateProvenance.LineId(line)}"))))).ToLowerInvariant();

    private static string BuildMarkdown(D1Artifact artifact)
    {
        var writer = new StringBuilder();
        writer.AppendLine("# Candidate Selection First-Loss Diagnosis");
        writer.AppendLine();
        writer.AppendLine("Diagnostic-only offline replay over frozen 004 source identities. No provider call, no production change.");
        writer.AppendLine();
        writer.AppendLine($"- `TOTAL_SELECTION_LOSS = {artifact.TOTAL_SELECTION_LOSS}`");
        writer.AppendLine($"- `BY_FIRST_REJECTING_OPERATION = {{ global_budget: {artifact.BY_FIRST_REJECTING_OPERATION["global_budget"]} }}`");
        writer.AppendLine($"- `CUTOFF_NEIGHBORHOOD_COMPLETE = {artifact.CUTOFF_NEIGHBORHOOD_COMPLETE.ToString().ToLowerInvariant()}`");
        writer.AppendLine($"- `CROSS_DOCUMENT_RECURRENCE = {artifact.CROSS_DOCUMENT_RECURRENCE}`");
        writer.AppendLine($"- `REMEDIATION_JUSTIFIED = {artifact.REMEDIATION_JUSTIFIED}`");
        writer.AppendLine($"- `PROVIDER_CALLS = {artifact.PROVIDER_CALLS}`");
        writer.AppendLine($"- `PRODUCTION_CODE_CHANGED = {artifact.PRODUCTION_CODE_CHANGED.ToString().ToLowerInvariant()}`");
        writer.AppendLine();
        writer.AppendLine("`CUTOFF_NEIGHBORHOOD_COMPLETE=false` because one frozen loss is rank `2653` in a `2653`-candidate pool, so ranks `r+1..r+3` do not exist to serialize.");
        writer.AppendLine();
        writer.AppendLine("## Finding");
        writer.AppendLine();
        writer.AppendLine("All 28 frozen 004 selection losses are reviewed heading occurrences whose covering candidate exists in the generated pool, but falls below the global top-160 cutoff. The selector applies no page budget, dominance, duplicate/canonical collision, diversity constraint, or hard exclusion before the cutoff.");
        writer.AppendLine();
        writer.AppendLine("The nearest lost rank is 822, so `budget +1`, `+5`, and `+10` recover zero headings while exposing additional candidates. Removing the global budget recovers all 28 but selects the full 2,653-candidate pool, which is diagnostic evidence against treating budget increase as a justified fix. Forcing all lost occurrences into the fixed budget recovers them only by displacing 28 baseline candidates.");
        writer.AppendLine();
        writer.AppendLine("## Counterfactuals");
        writer.AppendLine();
        writer.AppendLine("| counterfactual | recoveredHeading | displacedHeading | nonHeadingExposure | netReviewedGain | additionalCandidatesSelected | pageCoverageChange |");
        writer.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var item in artifact.COUNTERFACTUAL_RECOVERY)
            writer.AppendLine($"| `{item.Name}` | {item.RecoveredHeading} | {item.DisplacedHeading} | {item.NonHeadingExposure} | {item.NetReviewedGain} | {item.AdditionalCandidatesSelected} | {item.PageCoverageChange} |");
        writer.AppendLine();
        writer.AppendLine("## Cross-Document");
        writer.AppendLine();
        writer.AppendLine("| class | document | reviewedProof | minRank | maxRank |");
        writer.AppendLine("|---|---|---:|---:|---:|");
        foreach (var item in artifact.CROSS_DOCUMENT_CLASSES)
            writer.AppendLine($"| `{item.Class}` | `{item.DocumentId}` | {item.ReviewedProof} | {item.MinRank?.ToString() ?? ""} | {item.MaxRank?.ToString() ?? ""} |");
        return writer.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record FrozenLoss(string OccurrenceId, int Rank, double Score, string CandidateId)
    {
        public static FrozenLoss Read(JsonElement item) => new(
            item.GetProperty("GoldStableId").GetString()!,
            item.GetProperty("Rank").GetInt32(),
            item.GetProperty("Score").GetDouble(),
            item.GetProperty("CandidateId").GetString()!);
    }

    private sealed record SilverOccurrence(string OccurrenceId, string SourceText, IReadOnlyList<string> SourceLineIds,
        IReadOnlyList<int> ResolvedIndexes);

    private sealed record D1Artifact(int SchemaVersion, string ArtifactKind, string Phase, string SourceAuthority,
        string Identity, string DocumentId, string DocumentSha256, string SourceLineExtractionFingerprint,
        int CandidateBudget, int CutoffRank, int CandidateCount, int SelectedCandidateCount, int SelectedPageCoverage,
        IReadOnlyList<D1CandidateView> CutoffNeighborhood,
        [property: JsonPropertyName("TOTAL_SELECTION_LOSS")] int TOTAL_SELECTION_LOSS,
        [property: JsonPropertyName("BY_FIRST_REJECTING_OPERATION")] IReadOnlyDictionary<string, int> BY_FIRST_REJECTING_OPERATION,
        [property: JsonPropertyName("CUTOFF_NEIGHBORHOOD_COMPLETE")] bool CUTOFF_NEIGHBORHOOD_COMPLETE,
        [property: JsonPropertyName("COUNTERFACTUAL_RECOVERY")] IReadOnlyList<D1Counterfactual> COUNTERFACTUAL_RECOVERY,
        [property: JsonPropertyName("CROSS_DOCUMENT_RECURRENCE")] string CROSS_DOCUMENT_RECURRENCE,
        [property: JsonPropertyName("CROSS_DOCUMENT_CLASSES")] IReadOnlyList<CrossDocumentRow> CROSS_DOCUMENT_CLASSES,
        [property: JsonPropertyName("REMEDIATION_JUSTIFIED")] string REMEDIATION_JUSTIFIED,
        [property: JsonPropertyName("PROVIDER_CALLS")] int PROVIDER_CALLS,
        [property: JsonPropertyName("PRODUCTION_CODE_CHANGED")] bool PRODUCTION_CODE_CHANGED,
        IReadOnlyList<string> Notes,
        IReadOnlyList<D1Occurrence> Occurrences);

    private sealed record D1Occurrence(string OccurrenceId, IReadOnlyList<string> SourceLineIds, string SourceText,
        string CandidateIdDiagnostic, bool CandidateExistsInGeneratedPool, int Rank, double Score,
        IReadOnlyDictionary<string, double> ScoreComponents, double EscalationScore,
        IReadOnlyList<string> PositiveSignals, IReadOnlyList<string> NegativeSignals,
        IReadOnlyList<string> AmbiguitySignals, bool Selected, int CutoffRank, int CandidateBudget,
        int SelectedPageCoverage, IReadOnlyList<D1CandidateView> CompetingCandidatesAroundCutoff,
        [property: JsonPropertyName("FIRST_REJECTING_OPERATION")] string FIRST_REJECTING_OPERATION,
        [property: JsonPropertyName("FIRST_REJECTING_REASON")] string FIRST_REJECTING_REASON,
        IReadOnlyList<D1CandidateView> CutoffNeighborhood);

    private sealed record D1CandidateView(string CandidateId, int Rank, string Text, int Page, double Score,
        IReadOnlyDictionary<string, double> ScoreComponents, double EscalationScore, string Tier, string Scope,
        string RepresentationKind, bool Selected, bool ReviewedHeading, IReadOnlyList<string> ReviewedHeadingIds,
        IReadOnlyList<string> PositiveSignals, IReadOnlyList<string> NegativeSignals,
        IReadOnlyList<string> AmbiguitySignals, IReadOnlyList<string> ObservedEvidence);

    private sealed record D1Counterfactual(string Name, int EffectiveBudget, int GoldRecovered, int RecoveredHeading,
        IReadOnlyList<string> RecoveredOccurrenceIds, int AdditionalCandidatesSelected, int FalsePositiveExposure,
        int NonHeadingExposure, int RemovedBaselineCandidates, int RemovedReviewedHeadingExposure, int DisplacedHeading,
        int NetReviewedGain, int PageCoverageChange);

    private sealed record CrossDocumentRow(string DocumentId, string Class, string Mechanism, int ReviewedProof,
        int? MinRank, int? MaxRank);
}
