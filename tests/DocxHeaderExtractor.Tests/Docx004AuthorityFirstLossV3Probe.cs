using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>V3 occurrence-safe candidate-boundary trace. Diagnostic only; no production input.</summary>
public sealed class Docx004AuthorityFirstLossV3Probe
{
    [Fact]
    public void WriteOccurrenceSafeTrace()
    {
        var output = Environment.GetEnvironmentVariable("DOCX004_AUTHORITY_FIRST_LOSS_V3");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy",
            "004_Luat_Dau_tu_61-2020-QH14_EN.docx");
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels",
            "004-n3.2-silver-model-assisted.v1.json");
        var runPath = Path.Combine(root, ".verify-build", "004-authority", "openrouter-qwen9b-160.json");
        var v2Path = Path.Combine(root, "eval", "accuracy", "004-authority-first-loss.v2.json");
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        using var run = JsonDocument.Parse(File.ReadAllText(runPath));
        using var v2 = JsonDocument.Parse(File.ReadAllText(v2Path));
        var occurrences = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray().ToArray();
        var requests = occurrences.ToDictionary(
            item => item.GetProperty("goldStableId").GetString()!,
            item => (IReadOnlyList<string>)item.GetProperty("sourceLineIds").EnumerateArray()
                .Select(line => line.GetString()!).ToArray(), StringComparer.Ordinal);
        var lineage = PdfLayoutEvidenceOutline.TraceCandidateBoundaryLineage(docx, requests)
            .ToDictionary(item => item.OccurrenceId, StringComparer.Ordinal);
        var selected = run.RootElement.GetProperty("routeAudit").GetProperty("selectedSourceIdentities")
            .EnumerateArray().Select(item => new SelectedIdentity(
                item.GetProperty("candidateIdDiagnostic").GetString()!,
                item.GetProperty("sourceLineIds").EnumerateArray().Select(line => line.GetString()!).ToArray()))
            .ToArray();
        var v2Exact = v2.RootElement.GetProperty("rows").EnumerateArray()
            .Where(item => item.GetProperty("selectedStatus").GetString() == "SELECTED")
            .Select(item => item.GetProperty("occurrenceId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var rows = occurrences.Select(item => BuildRow(item, lineage[item.GetProperty("goldStableId").GetString()!], selected)).ToArray();
        var v3Exact = rows.Where(item => item.SelectedStatus == "SELECTED_EXACT")
            .Select(item => item.OccurrenceId).ToHashSet(StringComparer.Ordinal);
        var duplicateCandidateBindings = rows.SelectMany(item => item.CandidateIds).Count() -
            rows.SelectMany(item => item.CandidateIds).Distinct(StringComparer.Ordinal).Count();
        var duplicateSourceLineBindings = rows.SelectMany(item => item.SourceLineIds)
            .GroupBy(id => id, StringComparer.Ordinal).Sum(group => Math.Max(0, group.Count() - 1));
        var classes = new[] { "DOCUMENT_TITLE", "CHAPTER", "SECTION", "ARTICLE" }
            .Select(name => new { semanticClass = name, expected = rows.Count(item => item.SemanticClass == name), exactSelected = rows.Count(item => item.SemanticClass == name && item.SelectedStatus == "SELECTED_EXACT"), candidateAvailable = rows.Count(item => item.SemanticClass == name && item.CandidateStatus == "CANDIDATE_AVAILABLE") })
            .ToArray();
        var report = new
        {
            schemaVersion = 3,
            artifactKind = "004_authority_first_loss_trace",
            canonicalPipeline = "AuthorityExtractionPipeline",
            baseRevision = "a1c5b7d9a53ac665d6ceefb4729d5d603064d6dc",
            frozenRun = ".verify-build/004-authority/openrouter-qwen9b-160.json",
            bindingMethod = "EXACT_SOURCE_IDENTITY",
            occurrenceSafe = rows.All(item => item.BindingStatus == "EXACT_SOURCE_IDENTITY"),
            referenceAuthority = "MODEL_ASSISTED_SILVER structural occurrence reference; not independent human gold",
            sourcePopulation = new { expected = 93, documentTitle = 1, chapter = 7, section = 8, article = 77 },
            execution = new
            {
                route = run.RootElement.GetProperty("deterministicRoute").GetString(),
                provider = "OpenRouter",
                model = run.RootElement.GetProperty("model").GetString(),
                semanticScheduled = run.RootElement.GetProperty("routeAudit").GetProperty("semanticLane").GetProperty("scheduled").GetInt32(),
                semanticCompleted = run.RootElement.GetProperty("routeAudit").GetProperty("semanticLane").GetProperty("completed").GetInt32(),
                semanticTimedOut = run.RootElement.GetProperty("routeAudit").GetProperty("semanticLane").GetProperty("timedOut").GetInt32(),
                spanScheduled = run.RootElement.GetProperty("routeAudit").GetProperty("spanLane").GetProperty("scheduled").GetInt32(),
                spanCompleted = run.RootElement.GetProperty("routeAudit").GetProperty("spanLane").GetProperty("completed").GetInt32(),
                spanTimedOut = run.RootElement.GetProperty("routeAudit").GetProperty("spanLane").GetProperty("timedOut").GetInt32(),
                elapsedMs = run.RootElement.GetProperty("elapsedMs").GetInt64(),
                modelExecutionStatus = "ALL_SCHEDULED_REQUESTS_TIMED_OUT"
            },
            summary = new
            {
                expected = rows.Length,
                exactSourceBindings = rows.Count(item => item.RawSourceStatus == "RAW_SOURCE_LINE_PRESENT"),
                unresolvedSourceBindings = rows.Count(item => item.RawSourceStatus == "SOURCE_BINDING_UNRESOLVED"),
                candidateAvailableExact = rows.Count(item => item.CandidateStatus == "CANDIDATE_AVAILABLE"),
                candidateGenerationLossProven = rows.Count(item => item.FirstLossOwner == "CANDIDATE_GENERATION_LOSS"),
                candidateBindingUnresolved = rows.Count(item => item.CandidateStatus == "CANDIDATE_BINDING_UNRESOLVED"),
                selectedExact = v3Exact.Count,
                candidateSelectionLossProven = rows.Count(item => item.FirstLossOwner == "CANDIDATE_SELECTION_LOSS"),
                selectionAmbiguous = rows.Count(item => item.SelectedStatus == "SELECTION_AMBIGUOUS"),
                modelExecutionTimeout = rows.Count(item => item.FirstLossOwner == "MODEL_EXECUTION_TIMEOUT"),
                sourceFactLossProven = rows.Count(item => item.FirstLossOwner == "SOURCE_FACT_LOSS"),
                duplicateBindings = duplicateCandidateBindings,
                ambiguousBindings = rows.Count(item => item.BindingStatus == "AMBIGUOUS_BINDING"),
                v2Exact = v2Exact.Count,
                v3ReproducedV2Exact = v2Exact.Intersect(v3Exact).Count(),
                v2V3Mismatch = v2Exact.Except(v3Exact).Concat(v3Exact.Except(v2Exact)).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                primaryFirstLossOwner = rows.Any(item => item.FirstLossOwner == "MODEL_EXECUTION_TIMEOUT") ? "MODEL_EXECUTION_TIMEOUT" : "UNRESOLVED_TRACE",
                primaryStatus = rows.Any(item => item.FirstLossOwner == "UNRESOLVED_TRACE") ? "UNRESOLVED" : "PROVEN"
            },
            collisionChecks = new { duplicateCandidateBindings, duplicateSourceLineBindings, ambiguousContainment = rows.Count(item => item.BindingStatus == "AMBIGUOUS_BINDING"), article6Article7Distinct = DistinctArticlePair(rows, "004/article/6", "004/article/7") },
            bySemanticClass = classes,
            rows
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var markdown = Path.Combine(root, "docs", "accuracy", "004-authority-first-loss-v3.md");
        Directory.CreateDirectory(Path.GetDirectoryName(markdown)!);
        File.WriteAllText(markdown, $"# Document 004 occurrence-safe first-loss trace V3\n\nBinding: `EXACT_SOURCE_IDENTITY`; occurrenceSafe: `{report.occurrenceSafe}`.\n\nExact source bindings: {report.summary.exactSourceBindings}/93. Unresolved source bindings: {report.summary.unresolvedSourceBindings}.\n\nCandidate available exact: {report.summary.candidateAvailableExact}; candidate generation loss proven: {report.summary.candidateGenerationLossProven}; candidate binding unresolved: {report.summary.candidateBindingUnresolved}.\n\nSelected exact: {report.summary.selectedExact}; candidate selection loss proven: {report.summary.candidateSelectionLossProven}; selection ambiguous: {report.summary.selectionAmbiguous}.\n\nModel execution timeout: {report.summary.modelExecutionTimeout}. Frozen run: semantic 160 scheduled, 0 completed, 160 timed out; span 160 scheduled, 0 completed, 160 timed out.\n\nV2 exact: {report.summary.v2Exact}; V3 reproduced: {report.summary.v3ReproducedV2Exact}; mismatch: {report.summary.v2V3Mismatch.Length}.\n\nPrimary first-loss owner: `{report.summary.primaryFirstLossOwner}`; status: `{report.summary.primaryStatus}`.\n\nProvider calls this V3 task: 0. Production code changed: false. Remediation performed: false.\n");
    }

    private static Row BuildRow(JsonElement item, PdfCandidateBoundaryLineage trace, IReadOnlyList<SelectedIdentity> selected)
    {
        var required = item.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var final = trace.Stages.LastOrDefault();
        var candidates = final is null ? [] : final.CandidateLineIds.Where(pair => Contains(pair.Value, required)).Select(pair => pair.Key).ToArray();
        var selectedIds = selected.Where(identity => Contains(identity.LineIds, required)).Select(identity => identity.Id).ToArray();
        var raw = trace.Stages.FirstOrDefault(stage => stage.Component == "PdfSourceFacts");
        var rawPresent = raw is not null && Contains(raw.InputLineIds, required);
        var ambiguous = candidates.Length > 1;
        var candidateStatus = !rawPresent ? "CANDIDATE_BINDING_UNRESOLVED" : candidates.Length == 0 ? "CANDIDATE_BINDING_UNRESOLVED" : "CANDIDATE_AVAILABLE";
        var selectedStatus = candidates.Length == 0 ? "SELECTION_BINDING_UNRESOLVED" : selectedIds.Length > 1 ? "SELECTION_AMBIGUOUS" : selectedIds.Length == 1 ? "SELECTED_EXACT" : "NOT_SELECTED_EXACT";
        var owner = !rawPresent ? "UNRESOLVED_TRACE" : candidates.Length == 0 ? "CANDIDATE_GENERATION_LOSS" : selectedIds.Length == 0 ? "CANDIDATE_SELECTION_LOSS" : "MODEL_EXECUTION_TIMEOUT";
        var lossStage = trace.Stages.FirstOrDefault(stage => stage.Component != "PdfSourceFacts" && !stage.CandidateLineIds.Values.Any(lines => Contains(lines, required)));
        var component = owner switch
        {
            "MODEL_EXECUTION_TIMEOUT" => "SemanticLane",
            "CANDIDATE_SELECTION_LOSS" => "FrozenSelectedSourceIdentitiesJoin",
            "UNRESOLVED_TRACE" => "SourceIdentityBinding",
            _ => lossStage?.Component ?? "NONE",
        };
        var operation = owner switch
        {
            "MODEL_EXECUTION_TIMEOUT" => "ANALYZE",
            "CANDIDATE_SELECTION_LOSS" => "CONTAINMENT_JOIN",
            "UNRESOLVED_TRACE" => "EXACT_LINE_ID_LOOKUP",
            _ => lossStage?.Operation ?? "NONE",
        };
        var reason = owner switch
        {
            "MODEL_EXECUTION_TIMEOUT" => "semantic lane scheduled the selected occurrence but completed zero requests",
            "CANDIDATE_SELECTION_LOSS" => "final candidate lineage exists but no frozen selected identity contains all required source lines",
            "UNRESOLVED_TRACE" => "identity binding is not sufficient to establish an earlier loss",
            _ => lossStage?.Reason ?? "all stages cover occurrence",
        };
        return new Row(item.GetProperty("goldStableId").GetString()!, ClassOf(item), required, item.GetProperty("sourceSpan"), rawPresent ? "RAW_SOURCE_LINE_PRESENT" : "SOURCE_BINDING_UNRESOLVED", StageStatus(trace, "PdfSemanticBlockGrouper.Build", required), candidateStatus, candidates, selectedStatus, selectedIds, owner, component, operation, reason, ambiguous ? "AMBIGUOUS_BINDING" : "EXACT_SOURCE_IDENTITY", trace.Stages.Select(Stage).ToArray());
    }

    private static string StageStatus(PdfCandidateBoundaryLineage trace, string component, IReadOnlyList<string> required) =>
        trace.Stages.FirstOrDefault(stage => stage.Component == component) is { } stage && stage.CandidateLineIds.Values.Any(lines => Contains(lines, required)) ? "GROUPING_SURVIVED" : "GROUPING_ABSORBED";
    private static object Stage(PdfCandidateBoundaryLineageStage stage) => new { stage.Component, stage.Operation, stage.InputLineIds, stage.CandidateLineIds, stage.Reason };
    private static bool Contains(IReadOnlyList<string> haystack, IReadOnlyList<string> required) => required.All(haystack.Contains);
    private static string ClassOf(JsonElement item) => item.GetProperty("goldStableId").GetString()! switch { var x when x.Contains("/chapter/") => "CHAPTER", var x when x.Contains("/section/") => "SECTION", var x when x.Contains("/article/") => "ARTICLE", _ => "DOCUMENT_TITLE" };
    private static bool DistinctArticlePair(IEnumerable<Row> rows, string first, string second) => rows.Single(x => x.OccurrenceId == first).CandidateIds.Intersect(rows.Single(x => x.OccurrenceId == second).CandidateIds, StringComparer.Ordinal).Any() == false;
    private sealed record SelectedIdentity(string Id, IReadOnlyList<string> LineIds);
    private sealed record Row(string OccurrenceId, string SemanticClass, IReadOnlyList<string> SourceLineIds, JsonElement SourceSpan, string RawSourceStatus, string GroupingStatus, string CandidateStatus, IReadOnlyList<string> CandidateIds, string SelectedStatus, IReadOnlyList<string> SelectedCandidateIds, string FirstLossOwner, string FirstLossComponent, string FirstLossOperation, string FirstLossReason, string BindingStatus, IReadOnlyList<object> Stages);
}
