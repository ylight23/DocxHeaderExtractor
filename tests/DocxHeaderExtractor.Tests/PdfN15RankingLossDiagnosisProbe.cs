using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N1.5: frozen, model-free rank-loss diagnosis for the N1.2-S silver populations in 029 and 042.
/// Source occurrence identity is authoritative. Candidate ids are emitted only as same-run diagnostics.
/// This probe does not alter a score, a budget, a label, or any production route.
/// </summary>
public sealed class PdfN15RankingLossDiagnosisProbe
{
    private const int SelectedBudget = 160;
    private const string BaseCodeRevision = "a04bf0f9496ca4211f03c2a4227e7f550ae3fda8";
    private static readonly int[] RecallBudgets = [40, 80, 160, 200, 320, 500, 640, 1000, 1600];
    private static readonly (string Stem, string Relative)[] Documents =
    [
        ("029", @"02_hop_dong_mua_sam\029_WB_RFP_Works_DesignBuild_2021.docx"),
        ("042", @"03_tai_chinh_ke_toan\042_IDA_Financial_Statements_June_2025.docx"),
    ];

    [Fact]
    public void WriteRankingDiagnosis()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("BENCH_N15_OUTPUT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        Directory.CreateDirectory(outputDirectory);
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var artifact = BuildArtifact(root);
        var json = JsonSerializer.Serialize(artifact, JsonOptions);
        File.WriteAllText(Path.Combine(outputDirectory, "ranking-029-042.v1.json"), json);
        File.WriteAllText(Path.Combine(outputDirectory, "ranking-029-042-summary.v1.md"), BuildSummary(artifact));
    }

    [Fact]
    public void CommittedDiagnosisReproducesByteForByte()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "benchmark-n0", "diagnostics", "ranking-029-042");
        var jsonPath = Path.Combine(directory, "ranking-029-042.v1.json");
        var markdownPath = Path.Combine(directory, "ranking-029-042-summary.v1.md");
        if (!File.Exists(jsonPath) || !File.Exists(markdownPath)) return;

        var artifact = BuildArtifact(root);
        Assert.Equal(Normalize(JsonSerializer.Serialize(artifact, JsonOptions)), Normalize(File.ReadAllText(jsonPath)));
        Assert.Equal(Normalize(BuildSummary(artifact)), Normalize(File.ReadAllText(markdownPath)));
    }

    [Fact]
    public void DiagnosisKeepsN13DenominatorsAndSourceAuthority()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var artifact = BuildArtifact(root);
        var docs = artifact.Documents.ToDictionary(d => d.DocumentId, StringComparer.Ordinal);

        Assert.Equal(149, docs["029"].Denominators.FullCandidate);
        Assert.Equal(3, docs["029"].Denominators.SelectedAt160);
        Assert.Equal(148, docs["042"].Denominators.FullCandidate);
        Assert.Equal(12, docs["042"].Denominators.SelectedAt160);
        Assert.Contains("sourceLineIds", artifact.Identity, StringComparison.Ordinal);
        Assert.Contains("candidateId is diagnostics-only", artifact.Identity, StringComparison.Ordinal);

        foreach (var document in artifact.Documents)
        {
            Assert.Equal(document.Denominators.FullCandidate,
                document.RankBuckets.Sum(bucket => bucket.Count));
            Assert.Equal(1d, document.RecallAtK.All, 12);
            var recalls = RecallBudgets.Select(k => document.RecallAtK.At[k.ToString()]).ToArray();
            Assert.True(recalls.Zip(recalls.Skip(1)).All(pair => pair.First <= pair.Second));
            Assert.All(document.FullCandidateOccurrences, row => Assert.NotEmpty(row.SourceLineIds));
        }
    }

    private static RankingArtifact BuildArtifact(string root)
    {
        var documents = Documents.Select(document => BuildDocument(root, document)).ToArray();
        return new RankingArtifact(
            SchemaVersion: 1,
            ArtifactKind: "n1_5_silver_ranking_loss_diagnosis",
            BaseCodeRevision,
            Identity: "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only within this replay and is never cross-run identity",
            LabelAuthority: "MODEL_ASSISTED_SILVER",
            HumanAdjudicated: false,
            ClaimBoundary: "SILVER_PROXY_ONLY",
            Documents: documents);
    }

    private static RankingDocument BuildDocument(string root, (string Stem, string Relative) document)
    {
        var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.Relative);
        var silverPath = Path.Combine(root, "eval", "benchmark-n0", "silver-labels", $"{document.Stem}-n1.2-silver-model-assisted.v1.json");
        var censusPath = Path.Combine(root, "eval", "benchmark-n0", "census", $"{document.Stem}-n1.3-census.v1.json");
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        using var census = JsonDocument.Parse(File.ReadAllText(censusPath));

        var silverRoot = silver.RootElement;
        var sourcePacket = silverRoot.TryGetProperty("sourcePacket", out var packet) ? packet : silverRoot;
        var documentSha256 = sourcePacket.GetProperty("documentSha256").GetString()!;
        var expectedFingerprint = sourcePacket.GetProperty("sourceLineExtractionFingerprint").GetString()!;
        Assert.Equal(documentSha256, Sha256(docxPath));

        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        Assert.Equal(expectedFingerprint, SourceFingerprint(snapshot.Lines));
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var ranked = snapshot.Audit.Candidates;
        var rankById = ranked.Select((candidate, index) => (candidate.SourceId, Rank: index + 1))
            .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
        var indexByLineId = snapshot.Lines.Select((line, index) => (LineId: PdfCandidateProvenance.LineId(line), index))
            .ToDictionary(x => x.LineId, x => x.index, StringComparer.Ordinal);

        var occurrences = silverRoot.GetProperty("headingOccurrences").EnumerateArray()
            .Select(item => SilverOccurrence.Read(item, indexByLineId))
            .ToArray();
        Assert.All(occurrences, occurrence => Assert.Equal(occurrence.SourceLineIds.Count, occurrence.ResolvedIndexes.Count));

        var classifications = PdfExtractorQualityBenchmarkProbe.Classify(docxPath,
            occurrences.Select(o => new PdfExtractorQualityBenchmarkProbe.Occurrence(o.StableId, [], o.ResolvedIndexes)).ToList())
            .ToDictionary(item => item.Occurrence.Label, StringComparer.Ordinal);

        var rows = occurrences.Select(occurrence => BuildOccurrenceRow(
            occurrence, classifications[occurrence.StableId], snapshot, ranked, rankById, contexts)).ToArray();
        var full = rows.Where(row => row.Status == "full").OrderBy(row => row.CoveringRank).ToArray();
        var censusDenominators = census.RootElement.GetProperty("denominators");
        var denominators = new RankingDenominators(
            censusDenominators.GetProperty("silverReviewed").GetInt32(),
            full.Length,
            full.Count(row => row.CoveringRank <= SelectedBudget),
            rows.Count(row => row.Status != "full"),
            full.Count(row => row.CoveringRank > SelectedBudget));

        Assert.Equal(censusDenominators.GetProperty("fullCandidate").GetInt32(), denominators.FullCandidate);
        Assert.Equal(censusDenominators.GetProperty("selectedAt160").GetInt32(), denominators.SelectedAt160);
        Assert.Equal(census.RootElement.GetProperty("lossLedger").GetProperty("candidateConstructionLoss").GetInt32(), denominators.CandidateConstructionLoss);
        Assert.Equal(census.RootElement.GetProperty("lossLedger").GetProperty("rankBudgetLoss").GetInt32(), denominators.RankingLoss);

        var rankBuckets = BuildBuckets(full);
        Assert.Equal(denominators.FullCandidate, rankBuckets.Sum(bucket => bucket.Count));
        var recall = BuildRecall(full);
        var byKind = BuildBreakdown(full, row => row.Kind);
        var byConfidence = BuildBreakdown(full, row => row.SilverConfidence);
        var conclusion = Conclude(recall);

        return new RankingDocument(
            DocumentId: document.Stem,
            DocumentRelativePath: document.Relative.Replace('\\', '/'),
            DocumentSha256: documentSha256,
            SilverArtifactSha256: Sha256(silverPath),
            CensusArtifactSha256: Sha256(censusPath),
            SourceLineExtractionFingerprint: expectedFingerprint,
            Denominators: denominators,
            RankStatistics: BuildStatistics(full.Select(row => row.CoveringRank!.Value).ToArray()),
            RecallAtK: recall,
            RankBuckets: rankBuckets,
            ByKind: byKind,
            BySilverConfidence: byConfidence,
            DiagnosticConclusion: conclusion,
            FullCandidateOccurrences: full);
    }

    private static RankingOccurrence BuildOccurrenceRow(
        SilverOccurrence occurrence,
        PdfExtractorQualityBenchmarkProbe.OccurrenceClassification classification,
        PdfCandidateRankingSnapshot snapshot,
        IReadOnlyList<RankedCandidate> ranked,
        IReadOnlyDictionary<string, int> rankById,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts)
    {
        if (classification.Status != "full")
            return new RankingOccurrence(occurrence.StableId, occurrence.Page, occurrence.SourceLineIds, occurrence.SourceSpan,
                occurrence.Kind, occurrence.SilverConfidence, classification.Status, null, null, null, []);

        var covering = ranked.Where(candidate => snapshot.Provenance.TryGetValue(candidate.SourceId, out var provenance) &&
                provenance.Covers(occurrence.ResolvedIndexes))
            .OrderBy(candidate => rankById[candidate.SourceId])
            .ToArray();
        Assert.NotEmpty(covering);
        var target = covering[0];
        var rank = rankById[target.SourceId];
        Assert.Equal(classification.CoveringRank, rank);
        Assert.Equal(classification.CoveringCandidateId, target.SourceId);
        var context = contexts[target.SourceId];
        var provenance = snapshot.Provenance[target.SourceId];
        var competitors = rank <= SelectedBudget ? [] : ranked
            .Skip(Math.Max(0, rank - 4)).Take(Math.Min(3, rank - 1))
            .Select((candidate, index) => BuildCandidateEvidence(candidate, rank - 3 + index, snapshot.Provenance[candidate.SourceId], contexts[candidate.SourceId]))
            .ToArray();

        return new RankingOccurrence(occurrence.StableId, occurrence.Page, occurrence.SourceLineIds, occurrence.SourceSpan,
            occurrence.Kind, occurrence.SilverConfidence, "full", rank,
            BuildCandidateEvidence(target, rank, provenance, context), BucketFor(rank), competitors);
    }

    private static CandidateEvidence BuildCandidateEvidence(RankedCandidate candidate, int rank, PdfCandidateProvenance provenance,
        PdfCandidateContext context) => new(
            CandidateId: candidate.SourceId,
            Rank: rank,
            Page: candidate.Page,
            Text: candidate.Text,
            RepresentationKind: provenance.RepresentationKind.ToString(),
            CandidateScore: candidate.CandidateScore,
            EscalationScore: candidate.EscalationScore,
            Tier: candidate.Tier.ToString(),
            Scope: candidate.Scope,
            DomainRole: context.Source.DomainRole.ToString(),
            Marker: context.Source.Marker?.Family.ToString(),
            ObservedEvidence: context.Source.ObservedEvidence,
            PositiveSignals: candidate.PositiveSignals,
            NegativeSignals: candidate.NegativeSignals,
            AmbiguitySignals: candidate.AmbiguitySignals);

    private static IReadOnlyList<RankBucket> BuildBuckets(IReadOnlyList<RankingOccurrence> rows) =>
    [
        Count("selected_at_160", rows, rank => rank <= 160),
        Count("161_200", rows, rank => rank is >= 161 and <= 200),
        Count("201_320", rows, rank => rank is >= 201 and <= 320),
        Count("321_500", rows, rank => rank is >= 321 and <= 500),
        Count("501_1000", rows, rank => rank is >= 501 and <= 1000),
        Count("1001_1600", rows, rank => rank is >= 1001 and <= 1600),
        Count("over_1600", rows, rank => rank > 1600),
    ];

    private static RankBucket Count(string bucket, IEnumerable<RankingOccurrence> rows, Func<int, bool> predicate) =>
        new(bucket, rows.Count(row => row.CoveringRank is int rank && predicate(rank)));

    private static RecallAtK BuildRecall(IReadOnlyList<RankingOccurrence> rows)
    {
        var at = RecallBudgets.ToDictionary(k => k.ToString(), k => rows.Count(row => row.CoveringRank <= k) / (double)rows.Count,
            StringComparer.Ordinal);
        var values = RecallBudgets.Select(k => at[k.ToString()]).ToArray();
        Assert.True(values.Zip(values.Skip(1)).All(pair => pair.First <= pair.Second));
        return new RecallAtK(at, 1d);
    }

    private static IReadOnlyList<Breakdown> BuildBreakdown(IEnumerable<RankingOccurrence> rows, Func<RankingOccurrence, string> key) => rows
        .GroupBy(key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new Breakdown(group.Key, group.Count(), group.Count(row => row.CoveringRank <= SelectedBudget),
            group.Count(row => row.CoveringRank > SelectedBudget), BuildStatistics(group.Select(row => row.CoveringRank!.Value).ToArray())))
        .ToArray();

    private static RankStatistics BuildStatistics(IReadOnlyList<int> ranks)
    {
        var ordered = ranks.OrderBy(rank => rank).ToArray();
        Assert.NotEmpty(ordered);
        int Percentile(double p) => ordered[Math.Clamp((int)Math.Ceiling(p * ordered.Length) - 1, 0, ordered.Length - 1)];
        return new RankStatistics(ordered[0], Percentile(.25), Percentile(.50), Percentile(.75), Percentile(.90), Percentile(.95), ordered[^1]);
    }

    private static string Conclude(RecallAtK recall)
    {
        var at160 = recall.At["160"];
        var at320 = recall.At["320"];
        var at640 = recall.At["640"];
        // Frozen diagnostic rule, intentionally conservative: it labels a budget boundary only when a
        // modest doubling captures most of the source-covered silver population. Anything else is not
        // evidence that merely raising the budget is sufficient.
        if (at320 >= .85 && at320 - at160 >= .50) return "BUDGET_LIMITED";
        if (at640 < .50) return "SCORE_SEPARATION_FAILURE";
        return "MIXED";
    }

    private static string BuildSummary(RankingArtifact artifact)
    {
        var writer = new StringBuilder();
        writer.AppendLine("# N1.5 Silver Ranking-Loss Diagnosis");
        writer.AppendLine();
        writer.AppendLine("Model-free diagnostic only. Labels are `MODEL_ASSISTED_SILVER`; claims are `SILVER_PROXY_ONLY`.");
        writer.AppendLine();
        foreach (var document in artifact.Documents)
        {
            var d = document.Denominators;
            var stats = document.RankStatistics;
            writer.AppendLine($"## {document.DocumentId}");
            writer.AppendLine();
            writer.AppendLine($"- `silverReviewed={d.SilverReviewed}`, `fullCandidate={d.FullCandidate}`, `selectedAt160={d.SelectedAt160}`");
            writer.AppendLine($"- `candidateConstructionLoss={d.CandidateConstructionLoss}`, `rankingLoss={d.RankingLoss}`");
            writer.AppendLine($"- rank: min `{stats.Min}`, p50 `{stats.P50}`, p90 `{stats.P90}`, p95 `{stats.P95}`, max `{stats.Max}`");
            writer.AppendLine($"- Recall@160 `{document.RecallAtK.At["160"]:P1}`, @320 `{document.RecallAtK.At["320"]:P1}`, @640 `{document.RecallAtK.At["640"]:P1}`, @all `{document.RecallAtK.All:P1}`");
            writer.AppendLine($"- diagnostic conclusion: `{document.DiagnosticConclusion}`");
            writer.AppendLine();
        }
        return writer.ToString();
    }

    private static string BucketFor(int rank) => rank switch
    {
        <= 160 => "selected_at_160",
        <= 200 => "161_200",
        <= 320 => "201_320",
        <= 500 => "321_500",
        <= 1000 => "501_1000",
        <= 1600 => "1001_1600",
        _ => "over_1600",
    };

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string SourceFingerprint(IReadOnlyList<PdfLine> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', lines.Select((line, index) => $"{index}|{PdfCandidateProvenance.LineId(line)}"))))).ToLowerInvariant();
    private static string Normalize(string value) => value.Replace("\r\n", "\n");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record SilverOccurrence(string StableId, int Page, IReadOnlyList<string> SourceLineIds, JsonElement SourceSpan,
        string Kind, string SilverConfidence, IReadOnlyList<int> ResolvedIndexes)
    {
        public static SilverOccurrence Read(JsonElement item, IReadOnlyDictionary<string, int> indexByLineId)
        {
            var lineIds = item.GetProperty("sourceLineIds").EnumerateArray().Select(line => line.GetString()!).ToArray();
            var resolved = lineIds.Select(lineId => indexByLineId.TryGetValue(lineId, out var index) ? index : -1)
                .Where(index => index >= 0).ToArray();
            return new SilverOccurrence(
                item.GetProperty("silverStableId").GetString()!,
                item.GetProperty("page").GetInt32(), lineIds,
                item.GetProperty("sourceSpan").Clone(),
                item.GetProperty("kind").GetString()!, item.GetProperty("silverConfidence").GetString()!, resolved);
        }
    }

    private sealed record RankingArtifact(int SchemaVersion, string ArtifactKind, string BaseCodeRevision, string Identity,
        string LabelAuthority, bool HumanAdjudicated, string ClaimBoundary, IReadOnlyList<RankingDocument> Documents);
    private sealed record RankingDocument(string DocumentId, string DocumentRelativePath, string DocumentSha256,
        string SilverArtifactSha256, string CensusArtifactSha256, string SourceLineExtractionFingerprint,
        RankingDenominators Denominators, RankStatistics RankStatistics, RecallAtK RecallAtK,
        IReadOnlyList<RankBucket> RankBuckets, IReadOnlyList<Breakdown> ByKind,
        IReadOnlyList<Breakdown> BySilverConfidence, string DiagnosticConclusion,
        IReadOnlyList<RankingOccurrence> FullCandidateOccurrences);
    private sealed record RankingDenominators(int SilverReviewed, int FullCandidate, int SelectedAt160,
        int CandidateConstructionLoss, int RankingLoss);
    private sealed record RankStatistics(int Min, int P25, int P50, int P75, int P90, int P95, int Max);
    private sealed record RecallAtK(IReadOnlyDictionary<string, double> At, double All);
    private sealed record RankBucket(string Bucket, int Count);
    private sealed record Breakdown(string Value, int FullCandidate, int SelectedAt160, int RankingLoss, RankStatistics RankStatistics);
    private sealed record RankingOccurrence(string SilverStableId, int Page, IReadOnlyList<string> SourceLineIds, JsonElement SourceSpan,
        string Kind, string SilverConfidence, string Status, int? CoveringRank, CandidateEvidence? Candidate,
        string? RankBucket, IReadOnlyList<CandidateEvidence> ImmediatelyAboveCompetitors);
    private sealed record CandidateEvidence(string CandidateId, int Rank, int Page, string Text, string RepresentationKind,
        double CandidateScore, double EscalationScore, string Tier, string Scope, string DomainRole, string? Marker,
        IReadOnlyList<string> ObservedEvidence, IReadOnlyList<string> PositiveSignals,
        IReadOnlyList<string> NegativeSignals, IReadOnlyList<string> AmbiguitySignals);
}
