using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>V5 diagnostic correction: frozen reference populations and selected-carrier role binding.</summary>
public sealed class Docx004AuthorityFirstLossV5Probe
{
    [Fact]
    public void WriteCorrectedReferenceAndCarrierTrace()
    {
        var output = Environment.GetEnvironmentVariable("DOCX004_AUTHORITY_FIRST_LOSS_V5");
        if (string.IsNullOrWhiteSpace(output)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", "004-n3.2-silver-model-assisted.v1.json");
        var v4Path = Path.Combine(root, "eval", "accuracy", "004-authority-first-loss.v4.json");
        var runPath = Path.Combine(root, ".verify-build", "004-authority", "openrouter-qwen9b-160.json");
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        using var v4 = JsonDocument.Parse(File.ReadAllText(v4Path));
        using var run = JsonDocument.Parse(File.ReadAllText(runPath));
        var silverRows = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray().ToArray();
        var oldRows = v4.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(x => x.GetProperty("OccurrenceId").GetString()!, StringComparer.Ordinal);
        var roles = RecoverRoles(run.RootElement.GetProperty("routeAudit").GetProperty("modelInputContracts").EnumerateArray().Select(x => x.GetString()!).ToArray(), run.RootElement.GetProperty("routeAudit").GetProperty("rawAnalystResponses").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var rows = silverRows.Select(x => CorrectRow(x, oldRows[x.GetProperty("goldStableId").GetString()!], roles)).ToArray();
        var silverCounts = new[] { "DOCUMENT_TITLE", "CHAPTER", "SECTION", "ARTICLE", "APPENDIX" }.ToDictionary(x => x, x => rows.Count(y => y.SemanticClass == x), StringComparer.Ordinal);
        var carriers = rows.Where(x => x.SelectedStatus == "SELECTED_EXACT").SelectMany(x => x.SelectedCandidateIds).GroupBy(x => x, StringComparer.Ordinal).ToArray();
        var firstLossCounts = rows.GroupBy(x => x.FirstLossOwner, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var report = new
        {
            schemaVersion = 5,
            artifactKind = "004_authority_first_loss_trace",
            canonicalPipeline = "AuthorityExtractionPipeline",
            preservedArtifacts = new[] { "eval/accuracy/004-authority-first-loss.v1.json", "eval/accuracy/004-authority-first-loss.v2.json", "eval/accuracy/004-authority-first-loss.v3.json", "eval/accuracy/004-authority-first-loss.v4.json" },
            referencePopulations = new
            {
                n3Silver = new { DOCUMENT_TITLE = silverCounts["DOCUMENT_TITLE"], CHAPTER = silverCounts["CHAPTER"], SECTION = silverCounts["SECTION"], ARTICLE = silverCounts["ARTICLE"], APPENDIX = silverCounts["APPENDIX"], total = rows.Length },
                targetOntology = new { status = "BLOCKED", reason = "exact source identity for a source-derived LAW ON INVESTMENT document-title occurrence was not established; Appendix IV is not substituted", intended = new { DOCUMENT_TITLE = 1, CHAPTER = 7, SECTION = 8, ARTICLE = 77, APPENDIX = 0, total = 93 }, metrics = "BLOCKED" }
            },
            execution = new
            {
                deterministicRoute = run.RootElement.GetProperty("deterministicRoute").GetString(),
                semanticLaneStatus = run.RootElement.GetProperty("routeAudit").GetProperty("semanticLane").GetProperty("status").GetString(),
                spanLaneStatus = run.RootElement.GetProperty("routeAudit").GetProperty("spanLane").GetProperty("status").GetString(),
                spanLaneCompletedCounter = run.RootElement.GetProperty("routeAudit").GetProperty("spanLane").GetProperty("completed").GetInt32(),
                spanHttpRequestCount = "NOT_OBSERVABLE",
                spanPerOccurrenceTimeout = "NOT_OBSERVABLE",
                roleCandidateDecisionCoverage = $"{roles.Decided.Count}/{roles.Requested.Count}",
                uniqueRoleContracts = roles.UniqueContracts,
                uniqueRoleResponses = roles.UniqueResponses,
                roleContractsWithResponse = roles.ContractsWithResponse,
                roleIdsRequestedUnique = roles.Requested.Count,
                roleIdsDecidedUnique = roles.Decided.Count,
                roleIdsMissing = roles.Requested.Except(roles.Decided, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                roleIdsDuplicated = roles.Duplicated.OrderBy(x => x, StringComparer.Ordinal).ToArray()
            },
            summary = new
            {
                n3SilverTotal = rows.Length,
                targetTotal = "BLOCKED",
                selectedOccurrencesSilver = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT"),
                selectedCarrierUnique = carriers.Length,
                selectedCarrierSingleOccurrence = carriers.Count(x => x.Count() == 1),
                selectedCarrierMultiOccurrence = carriers.Count(x => x.Count() > 1),
                selectedCarrierMultiOccurrenceIds = carriers.Where(x => x.Count() > 1).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                roleHeadingProposal = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_HEADING_PROPOSAL"),
                roleNonHeading = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_NON_HEADING"),
                roleUncertain = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_UNCERTAIN"),
                roleOccurrenceBindingUnresolved = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_OCCURRENCE_BINDING_UNRESOLVED"),
                roleEvidenceUnrecoverable = rows.Count(x => x.SelectedStatus == "SELECTED_EXACT" && x.RoleStatus == "ROLE_EVIDENCE_UNRECOVERABLE"),
                spanLaneNoResolution = rows.Count(x => x.FirstLossOwner == "SPAN_LANE_NO_RESOLUTION"),
                spanLaneNoResolutionOccurrenceIds = rows.Where(x => x.FirstLossOwner == "SPAN_LANE_NO_RESOLUTION").Select(x => x.OccurrenceId).ToArray(),
                silverFirstLossCounts = firstLossCounts,
                silverFirstLossTotal = firstLossCounts.Values.Sum(),
                targetFirstLossTotal = "BLOCKED",
                primarySilverOwner = firstLossCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key,
                primaryTargetOwner = "BLOCKED"
            },
            rows,
            providerCallsThisTask = 0,
            productionCodeChanged = false,
            remediationPerformed = false
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var markdown = Path.Combine(root, "docs", "accuracy", "004-authority-first-loss-v5.md");
        Directory.CreateDirectory(Path.GetDirectoryName(markdown)!);
        File.WriteAllText(markdown, $"# Document 004 first-loss trace V5\n\nN3 silver population: title {silverCounts["DOCUMENT_TITLE"]}, chapters {silverCounts["CHAPTER"]}, sections {silverCounts["SECTION"]}, articles {silverCounts["ARTICLE"]}, appendix {silverCounts["APPENDIX"]}, total {rows.Length}. Target ontology is `BLOCKED`: no exact source identity for `LAW ON INVESTMENT` was established, so Appendix IV is not substituted.\n\nSelected silver occurrences: {report.summary.selectedOccurrencesSilver}; selected carriers unique: {report.summary.selectedCarrierUnique}; carriers with multiple logical occurrences: {report.summary.selectedCarrierMultiOccurrence}. Role accounting: heading {report.summary.roleHeadingProposal}, non-heading {report.summary.roleNonHeading}, uncertain {report.summary.roleUncertain}, occurrence binding unresolved {report.summary.roleOccurrenceBindingUnresolved}, evidence unrecoverable {report.summary.roleEvidenceUnrecoverable}.\n\nRole candidate coverage: {report.execution.roleCandidateDecisionCoverage}; unique role contracts/responses: {report.execution.uniqueRoleContracts}/{report.execution.uniqueRoleResponses}. Span lane: `{report.execution.spanLaneStatus}`, completed counter {report.execution.spanLaneCompletedCounter}; per-request timeout is not observable. Silver first-loss total: {report.summary.silverFirstLossTotal}/93. Provider calls: 0; production changed: false; remediation: false.\n");
        Assert.Equal(93, rows.Length);
        Assert.Equal(93, report.summary.silverFirstLossTotal);
        Assert.Equal(55, report.summary.selectedOccurrencesSilver);
        Assert.Equal(55, report.summary.roleHeadingProposal + report.summary.roleNonHeading + report.summary.roleUncertain + report.summary.roleOccurrenceBindingUnresolved + report.summary.roleEvidenceUnrecoverable);
        Assert.Equal(160, roles.Requested.Count);
        Assert.Equal(160, roles.Decided.Count);
    }

    private static Row CorrectRow(JsonElement source, JsonElement old, RoleRecovery roles)
    {
        var selected = old.GetProperty("SelectedCandidateIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var exact = old.GetProperty("SelectedStatus").GetString() == "SELECTED_EXACT";
        var role = exact && selected.Length == 1 && roles.Decisions.TryGetValue(selected[0], out var decision) ? decision.Role : null;
        var roleStatus = !exact ? "ROLE_EVIDENCE_UNRECOVERABLE" : selected.Length > 1 ? "SELECTED_CARRIER_AMBIGUOUS" : role is null ? "ROLE_EVIDENCE_UNRECOVERABLE" : IsHeading(role) ? "ROLE_HEADING_PROPOSAL" : IsNonHeading(role) ? "ROLE_NON_HEADING" : "ROLE_UNCERTAIN";
        if (roleStatus == "SELECTED_CARRIER_AMBIGUOUS") roleStatus = "ROLE_OCCURRENCE_BINDING_UNRESOLVED";
        var owner = !exact ? old.GetProperty("FirstLossOwner").GetString()! : roleStatus == "ROLE_HEADING_PROPOSAL" ? "SPAN_LANE_NO_RESOLUTION" : roleStatus == "ROLE_NON_HEADING" ? "ROLE_REJECTION" : roleStatus == "ROLE_OCCURRENCE_BINDING_UNRESOLVED" ? "ROLE_OCCURRENCE_BINDING_UNRESOLVED" : "POST_SELECTION_TRACE_UNRESOLVED";
        return new Row(source.GetProperty("goldStableId").GetString()!, ClassOf(source), Lines(source, "sourceLineIds"), source.GetProperty("sourceSpan"), old.GetProperty("CandidateStatus").GetString()!, selected, old.GetProperty("CandidateIds").EnumerateArray().Select(x => x.GetString()!).ToArray(), old.GetProperty("SelectedStatus").GetString()!, roleStatus, owner, old.GetProperty("FirstLossComponent").GetString()!, old.GetProperty("FirstLossOperation").GetString()!, old.GetProperty("FirstLossReason").GetString()!);
    }

    private static RoleRecovery RecoverRoles(string[] contracts, string[] responses)
    {
        var contractSets = contracts.Select(ParseIds).GroupBy(x => string.Join("\u001f", x.OrderBy(y => y, StringComparer.Ordinal)), StringComparer.Ordinal).Select(x => x.First()).ToArray();
        var uniqueResponses = responses.Select(x => (x, h: Hash(x))).GroupBy(x => x.h, StringComparer.Ordinal).Select(x => x.First().x).Select(ParseResponse).Where(x => x is not null).Select(x => x!).ToArray();
        var requested = contractSets.SelectMany(x => x).ToHashSet(StringComparer.Ordinal); var decided = new Dictionary<string, RoleDecision>(StringComparer.Ordinal); var duplicate = new HashSet<string>(StringComparer.Ordinal); var matched = 0;
        foreach (var set in contractSets) { var response = uniqueResponses.FirstOrDefault(x => x.Decisions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(set)); if (response is null) continue; matched++; foreach (var pair in response.Decisions) { if (!decided.TryAdd(pair.Key, pair.Value)) duplicate.Add(pair.Key); } }
        return new RoleRecovery(decided, contractSets.Length, uniqueResponses.Length, matched, requested, decided.Keys.ToHashSet(StringComparer.Ordinal), duplicate);
    }
    private static string[] ParseIds(string text) { using var d = JsonDocument.Parse(text); return d.RootElement.GetProperty("blocks").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).Distinct(StringComparer.Ordinal).ToArray(); }
    private static RoleResponse? ParseResponse(string text) { using var d = JsonDocument.Parse(text); if (!d.RootElement.TryGetProperty("blocks", out var b)) return null; var r = new Dictionary<string, RoleDecision>(StringComparer.Ordinal); foreach (var x in b.EnumerateArray()) { if (!x.TryGetProperty("role", out var role) || !x.TryGetProperty("confidence", out var confidence)) return null; r[x.GetProperty("id").GetString()!] = new RoleDecision(role.GetString()!, confidence.GetDouble()); } return new RoleResponse(r); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string[] Lines(JsonElement x, string p) => x.GetProperty(p).EnumerateArray().Select(y => y.GetString()!).ToArray();
    private static string ClassOf(JsonElement x) => x.GetProperty("goldStableId").GetString()! switch { var id when id.Contains("/chapter/", StringComparison.Ordinal) => "CHAPTER", var id when id.Contains("/section/", StringComparison.Ordinal) => "SECTION", var id when id.Contains("/article/", StringComparison.Ordinal) => "ARTICLE", var id when id.Contains("/appendix/", StringComparison.Ordinal) => "APPENDIX", var id when id.Contains("/document-title/", StringComparison.Ordinal) => "DOCUMENT_TITLE", _ => "UNKNOWN" };
    private static bool IsHeading(string role) => role.Contains("heading", StringComparison.OrdinalIgnoreCase) || role.StartsWith("legal_", StringComparison.OrdinalIgnoreCase) && !role.Contains("clause", StringComparison.OrdinalIgnoreCase) && !role.Contains("point", StringComparison.OrdinalIgnoreCase);
    private static bool IsNonHeading(string role) => role.Contains("body", StringComparison.OrdinalIgnoreCase) || role.Contains("clause", StringComparison.OrdinalIgnoreCase) || role.Contains("point", StringComparison.OrdinalIgnoreCase);
    private sealed record RoleDecision(string Role, double Confidence);
    private sealed record RoleResponse(IReadOnlyDictionary<string, RoleDecision> Decisions);
    private sealed record RoleRecovery(Dictionary<string, RoleDecision> Decisions, int UniqueContracts, int UniqueResponses, int ContractsWithResponse, HashSet<string> Requested, HashSet<string> Decided, HashSet<string> Duplicated);
    private sealed record Row(string OccurrenceId, string SemanticClass, IReadOnlyList<string> SourceLineIds, JsonElement SourceSpan, string CandidateStatus, IReadOnlyList<string> SelectedCandidateIds, IReadOnlyList<string> CandidateIds, string SelectedStatus, string RoleStatus, string FirstLossOwner, string FirstLossComponent, string FirstLossOperation, string FirstLossReason);
}
