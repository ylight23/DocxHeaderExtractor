using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Source-line-first audit of the ten frozen candidate-generation losses in 004.
/// This probe only observes existing construction stages and never changes extraction behavior.
/// </summary>
public sealed class PdfCandidateGenerationFirstLossProbe
{
    private const string DocumentId = "004";
    private const string RelativePath = @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx";
    private static readonly string[] Producers =
        ["BuildBroadCandidates", "BuildWideAuditCandidates", "BuildSupplementCandidates"];

    [Fact]
    public void WriteCandidateGenerationFirstLoss()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_CANDIDATE_GENERATION_FIRST_LOSS");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var inputPath = Path.Combine(root, "eval", "accuracy-round1", "candidate-loss-causal-classification.v1.json");
        using var input = JsonDocument.Parse(File.ReadAllText(inputPath));
        var losses = input.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(row => row.GetProperty("DocumentId").GetString() == DocumentId)
            .ToArray();
        Assert.Equal(10, losses.Length);

        var requests = losses.ToDictionary(
            row => row.GetProperty("GoldStableId").GetString()!,
            row => (IReadOnlyList<string>)row.GetProperty("SourceLineIds").EnumerateArray()
                .Select(value => value.GetString()!).ToArray(), StringComparer.Ordinal);
        var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", RelativePath);
        var lineage = PdfLayoutEvidenceOutline.TraceCandidateBoundaryLineage(docxPath, requests)
            .ToDictionary(item => item.OccurrenceId, StringComparer.Ordinal);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        var lineById = snapshot.Lines
            .Select((line, index) => (Id: PdfCandidateProvenance.LineId(line), Line: line, Index: index))
            .ToDictionary(item => item.Id, item => (item.Line, item.Index), StringComparer.Ordinal);

        var occurrences = losses.Select(loss => BuildOccurrence(loss, lineage[loss.GetProperty("GoldStableId").GetString()!],
            snapshot, lineById, root)).ToArray();
        Assert.Equal(10, occurrences.Length);

        var recurrencePath = Path.Combine(root, "eval", "accuracy-round1", "candidate-boundary-lineage.v1.json");
        using var recurrence = JsonDocument.Parse(File.ReadAllText(recurrencePath));
        var recurringDocs = recurrence.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(row => row.GetProperty("firstLossComponent").GetString() == "PdfSemanticBlockGrouper.Build")
            .Select(row => row.GetProperty("documentId").GetString()!)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value).ToArray();

        var byOperation = occurrences.GroupBy(row => row.FirstLossOperation, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_candidate_generation_first_loss",
            phase = "candidate_generation_cross_document_diagnosis",
            sourceAuthority = "Round 1A frozen 004 candidate-construction-loss population",
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only",
            TOTAL_REVIEWED_LOSSES = 10,
            BY_FIRST_REJECTING_OPERATION = byOperation,
            occurrences,
            COUNTERFACTUAL_RECOVERY = new
            {
                boundarySplit = new
                {
                    occurrences = occurrences.Count(row => row.RootCause == "LINE_GROUP_BOUNDARY_SPLIT"),
                    recovered = "9/9 by the previously committed constrained boundary simulation",
                    reference = "eval/accuracy-round1/candidate-boundary-counterfactual.v1.json",
                    productionReplay = false
                },
                absorbedOrTruncated = new
                {
                    occurrences = occurrences.Count(row => row.RootCause == "LINE_GROUP_ABSORBED_OR_TRUNCATED"),
                    recovered = "not measured; no exact safe predicate isolated",
                    productionReplay = false
                }
            },
            COLLATERAL_CANDIDATE_COST = new
            {
                constrainedBoundarySimulation = "0 new candidates, 0 duplicate candidates, 0 inflation in its measured scope; not equivalent to a safe grouping fix",
                groupingPredicateRemoval = "not run; exact predicate/invariant is not proven",
                negativeControls = "not measured by this diagnosis-only pass"
            },
            CROSS_DOCUMENT_RECURRENCE = recurringDocs.Length > 1 ? "PROVEN" : "NOT_PROVEN",
            crossDocumentEvidence = new
            {
                mechanism = "PdfSemanticBlockGrouper.Build / LINE_GROUP loses one complete source occurrence boundary",
                documents = recurringDocs,
                source = "candidate-boundary-lineage.v1.json; existing reviewed occurrence traces",
                interpretation = "producer recurrence is observed; a safe general remediation invariant is not established"
            },
            REMEDIATION_JUSTIFIED = "NO",
            providerCalls = 0,
            productionCodeChanged = false,
            finalStatus = "DIAGNOSIS_ONLY_REMEDIATION_NOT_JUSTIFIED"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void Frozen004LossPopulationHasTenRows()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_CANDIDATE_GENERATION_FIRST_LOSS");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(10, report.RootElement.GetProperty("TOTAL_REVIEWED_LOSSES").GetInt32());
        Assert.Equal(10, report.RootElement.GetProperty("occurrences").GetArrayLength());
        Assert.Equal(0, report.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(report.RootElement.GetProperty("productionCodeChanged").GetBoolean());
    }

    private static OccurrenceReport BuildOccurrence(
        JsonElement loss,
        PdfCandidateBoundaryLineage trace,
        PdfCandidateRankingSnapshot snapshot,
        IReadOnlyDictionary<string, (PdfLine Line, int Index)> lineById,
        string root)
    {
        var sourceLineIds = loss.GetProperty("SourceLineIds").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        var required = sourceLineIds.Where(lineById.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var firstLoss = trace.Stages.Skip(1).FirstOrDefault(stage =>
            !stage.CandidateLineIds.Values.Any(lines => required.All(lines.Contains)));
        var firstLossOperation = firstLoss?.Operation ?? "NONE";
        var frozenOwner = loss.GetProperty("Owner").GetString();
        var rootCause = firstLoss?.Component == "PdfSemanticBlockGrouper.Build"
            ? frozenOwner == "CANDIDATE_MERGE_DESTROYS_OCCURRENCE"
                ? "LINE_GROUP_ABSORBED_OR_TRUNCATED"
                : "LINE_GROUP_BOUNDARY_SPLIT"
            : "UNRESOLVED";

        var stageAvailability = Producers.ToDictionary(producer => producer, producer =>
        {
            var stage = trace.Stages.FirstOrDefault(item => item.Component == producer);
            return stage is not null && stage.CandidateLineIds.Values.Any(lines => required.All(lines.Contains));
        }, StringComparer.Ordinal);
        var indexes = sourceLineIds.Where(lineById.ContainsKey).Select(id => lineById[id].Index).OrderBy(x => x).ToArray();
        var neighborStart = indexes.Length == 0 ? 0 : Math.Max(0, indexes[0] - 2);
        var neighborEnd = indexes.Length == 0 ? 0 : Math.Min(snapshot.Lines.Count, indexes[^1] + 3);
        var neighbors = snapshot.Lines
            .Select((line, index) => (line, index))
            .Where(item => item.index >= neighborStart && item.index < neighborEnd)
            .Select(item => new
            {
                lineId = PdfCandidateProvenance.LineId(item.line),
                page = item.line.Page,
                text = item.line.Text,
                fontSize = item.line.FontSize,
                boldRatio = item.line.BoldRatio,
                fontName = item.line.FontName,
                left = item.line.Left,
                right = item.line.Right,
                y = item.line.Y,
                matchText = item.line.MatchText,
                annotation = snapshot.Annotations[item.index].Reason
            }).ToArray();
        var sourceFacts = sourceLineIds.Where(lineById.ContainsKey).Select(id =>
        {
            var item = lineById[id];
            var annotation = snapshot.Annotations[item.Index];
            return new
            {
                lineId = id,
                page = item.Line.Page,
                text = item.Line.Text,
                matchText = item.Line.MatchText,
                fontSize = item.Line.FontSize,
                boldRatio = item.Line.BoldRatio,
                fontName = item.Line.FontName,
                left = item.Line.Left,
                right = item.Line.Right,
                y = item.Line.Y,
                annotationReason = annotation.Reason,
                tableLike = annotation.TableLike,
                repeated = annotation.Repeated,
                headerFooterZone = annotation.HeaderFooterZone,
                excludedFromSemanticSamples = annotation.ExcludeFromSemanticSamples,
                excludedFromCandidateGrouping = annotation.ExcludeFromCandidateGrouping
            };
        }).ToArray();
        return new OccurrenceReport(
            loss.GetProperty("GoldStableId").GetString()!,
            loss.GetProperty("DocumentSha256").GetString()!,
            loss.GetProperty("Page").GetInt32(),
            sourceLineIds,
            loss.GetProperty("SourceText").GetString()!,
            loss.TryGetProperty("Marker", out var marker) ? marker.GetString() : null,
            loss.GetProperty("GoldStableId").GetString()!.Split('/')[1],
            sourceFacts,
            neighbors,
            stageAvailability,
            firstLoss?.Component ?? "UNRESOLVED",
            firstLossOperation,
            firstLoss is null ? "all stages cover occurrence or trace unavailable" : "no single stage candidate covers every sourceLineId",
            rootCause,
            trace.Stages,
            trace.Stages.Last().CandidateLineIds.Values.SelectMany(value => value).Distinct(StringComparer.Ordinal).ToArray(),
            rootCause == "LINE_GROUP_BOUNDARY_SPLIT"
                ? "constrained boundary simulation recovered this occurrence; no production predicate was removed"
                : "not measured; no exact grouping predicate isolated",
            rootCause == "LINE_GROUP_BOUNDARY_SPLIT" ? "0 in prior constrained simulation" : "not measured");
    }

    private sealed record OccurrenceReport(
        string SourceOccurrenceId,
        string DocumentSha256,
        int SourcePage,
        string[] SourceLineIds,
        string SourceText,
        string? Marker,
        string HeadingType,
        object SourceFacts,
        object SurroundingSourceLines,
        IReadOnlyDictionary<string, bool> ProducerAvailable,
        string FirstRejectingComponent,
        string FirstLossOperation,
        string FirstRejectingPredicate,
        string RootCause,
        IReadOnlyList<PdfCandidateBoundaryLineageStage> Stages,
        string[] DiagnosticCandidateIds,
        string CounterfactualRecovery,
        string CollateralCandidateCost);
}
