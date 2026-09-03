using System.Globalization;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Round 1B: replays the existing deterministic construction trace for the exact 47 frozen misses.
/// The trace is diagnostic-only. A producer/owner is assigned only when the trace's source geometry
/// matches the reviewed occurrence; otherwise the row remains unresolved rather than becoming a
/// title-containment claim.
/// </summary>
public sealed class PdfRound1CandidateCausalClassificationProbe
{
    private static readonly (string DocumentId, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    private sealed record Miss(
        string DocumentId,
        string DocumentSha256,
        string GoldStableId,
        int Page,
        string[] SourceLineIds,
        string SourceText,
        string? Marker,
        string CensusStatus);

    private sealed record ClassificationRow(
        string DocumentId,
        string DocumentSha256,
        string GoldStableId,
        int Page,
        string[] SourceLineIds,
        object SourceSpan,
        string SourceText,
        string? Marker,
        string SourceRepresentation,
        IReadOnlyList<string> PreGroupCandidateIds,
        IReadOnlyList<string> PostGroupCandidateIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ProducerCandidateIds,
        IReadOnlyDictionary<string, string> ProducerDecisions,
        string GroupOperation,
        string FirstLoss,
        string Owner,
        string CensusStatus,
        string EvidenceStrength,
        bool ExactSourceLineMatch,
        IReadOnlyList<string> OverlappingCandidates);

    [Fact]
    public void WriteCausalClassification()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1_CAUSAL_CLASSIFICATION");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var rows = Documents.SelectMany(document => ClassifyDocument(root, document)).ToArray();
        var byOwner = rows.GroupBy(row => row.Owner, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round1_candidate_loss_causal_classification",
            phase = "round1b",
            modelCalls = 0,
            productionChanges = false,
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only",
            input = new { totalReviewed = 422, candidatePresent = 375, candidateMisses = 47 },
            ownerTaxonomy = new[]
            {
                "SOURCE_REPRESENTATION_MISSING", "CANDIDATE_PRODUCER_NOT_TRIGGERED",
                "CANDIDATE_BOUNDARY_MISMATCH", "CANDIDATE_MERGE_DESTROYS_OCCURRENCE",
                "HARD_FILTER_REJECTION", "OCCURRENCE_JOIN_MISMATCH", "UNRESOLVED"
            },
            partition = byOwner,
            clusters = rows.GroupBy(row => row.Owner, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .Select(group => new
                {
                    owner = group.Key,
                    count = group.Count(),
                    documents = group.Select(row => row.DocumentId).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray(),
                    exampleIdentities = group.Take(3).Select(row => row.GoldStableId).ToArray(),
                    repeatedFailure = group.Select(row => row.DocumentId).Distinct().Count() > 1,
                    remediationStatus = "NOT_YET_JUSTIFIED",
                    invariant = "not promoted by this diagnosis-only trace"
                }).ToArray(),
            occurrences = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void FrozenClassificationPartitionHas47Rows()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1_CAUSAL_CLASSIFICATION");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        var input = report.RootElement.GetProperty("input");
        Assert.Equal(422, input.GetProperty("totalReviewed").GetInt32());
        Assert.Equal(375, input.GetProperty("candidatePresent").GetInt32());
        Assert.Equal(47, input.GetProperty("candidateMisses").GetInt32());
        Assert.Equal(47, report.RootElement.GetProperty("occurrences").GetArrayLength());
        var sum = report.RootElement.GetProperty("partition").EnumerateObject().Sum(property => property.Value.GetInt32());
        Assert.Equal(47, sum);
    }

    private static IEnumerable<ClassificationRow> ClassifyDocument(string root, (string DocumentId, string RelativePath) document)
    {
        var censusPath = Path.Combine(root, "eval", "benchmark-n3", "census", $"{document.DocumentId}-n3.3-census.v1.json");
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", $"{document.DocumentId}-n3.2-silver-model-assisted.v1.json");
        var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
        using var census = JsonDocument.Parse(File.ReadAllText(censusPath));
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        var headings = silver.RootElement.GetProperty("headingOccurrences")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("goldStableId").GetString()!, StringComparer.Ordinal);
        var misses = census.RootElement.GetProperty("occurrences").GetProperty("candidateConstructionLoss")
            .EnumerateArray()
            .Select(loss =>
            {
                var stableId = loss.GetProperty("stableId").GetString()!;
                var heading = headings[stableId];
                return new Miss(document.DocumentId,
                    census.RootElement.GetProperty("documentSha256").GetString()!, stableId,
                    heading.GetProperty("page").GetInt32(),
                    heading.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                    heading.GetProperty("sourceText").GetString()!,
                    heading.TryGetProperty("marker", out var marker) ? marker.GetString() : null,
                    loss.GetProperty("status").GetString()!);
            }).ToArray();

        var traces = PdfLayoutEvidenceOutline.TraceCandidateConstruction(docxPath, misses.Select(m => m.SourceText));
        var traceByText = traces.GroupBy(trace => trace.ExpectedText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        var lineIndex = snapshot.Lines.Select((line, index) => (LineId: PdfCandidateProvenance.LineId(line), index))
            .GroupBy(item => item.LineId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        foreach (var miss in misses)
        {
            var requiredIndexes = miss.SourceLineIds
                .Select(id => lineIndex.TryGetValue(id, out var index) ? index : -1)
                .ToArray();
            if (requiredIndexes.Any(index => index < 0))
            {
                yield return Row(miss, "OCCURRENCE_JOIN_MISMATCH", "reviewed source line identity is absent from current snapshot", null, false);
                continue;
            }

            var requiredCoordinates = miss.SourceLineIds.Select(ReviewedCoordinateKey).ToHashSet(StringComparer.Ordinal);
            var covering = snapshot.Provenance.Values
                .Where(provenance => requiredIndexes.All(provenance.LineIndexes.Contains))
                .Select(provenance => provenance.CandidateSourceId)
                .ToArray();
            var touching = snapshot.Provenance.Values
                .Where(provenance => requiredIndexes.Any(provenance.LineIndexes.Contains))
                .Select(provenance => provenance.CandidateSourceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (covering.Length > 0)
            {
                yield return Row(miss, "OCCURRENCE_JOIN_MISMATCH", "frozen census miss is contradicted by a full-coverage candidate", null, true, covering);
                continue;
            }

            if (touching.Length > 0)
            {
                yield return Row(miss, "CANDIDATE_BOUNDARY_MISMATCH", "candidate touches reviewed source lines but covers only a subset", null, true, touching);
                continue;
            }

            var annotations = requiredIndexes.Select(index => snapshot.Annotations[index]).ToArray();
            if (annotations.All(annotation => annotation.ExcludeFromCandidateGrouping))
            {
                yield return Row(miss, "HARD_FILTER_REJECTION", "all reviewed source lines are excluded from candidate grouping", null, true);
                continue;
            }

            // The title-based construction trace is admitted only when its raw source window names
            // this exact occurrence. A text collision is diagnostic ambiguity, not producer proof.
            if (!traceByText.TryGetValue(miss.SourceText, out var trace))
            {
                yield return Row(miss, "UNRESOLVED", "no exact construction trace", null, true);
                continue;
            }
            var tracedCoordinates = trace.SourceLines.Select(line => TraceCoordinateKey(line.SourceId)).ToHashSet(StringComparer.Ordinal);
            var occurrenceMatched = requiredCoordinates.Count > 0 && requiredCoordinates.All(tracedCoordinates.Contains);
            if (!occurrenceMatched)
            {
                yield return Row(miss, "UNRESOLVED", "title trace resolves to a different source occurrence", trace, true);
                continue;
            }

            var owner = trace.FirstLoss switch
            {
                "representation_missing" or "not_represented" => "SOURCE_REPRESENTATION_MISSING",
                "line_filter_gate" => "HARD_FILTER_REJECTION",
                "semantic_block_grouping" when trace.GroupOperation.Contains("absorbed", StringComparison.OrdinalIgnoreCase) ||
                    trace.GroupOperation.Contains("truncated", StringComparison.OrdinalIgnoreCase) => "CANDIDATE_MERGE_DESTROYS_OCCURRENCE",
                "semantic_block_grouping" => "CANDIDATE_BOUNDARY_MISMATCH",
                "candidate_producer" => "CANDIDATE_PRODUCER_NOT_TRIGGERED",
                "candidate_available" => "CANDIDATE_BOUNDARY_MISMATCH",
                _ => "UNRESOLVED"
            };
            yield return Row(miss, owner, trace.FirstLoss, trace, true);
        }
    }

    private static ClassificationRow Row(Miss miss, string owner, string firstLoss, PdfCandidateConstructionTrace? trace = null,
        bool exactSourceLineMatch = false, IReadOnlyList<string>? overlappingCandidates = null) =>
        new(
            miss.DocumentId,
            miss.DocumentSha256,
            miss.GoldStableId,
            miss.Page,
            miss.SourceLineIds,
            new { startLineId = miss.SourceLineIds.FirstOrDefault(), endLineId = miss.SourceLineIds.LastOrDefault() },
            miss.SourceText,
            miss.Marker,
            trace is null ? "not_traced" : "represented_in_trace_window",
            trace?.PreGroupCandidateIds ?? [],
            trace?.PostGroupCandidateIds ?? [],
            trace?.ProducerCandidateIds ?? new Dictionary<string, IReadOnlyList<string>>(),
            trace?.ProducerDecisions ?? new Dictionary<string, string>(),
            trace?.GroupOperation ?? "not_traced",
            firstLoss,
            owner,
            miss.CensusStatus,
            trace is null ? "insufficient" : "diagnostic_trace_only",
            exactSourceLineMatch,
            overlappingCandidates ?? []);

    private static string ReviewedCoordinateKey(string lineId)
    {
        var parts = lineId.Split('|');
        return parts.Length >= 3 ? $"{parts[0]}|{parts[1]}|{parts[2]}" : lineId;
    }

    private static string TraceCoordinateKey(string sourceId)
    {
        if (!sourceId.StartsWith("p", StringComparison.Ordinal)) return sourceId;
        var parts = sourceId.Split(':');
        return parts.Length >= 3
            ? string.Create(CultureInfo.InvariantCulture, $"{parts[0][1..]}|{parts[1][1..]}|{parts[2][1..]}")
            : sourceId;
    }
}
