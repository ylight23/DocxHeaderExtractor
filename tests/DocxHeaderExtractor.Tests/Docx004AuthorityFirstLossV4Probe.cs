using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>V4 diagnostic reconstruction from the frozen run; it never calls a provider.</summary>
public sealed class Docx004AuthorityFirstLossV4Probe
{
    [Fact]
    public void WritePreSpanRoleTrace()
    {
        var output = Environment.GetEnvironmentVariable("DOCX004_AUTHORITY_FIRST_LOSS_V4");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy",
            "004_Luat_Dau_tu_61-2020-QH14_EN.docx");
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", "004-n3.2-silver-model-assisted.v1.json");
        var runPath = Path.Combine(root, ".verify-build", "004-authority", "openrouter-qwen9b-160.json");
        var v2Path = Path.Combine(root, "eval", "accuracy", "004-authority-first-loss.v2.json");
        var v3Path = Path.Combine(root, "eval", "accuracy", "004-authority-first-loss.v3.json");
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        using var run = JsonDocument.Parse(File.ReadAllText(runPath));
        using var v2 = JsonDocument.Parse(File.ReadAllText(v2Path));
        using var v3 = JsonDocument.Parse(File.ReadAllText(v3Path));

        var occurrences = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray().ToArray();
        var requests = occurrences.ToDictionary(x => x.GetProperty("goldStableId").GetString()!,
            x => (IReadOnlyList<string>)x.GetProperty("sourceLineIds").EnumerateArray().Select(y => y.GetString()!).ToArray(), StringComparer.Ordinal);
        var lineage = PdfLayoutEvidenceOutline.TraceCandidateBoundaryLineage(docx, requests)
            .ToDictionary(x => x.OccurrenceId, StringComparer.Ordinal);
        var selected = run.RootElement.GetProperty("routeAudit").GetProperty("selectedSourceIdentities").EnumerateArray()
            .Select(x => new Selected(x.GetProperty("candidateIdDiagnostic").GetString()!, Lines(x, "sourceLineIds"))).ToArray();
        var v3Rows = v3.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(x => x.GetProperty("OccurrenceId").GetString()!, StringComparer.Ordinal);
        var v2Exact = v2.RootElement.GetProperty("rows").EnumerateArray().Where(x => x.GetProperty("selectedStatus").GetString() == "SELECTED")
            .Select(x => x.GetProperty("occurrenceId").GetString()!).ToHashSet(StringComparer.Ordinal);

        var contracts = run.RootElement.GetProperty("routeAudit").GetProperty("modelInputContracts").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var responses = run.RootElement.GetProperty("routeAudit").GetProperty("rawAnalystResponses").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var roleRecovery = RecoverRoleDecisions(contracts, responses);
        var roleDecisions = roleRecovery.Decisions;
        var rows = occurrences.Select(x => BuildRow(x, lineage[x.GetProperty("goldStableId").GetString()!], v3Rows, selected, roleDecisions)).ToArray();
        var v3Exact = v3Rows.Values.Where(x => x.GetProperty("SelectedStatus").GetString() == "SELECTED_EXACT").Select(x => x.GetProperty("OccurrenceId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var duplicateCarriers = rows.SelectMany(x => x.CandidateIds).GroupBy(x => x, StringComparer.Ordinal).Where(x => x.Count() > 1)
            .Select(x => new
            {
                carrierId = x.Key,
                occurrenceIds = rows.Where(r => r.CandidateIds.Contains(x.Key, StringComparer.Ordinal)).Select(r => r.OccurrenceId).ToArray(),
                carrierSourceLineIds = rows.Where(r => r.CandidateIds.Contains(x.Key, StringComparer.Ordinal)).SelectMany(r => r.SourceLineIds).Distinct(StringComparer.Ordinal).ToArray(),
                roleDecisionCount = roleDecisions.ContainsKey(x.Key) ? 1 : 0,
                oneRoleDecision = roleDecisions.ContainsKey(x.Key),
                oneSpanCanRepresentBoth = rows.Where(r => r.CandidateIds.Contains(x.Key, StringComparer.Ordinal)).SelectMany(r => r.SourceLineIds).Distinct(StringComparer.Ordinal).Count() == rows.Where(r => r.CandidateIds.Contains(x.Key, StringComparer.Ordinal)).SelectMany(r => r.SourceLineIds).Count(),
                classification = "MULTI_OCCURRENCE_CARRIER"
            }).ToArray();
        var audit = run.RootElement.GetProperty("routeAudit");
        var report = new
        {
            schemaVersion = 4,
            artifactKind = "004_authority_first_loss_trace",
            canonicalPipeline = "AuthorityExtractionPipeline",
            baseRevision = "a1c5b7d9a53ac665d6ceefb4729d5d603064d6dc",
            frozenRun = ".verify-build/004-authority/openrouter-qwen9b-160.json",
            preservedArtifacts = new[] { "eval/accuracy/004-authority-first-loss.v1.json", "eval/accuracy/004-authority-first-loss.v2.json", "eval/accuracy/004-authority-first-loss.v3.json" },
            identity = new { occurrenceIdentitySafe = true, carrierBindingUnique = false, ambiguousCarrierOccurrences = rows.Count(x => x.BindingStatus == "AMBIGUOUS_BINDING") },
            execution = new
            {
                deterministicRoute = run.RootElement.GetProperty("deterministicRoute").GetString(),
                provider = "OpenRouter",
                model = run.RootElement.GetProperty("model").GetString(),
                semanticLaneStatus = audit.GetProperty("semanticLane").GetProperty("status").GetString(),
                semanticLaneScheduled = audit.GetProperty("semanticLane").GetProperty("scheduled").GetInt32(),
                semanticLaneCompleted = audit.GetProperty("semanticLane").GetProperty("completed").GetInt32(),
                semanticLaneTimedOutCounter = audit.GetProperty("semanticLane").GetProperty("timedOut").GetInt32(),
                spanLaneStatus = audit.GetProperty("spanLane").GetProperty("status").GetString(),
                spanLaneScheduled = audit.GetProperty("spanLane").GetProperty("scheduled").GetInt32(),
                spanLaneCompleted = audit.GetProperty("spanLane").GetProperty("completed").GetInt32(),
                spanLaneTimedOutCounter = audit.GetProperty("spanLane").GetProperty("timedOut").GetInt32(),
                semanticHttpTimeoutCount = "NOT_OBSERVABLE",
                spanHttpRequestCount = "NOT_OBSERVABLE",
                semanticTimedOutCounterInterpretation = "not an HTTP request count; final counters can be contaminated by span decisions folded into blockAnalysis",
                uniqueRoleContracts = roleRecovery.UniqueContracts,
                uniqueRoleResponses = roleRecovery.UniqueResponses,
                roleContractsWithResponse = roleRecovery.ContractsWithResponse,
                roleIdsRequestedUnique = roleRecovery.RequestedIds.Count,
                roleIdsDecidedUnique = roleRecovery.DecidedIds.Count,
                roleIdsMissing = roleRecovery.RequestedIds.Except(roleRecovery.DecidedIds, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                roleIdsDuplicated = roleRecovery.DuplicateIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                roleCandidateDecisionCoverage = $"{roleRecovery.DecidedIds.Count}/{roleRecovery.RequestedIds.Count}",
                rawContractCount = contracts.Length,
                rawResponseCount = responses.Length
            },
            summary = new
            {
                expected = rows.Length,
                sourcePresent = rows.Count(x => x.RawSourceStatus == "RAW_SOURCE_LINE_PRESENT"),
                sourceMissingProven = 0,
                sourceUnresolved = rows.Count(x => x.RawSourceStatus != "RAW_SOURCE_LINE_PRESENT"),
                candidateAvailableExact = rows.Count(x => x.CandidateStatus == "CANDIDATE_AVAILABLE"),
                candidateGenerationLoss = rows.Count(x => x.CandidateStatus == "CANDIDATE_NOT_AVAILABLE_PROVEN"),
                candidateBindingUnresolved = rows.Count(x => x.CandidateStatus == "CANDIDATE_BINDING_UNRESOLVED"),
                candidateSelectionLoss = rows.Count(x => x.FirstLossOwner == "CANDIDATE_SELECTION_LOSS"),
                selectedOccurrences = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT"),
                selectionAmbiguous = rows.Count(x => x.SelectedStatus == "SELECTION_AMBIGUOUS"),
                roleHeadingProposal = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_HEADING_PROPOSAL"),
                roleNonHeading = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_NON_HEADING"),
                roleUncertain = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_UNCERTAIN"),
                roleTraceUnresolved = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_EVIDENCE_UNRECOVERABLE"),
                roleOccurrenceBindingUnresolved = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_BINDING_AMBIGUOUS"),
                spanExecutionTimeout = rows.Count(x => x.FirstLossOwner == "SPAN_EXECUTION_TIMEOUT"),
                postSelectionTraceUnresolved = rows.Count(x => x.FirstLossOwner == "POST_SELECTION_MODEL_TRACE_UNRESOLVED"),
                modelExecutionTimeout = 0,
                spanExecutionTimeoutOccurrenceIds = rows.Where(x => x.FirstLossOwner == "SPAN_EXECUTION_TIMEOUT").Select(x => x.OccurrenceId).ToArray(),
                roleOccurrenceBindingUnresolvedOccurrenceIds = rows.Where(x => x.FirstLossOwner == "ROLE_OCCURRENCE_BINDING_UNRESOLVED").Select(x => x.OccurrenceId).ToArray(),
                v2Exact = v2Exact.Count,
                reproducedV2Exact = v2Exact.Intersect(v3Exact).Count(),
                v2V3Mismatch = v2Exact.Except(v3Exact).Concat(v3Exact.Except(v2Exact)).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                multiOccurrenceCarriers = duplicateCarriers.Length,
                firstLossTotal = rows.Length,
                firstLossCounts = rows.GroupBy(x => x.FirstLossOwner, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
                primaryFirstLossOwner = "ROLE_OCCURRENCE_BINDING_UNRESOLVED",
                primaryStatus = "UNRESOLVED"
            },
            duplicateCarriers,
            rows,
            providerCallsThisTask = 0,
            productionCodeChanged = false,
            remediationPerformed = false
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var markdown = Path.Combine(root, "docs", "accuracy", "004-authority-first-loss-v4.md");
        Directory.CreateDirectory(Path.GetDirectoryName(markdown)!);
        File.WriteAllText(markdown, $"# Document 004 first-loss trace V4\n\nV4 reconstructs pre-span role evidence from the frozen run and never uses final `blockDecisions`.\n\nSource present: {report.summary.sourcePresent}/93. Candidate generation loss: {report.summary.candidateGenerationLoss}. Candidate selection loss: {report.summary.candidateSelectionLoss}. Selected: {report.summary.selectedOccurrences}.\n\nRole heading proposal: {report.summary.roleHeadingProposal}; non-heading: {report.summary.roleNonHeading}; uncertain: {report.summary.roleUncertain}; evidence unrecoverable: {report.summary.roleTraceUnresolved}. Span execution timeout is counted only after a proven heading-like role: {report.summary.spanExecutionTimeout}. Post-selection trace unresolved: {report.summary.postSelectionTraceUnresolved}.\n\nSemantic lane: `{report.execution.semanticLaneStatus}` (scheduled {report.execution.semanticLaneScheduled}, completed {report.execution.semanticLaneCompleted}, timed-out counter {report.execution.semanticLaneTimedOutCounter}); span lane: `{report.execution.spanLaneStatus}` (scheduled {report.execution.spanLaneScheduled}, completed {report.execution.spanLaneCompleted}, timed-out counter {report.execution.spanLaneTimedOutCounter}). HTTP timeout/request counts are not observable from these counters.\n\nUnique role contracts: {report.execution.uniqueRoleContracts}; unique role responses: {report.execution.uniqueRoleResponses}. V2 exact: {report.summary.v2Exact}; reproduced: {report.summary.reproducedV2Exact}; mismatch: {report.summary.v2V3Mismatch.Length}.\n\nCarrier binding is not unique; ambiguous occurrences: {report.identity.ambiguousCarrierOccurrences}. Provider calls: 0. Production changed: false. Remediation: false.\n");
        Assert.Equal(93, rows.Length);
        Assert.Equal(v2Exact.Count, report.summary.reproducedV2Exact);
        Assert.Equal(55, report.summary.selectedOccurrences);
        Assert.Equal(55, report.summary.roleHeadingProposal + report.summary.roleNonHeading + report.summary.roleUncertain + report.summary.roleTraceUnresolved + report.summary.roleOccurrenceBindingUnresolved);
        Assert.Equal(93, report.summary.firstLossCounts.Values.Sum());
        Assert.Equal(160, roleRecovery.RequestedIds.Count);
        Assert.Equal(160, roleRecovery.DecidedIds.Count);
    }

    private static RoleRecovery RecoverRoleDecisions(string[] contracts, string[] responses)
    {
        var uniqueContracts = contracts.Select(Hash).Distinct(StringComparer.Ordinal).Count();
        var result = new Dictionary<string, RoleDecision>(StringComparer.Ordinal);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        var roleContracts = contracts.Select(ParseIds).Where(x => x.Length > 0).GroupBy(x => string.Join("\u001f", x.OrderBy(y => y, StringComparer.Ordinal)), StringComparer.Ordinal).Select(x => x.First()).ToArray();
        var uniqueResponseObjects = responses.Select((text, index) => (text, index, hash: Hash(text))).GroupBy(x => x.hash, StringComparer.Ordinal).Select(x => x.First()).ToArray();
        var roleResponses = uniqueResponseObjects.Select(x => ParseRoleResponse(x.text)).Where(x => x is not null).Select(x => x!).ToArray();
        var contractsWithResponse = 0;
        foreach (var contractIds in roleContracts)
        {
            foreach (var id in contractIds) requested.Add(id);
            var match = roleResponses.FirstOrDefault(x => x.Decisions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(contractIds));
            if (match is null) continue;
            contractsWithResponse++;
            foreach (var pair in match.Decisions)
            {
                if (result.ContainsKey(pair.Key)) duplicateIds.Add(pair.Key);
                else result[pair.Key] = pair.Value;
            }
        }
        return new RoleRecovery(result, uniqueContracts, roleResponses.Length, contractsWithResponse, requested, result.Keys.ToHashSet(StringComparer.Ordinal), duplicateIds);
    }

    private static string[] ParseIds(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("blocks").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static RoleResponse? ParseRoleResponse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("blocks", out var blocks)) return null;
        var decisions = new Dictionary<string, RoleDecision>(StringComparer.Ordinal);
        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("role", out var role) || !block.TryGetProperty("confidence", out var confidence)) return null;
            decisions[block.GetProperty("id").GetString()!] = new RoleDecision(role.GetString()!, confidence.GetDouble());
        }
        return new RoleResponse(decisions);
    }

    private static Row BuildRow(JsonElement item, PdfCandidateBoundaryLineage trace, IReadOnlyDictionary<string, JsonElement> v3Rows, IReadOnlyList<Selected> selected, IReadOnlyDictionary<string, RoleDecision> roles)
    {
        var required = Lines(item, "sourceLineIds");
        var final = trace.Stages.LastOrDefault();
        var candidates = final?.CandidateLineIds.Where(x => Contains(x.Value, required)).Select(x => x.Key).ToArray() ?? [];
        var selectedIds = selected.Where(x => Contains(x.LineIds, required)).Select(x => x.Id).ToArray();
        var raw = trace.Stages.FirstOrDefault(x => x.Component == "PdfSourceFacts");
        var rawPresent = raw is not null && Contains(raw.InputLineIds, required);
        var candidateStatus = !rawPresent ? "CANDIDATE_BINDING_UNRESOLVED" : candidates.Length == 0 ? "CANDIDATE_NOT_AVAILABLE_PROVEN" : "CANDIDATE_AVAILABLE";
        var selectedStatus = candidates.Length == 0 ? "SELECTION_BINDING_UNRESOLVED" : selectedIds.Length > 1 ? "SELECTION_AMBIGUOUS" : selectedIds.Length == 1 ? "SELECTED_EXACT" : "NOT_SELECTED_EXACT";
        var selectedCarrier = selectedIds.Select(id => roles.TryGetValue(id, out var value) ? value : (RoleDecision?)null).Where(x => x is not null).Select(x => x!.Value).ToArray();
        var roleStatus = selectedStatus != "SELECTED_EXACT" ? "ROLE_EVIDENCE_UNRECOVERABLE" : candidates.Length > 1 ? "ROLE_BINDING_AMBIGUOUS" : selectedCarrier.Length == 0 ? "ROLE_EVIDENCE_UNRECOVERABLE" : IsHeading(selectedCarrier[0].Role) ? "ROLE_HEADING_PROPOSAL" : IsNonHeading(selectedCarrier[0].Role) ? "ROLE_NON_HEADING" : "ROLE_UNCERTAIN";
        var owner = selectedStatus != "SELECTED_EXACT" ? selectedStatus == "NOT_SELECTED_EXACT" ? "CANDIDATE_SELECTION_LOSS" : candidateStatus == "CANDIDATE_NOT_AVAILABLE_PROVEN" ? "CANDIDATE_GENERATION_LOSS" : "UNRESOLVED_TRACE" : roleStatus == "ROLE_HEADING_PROPOSAL" ? "SPAN_EXECUTION_TIMEOUT" : roleStatus == "ROLE_NON_HEADING" ? "ROLE_REJECTION" : roleStatus == "ROLE_BINDING_AMBIGUOUS" ? "ROLE_OCCURRENCE_BINDING_UNRESOLVED" : "POST_SELECTION_TRACE_UNRESOLVED";
        var firstLoss = trace.FirstLossComponent;
        var firstOperation = trace.FirstLossOperation;
        var firstReason = trace.FirstLossReason;
        if (owner == "CANDIDATE_SELECTION_LOSS") { firstLoss = "FrozenSelectedSourceIdentitiesJoin"; firstOperation = "CONTAINMENT_JOIN"; firstReason = "final candidate exists but is absent from frozen selected identities"; }
        if (owner == "SPAN_EXECUTION_TIMEOUT") { firstLoss = "SpanLane"; firstOperation = "ANALYZE"; firstReason = "pre-span role is heading-like; span completion is zero and span lane timed out"; }
        return new Row(item.GetProperty("goldStableId").GetString()!, ClassOf(item), required, item.GetProperty("sourceSpan"), rawPresent ? "RAW_SOURCE_LINE_PRESENT" : "SOURCE_BINDING_UNRESOLVED", candidateStatus, candidates, selectedStatus, selectedIds, roleStatus, owner, firstLoss, firstOperation, firstReason, candidates.Length > 1 ? "AMBIGUOUS_BINDING" : "EXACT_SOURCE_IDENTITY", trace.Stages.Select(x => new Stage(x.Component, x.Operation, x.InputLineIds, x.CandidateLineIds, x.Reason)).ToArray());
    }

    private static bool IsHeading(string role) => role.Contains("heading", StringComparison.OrdinalIgnoreCase) || role.StartsWith("legal_", StringComparison.OrdinalIgnoreCase) && !role.Contains("clause", StringComparison.OrdinalIgnoreCase) && !role.Contains("point", StringComparison.OrdinalIgnoreCase);
    private static bool IsNonHeading(string role) => role.Contains("body", StringComparison.OrdinalIgnoreCase) || role.Contains("clause", StringComparison.OrdinalIgnoreCase) || role.Contains("point", StringComparison.OrdinalIgnoreCase);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string[] Lines(JsonElement item, string property) => item.GetProperty(property).EnumerateArray().Select(x => x.GetString()!).ToArray();
    private static bool Contains(IReadOnlyList<string> values, IReadOnlyList<string> required) => required.All(values.Contains);
    private static string ClassOf(JsonElement item) => item.GetProperty("goldStableId").GetString()! switch { var x when x.Contains("/chapter/") => "CHAPTER", var x when x.Contains("/section/") => "SECTION", var x when x.Contains("/article/") => "ARTICLE", _ => "DOCUMENT_TITLE" };
    private sealed record Selected(string Id, IReadOnlyList<string> LineIds);
    private sealed record RoleResponse(IReadOnlyDictionary<string, RoleDecision> Decisions);
    private sealed record RoleRecovery(Dictionary<string, RoleDecision> Decisions, int UniqueContracts, int UniqueResponses, int ContractsWithResponse, HashSet<string> RequestedIds, HashSet<string> DecidedIds, HashSet<string> DuplicateIds);
    private readonly record struct RoleDecision(string Role, double Confidence);
    private sealed record Stage(string Component, string Operation, IReadOnlyList<string> InputLineIds, IReadOnlyDictionary<string, IReadOnlyList<string>> CandidateLineIds, string Reason);
    private sealed record Row(string OccurrenceId, string SemanticClass, IReadOnlyList<string> SourceLineIds, JsonElement SourceSpan, string RawSourceStatus, string CandidateStatus, IReadOnlyList<string> CandidateIds, string SelectedStatus, IReadOnlyList<string> SelectedCandidateIds, string RoleStatus, string FirstLossOwner, string FirstLossComponent, string FirstLossOperation, string FirstLossReason, string BindingStatus, IReadOnlyList<Stage> Stages);
}
