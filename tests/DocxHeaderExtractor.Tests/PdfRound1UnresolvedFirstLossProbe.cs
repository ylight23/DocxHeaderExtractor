using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Round 1E: reclassifies only the frozen unresolved misses using source-line lineage.</summary>
public sealed class PdfRound1UnresolvedFirstLossProbe
{
    private static readonly (string DocumentId, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    [Fact]
    public void WriteUnresolvedFirstLoss()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1E_UNRESOLVED_FIRST_LOSS");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var inputPath = Path.Combine(root, "eval", "accuracy-round1",
            "candidate-loss-causal-classification.v1.json");
        using var input = JsonDocument.Parse(File.ReadAllText(inputPath));
        var unresolved = input.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(row => row.GetProperty("Owner").GetString() == "UNRESOLVED")
            .ToArray();
        Assert.Equal(27, unresolved.Length);

        var rows = new List<object>();
        foreach (var document in Documents)
        {
            var documentRows = unresolved.Where(row => row.GetProperty("DocumentId").GetString() == document.DocumentId).ToArray();
            if (documentRows.Length == 0) continue;
            var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
            var requests = documentRows.ToDictionary(
                row => row.GetProperty("GoldStableId").GetString()!,
                row => (IReadOnlyList<string>)row.GetProperty("SourceLineIds").EnumerateArray()
                    .Select(value => value.GetString()!).ToArray(), StringComparer.Ordinal);
            var lineage = PdfLayoutEvidenceOutline.TraceCandidateBoundaryLineage(docxPath, requests)
                .ToDictionary(item => item.OccurrenceId, StringComparer.Ordinal);
            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
            var lineIds = snapshot.Lines.Select(PdfCandidateProvenance.LineId).ToHashSet(StringComparer.Ordinal);

            foreach (var source in documentRows)
            {
                var id = source.GetProperty("GoldStableId").GetString()!;
                var required = requests[id];
                var missingSourceLines = required.Where(line => !lineIds.Contains(line)).ToArray();
                var trace = lineage[id];
                var fullCandidates = snapshot.Provenance.Values
                    .Where(candidate => required.All(line => candidate.LineIndexes.Any(index =>
                        string.Equals(PdfCandidateProvenance.LineId(snapshot.Lines[index]), line, StringComparison.Ordinal))))
                    .Select(candidate => candidate.CandidateSourceId).ToArray();
                var touchingCandidates = snapshot.Provenance.Values
                    .Where(candidate => required.Any(line => candidate.LineIndexes.Any(index =>
                        string.Equals(PdfCandidateProvenance.LineId(snapshot.Lines[index]), line, StringComparison.Ordinal))))
                    .Select(candidate => candidate.CandidateSourceId).Distinct(StringComparer.Ordinal).ToArray();
                var classification = Classify(trace, missingSourceLines, fullCandidates, touchingCandidates);
                rows.Add(new
                {
                    documentId = document.DocumentId,
                    documentSha256 = source.GetProperty("DocumentSha256").GetString(),
                    goldStableId = id,
                    page = source.GetProperty("Page").GetInt32(),
                    sourceLineIds = required,
                    sourceText = source.GetProperty("SourceText").GetString(),
                    priorOwner = "UNRESOLVED",
                    owner = classification.Owner,
                    firstLossStage = classification.Stage,
                    firstLossOperation = classification.Operation,
                    firstLossReason = classification.Reason,
                    missingSourceLineIds = missingSourceLines,
                    fullCoverageCandidateIds = fullCandidates,
                    touchingCandidateIds = touchingCandidates,
                    stages = trace.Stages,
                    evidence = classification.Evidence
                });
            }
        }

        var ownerCounts = rows.Select(row => JsonSerializer.SerializeToElement(row))
            .GroupBy(row => row.GetProperty("owner").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var ownerOrder = new[]
        {
            "SOURCE_REPRESENTATION_MISSING", "CANDIDATE_PRODUCER_NOT_TRIGGERED",
            "CANDIDATE_BOUNDARY_MISMATCH", "CANDIDATE_MERGE_DESTROYS_OCCURRENCE",
            "HARD_FILTER_REJECTION", "OCCURRENCE_JOIN_MISMATCH", "UNRESOLVED"
        };
        var clusters = ownerOrder.Select(owner =>
        {
            var group = rows.Select(row => JsonSerializer.SerializeToElement(row))
                .Where(row => row.GetProperty("owner").GetString() == owner).ToArray();
            return new
            {
                owner,
                count = group.Length,
                documents = group.Select(row => row.GetProperty("documentId").GetString()!)
                    .Distinct(StringComparer.Ordinal).OrderBy(value => value).ToArray(),
                firstLossStages = group.Select(row => row.GetProperty("firstLossStage").GetString()!)
                    .Distinct(StringComparer.Ordinal).ToArray(),
                crossDocumentRecurrence = group.Select(row => row.GetProperty("documentId").GetString())
                    .Distinct(StringComparer.Ordinal).Count() > 1,
                status = group.Length == 0 ? "UNRESOLVED" : "NOT_YET_JUSTIFIED",
                sharedInvariant = "not established by diagnosis-only source lineage",
                positiveExamples = "not separately reviewed in Round 1E; no positive control promoted",
                negativeControls = "375 frozen candidate-present occurrences remain outside this input cohort",
                estimatedCandidateRecovery = "not measured; requires a separate counterfactual",
                candidateInflationRisk = "not measured; no remediation simulated"
            };
        }).Where(cluster => cluster.count > 0).ToArray();

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round1_candidate_unresolved_first_loss",
            phase = "round1e",
            modelCalls = 0,
            productionChanges = false,
            rankingChanged = false,
            inputUnresolved = 27,
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only",
            partition = ownerOrder.ToDictionary(owner => owner, owner => ownerCounts.GetValueOrDefault(owner), StringComparer.Ordinal),
            clusters,
            occurrences = rows,
            recurringRemediationCandidates = clusters.Where(cluster => cluster.crossDocumentRecurrence &&
                cluster.status == "REMEDIATION_CANDIDATE").Select(cluster => cluster.owner).ToArray(),
            finalStatus = clusters.Any(cluster => cluster.crossDocumentRecurrence)
                ? "RECURRING_CLASS_IDENTIFIED_REMEDIATION_NOT_YET_JUSTIFIED"
                : "NO_RECURRING_CLASS_PROVEN"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void FrozenUnresolvedAccountingHas27Rows()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1E_UNRESOLVED_FIRST_LOSS");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(27, report.RootElement.GetProperty("inputUnresolved").GetInt32());
        Assert.Equal(27, report.RootElement.GetProperty("occurrences").GetArrayLength());
        var sum = report.RootElement.GetProperty("partition").EnumerateObject()
            .Sum(property => property.Value.GetInt32());
        Assert.Equal(27, sum);
    }

    private static (string Owner, string Stage, string Operation, string Reason, string Evidence) Classify(
        PdfCandidateBoundaryLineage trace, IReadOnlyList<string> missingSourceLines,
        IReadOnlyList<string> fullCandidates, IReadOnlyList<string> touchingCandidates)
    {
        if (missingSourceLines.Count > 0)
            return ("OCCURRENCE_JOIN_MISMATCH", "PdfSourceFacts", "SOURCE_OCCURRENCE",
                "reviewed source line identity is absent from current source snapshot", "source identity unavailable");
        if (fullCandidates.Count > 0)
            return ("OCCURRENCE_JOIN_MISMATCH", "MergeCandidateSets", "MERGE",
                "current candidate fully covers the reviewed occurrence", "frozen miss contradicted by current pool");
        if (trace.FirstLossComponent == "BuildBroadCandidates" ||
            trace.FirstLossComponent == "BuildWideAuditCandidates" ||
            trace.FirstLossComponent == "BuildSupplementCandidates")
            return ("CANDIDATE_PRODUCER_NOT_TRIGGERED", trace.FirstLossComponent, trace.FirstLossOperation,
                trace.FirstLossReason, "source lines survived grouping but no producer emitted a covering candidate");
        if (trace.FirstLossComponent == "PdfSemanticBlockGrouper.Build" && touchingCandidates.Count > 0)
            return ("CANDIDATE_BOUNDARY_MISMATCH", trace.FirstLossComponent, trace.FirstLossOperation,
                trace.FirstLossReason, "grouping/pool contains only partial source-line coverage");
        if (trace.FirstLossComponent == "PdfSemanticBlockGrouper.Build")
            return ("CANDIDATE_MERGE_DESTROYS_OCCURRENCE", trace.FirstLossComponent, trace.FirstLossOperation,
                trace.FirstLossReason, "line-group stage loses the complete source boundary before producers");
        return ("UNRESOLVED", trace.FirstLossComponent, trace.FirstLossOperation, trace.FirstLossReason,
            "available lineage does not identify a selective first-loss owner");
    }
}
