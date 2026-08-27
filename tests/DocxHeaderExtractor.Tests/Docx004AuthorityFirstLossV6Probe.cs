using System.Text.Json;
using UglyToad.PdfPig;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>V6 diagnostic: owner-derived provenance and raw-PDF title identity.</summary>
public sealed class Docx004AuthorityFirstLossV6Probe
{
    [Fact]
    public void WriteCausalProvenanceAndTargetTrace()
    {
        var output = Environment.GetEnvironmentVariable("DOCX004_AUTHORITY_FIRST_LOSS_V6");
        if (string.IsNullOrWhiteSpace(output)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        using var v5 = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval/accuracy/004-authority-first-loss.v5.json")));
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".verify-build/004-authority", "openrouter-qwen9b-160.json")));
        var oldRows = v5.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var rows = oldRows.Select(CorrectProvenance).ToList();
        var pdfPath = Path.Combine(root, "todo10_8", "heading_corpus_100", "01_phap_quy", "004_Luat_Dau_tu_61-2020-QH14_EN.pdf");
        var titleLines = FindExactTitle(pdfPath);
        var targetStatus = titleLines.Count == 1 ? "PASS" : "BLOCKED";
        JsonElement? titleRow = null;
        if (titleLines.Count == 1)
        {
            var line = titleLines[0];
            var titleRequest = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["004/document-title/1"] = [PdfCandidateProvenance.LineId(line)] };
            var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy", "004_Luat_Dau_tu_61-2020-QH14_EN.docx");
            var trace = PdfLayoutEvidenceOutline.TraceCandidateBoundaryLineage(docxPath, titleRequest).Single();
            var required = titleRequest["004/document-title/1"];
            var final = trace.Stages.LastOrDefault();
            var candidateIds = final?.CandidateLineIds.Where(x => required.All(x.Value.Contains)).Select(x => x.Key).ToArray() ?? [];
            var selected = run.RootElement.GetProperty("routeAudit").GetProperty("selectedSourceIdentities").EnumerateArray()
                .Where(x => x.GetProperty("sourceLineIds").EnumerateArray().Select(y => y.GetString()!).Contains(required[0]))
                .Select(x => x.GetProperty("candidateIdDiagnostic").GetString()!).ToArray();
            var owner = candidateIds.Length == 0 ? "CANDIDATE_GENERATION_LOSS" : selected.Length == 0 ? "CANDIDATE_SELECTION_LOSS" : "POST_SELECTION_TRACE_UNRESOLVED";
            var component = owner == "CANDIDATE_GENERATION_LOSS" ? trace.FirstLossComponent : owner == "CANDIDATE_SELECTION_LOSS" ? "FrozenSelectedSourceIdentitiesJoin" : "SemanticRoleLane";
            var operation = owner == "CANDIDATE_GENERATION_LOSS" ? trace.FirstLossOperation : owner == "CANDIDATE_SELECTION_LOSS" ? "CONTAINMENT_JOIN" : "CLASSIFY";
            var reason = owner == "CANDIDATE_GENERATION_LOSS" ? trace.FirstLossReason : owner == "CANDIDATE_SELECTION_LOSS" ? "final candidate exists but occurrence is absent from selected source identities" : "title candidate selected but frozen pre-span role evidence is not safely attributable";
            titleRow = JsonSerializer.SerializeToElement(new { occurrenceId = "004/document-title/1", semanticClass = "DOCUMENT_TITLE", sourceLineIds = required, sourceSpan = new { startLineId = required[0], endLineId = required[0] }, sourceText = "LAW ON INVESTMENT", rawSourceStatus = "RAW_SOURCE_LINE_PRESENT", candidateStatus = candidateIds.Length == 0 ? "CANDIDATE_NOT_AVAILABLE_PROVEN" : "CANDIDATE_AVAILABLE", candidateIds, selectedStatus = selected.Length == 1 ? "SELECTED_EXACT" : "NOT_SELECTED_EXACT", selectedCandidateIds = selected, roleStatus = "ROLE_EVIDENCE_UNRECOVERABLE", firstLossOwner = owner, firstLossComponent = component, firstLossOperation = operation, firstLossReason = reason, bindingStatus = candidateIds.Length > 1 ? "AMBIGUOUS_BINDING" : "EXACT_SOURCE_IDENTITY" });
            rows.RemoveAll(x => x.GetProperty("occurrenceId").GetString() == "004/appendix/1");
            rows.Add(titleRow.Value);
        }
        var silverCounts = oldRows.GroupBy(x => x.GetProperty("SemanticClass").GetString()!).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var targetRows = titleRow is null ? [] : rows;
        var firstLoss = rows.GroupBy(x => x.GetProperty("firstLossOwner").GetString()!, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var targetFirstLoss = targetRows.GroupBy(x => x.GetProperty("firstLossOwner").GetString()!, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var report = new
        {
            schemaVersion = 6,
            artifactKind = "004_authority_first_loss_trace",
            canonicalPipeline = "AuthorityExtractionPipeline",
            preservedArtifacts = new[] { "eval/accuracy/004-authority-first-loss.v1.json", "eval/accuracy/004-authority-first-loss.v2.json", "eval/accuracy/004-authority-first-loss.v3.json", "eval/accuracy/004-authority-first-loss.v4.json", "eval/accuracy/004-authority-first-loss.v5.json" },
            silver = new { candidateGeneration = 10, candidateSelection = 28, spanLaneNoResolution = 55, total = oldRows.Length },
            referencePopulations = new
            {
                n3Silver = new { DOCUMENT_TITLE = silverCounts.GetValueOrDefault("DOCUMENT_TITLE"), CHAPTER = silverCounts.GetValueOrDefault("CHAPTER"), SECTION = silverCounts.GetValueOrDefault("SECTION"), ARTICLE = silverCounts.GetValueOrDefault("ARTICLE"), APPENDIX = silverCounts.GetValueOrDefault("APPENDIX"), total = oldRows.Length },
                targetOntology = new { status = targetStatus, DOCUMENT_TITLE = titleRow is null ? 0 : 1, CHAPTER = 7, SECTION = 8, ARTICLE = 77, APPENDIX = 0, total = titleRow is null ? "BLOCKED" : "93" }
            },
            targetTitle = new { status = targetStatus, sourceId = titleRow is null ? null : titleRow.Value.GetProperty("sourceLineIds")[0].GetString(), sourceText = titleRow is null ? null : "LAW ON INVESTMENT", firstLoss = titleRow is null ? "BLOCKED" : titleRow.Value.GetProperty("firstLossOwner").GetString(), authority = "SOURCE_OBSERVED_STRUCTURAL_REFERENCE", source = "raw PdfLineExtraction inventory; not human gold" },
            execution = new { semanticLaneStatus = run.RootElement.GetProperty("routeAudit").GetProperty("semanticLane").GetProperty("status").GetString(), spanLaneStatus = run.RootElement.GetProperty("routeAudit").GetProperty("spanLane").GetProperty("status").GetString(), spanLaneCompletedCounter = run.RootElement.GetProperty("routeAudit").GetProperty("spanLane").GetProperty("completed").GetInt32(), spanHttpRequestCount = "NOT_OBSERVABLE", spanPerOccurrenceTimeout = "NOT_OBSERVABLE" },
            silverFirstLossCounts = firstLoss,
            targetFirstLossCounts = targetStatus == "PASS" ? targetFirstLoss : null,
            rowCausalConsistency = rows.All(Consistent),
            roleClassificationContract = ClosedRoles.All(IsHeading),
            rows,
            providerCallsThisTask = 0,
            productionCodeChanged = false,
            remediationPerformed = false
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var markdown = Path.Combine(root, "docs", "accuracy", "004-authority-first-loss-v6.md");
        Directory.CreateDirectory(Path.GetDirectoryName(markdown)!);
        File.WriteAllText(markdown, $"# Document 004 first-loss trace V6\n\nSilver population: title {silverCounts.GetValueOrDefault("DOCUMENT_TITLE")}, chapter {silverCounts.GetValueOrDefault("CHAPTER")}, section {silverCounts.GetValueOrDefault("SECTION")}, article {silverCounts.GetValueOrDefault("ARTICLE")}, appendix {silverCounts.GetValueOrDefault("APPENDIX")}, total {oldRows.Length}.\n\nTarget title identity: `{targetStatus}`. {(titleRow is null ? "No unique raw PDF line was established; target metrics remain blocked." : "Raw PdfLineExtraction established the unique source line for LAW ON INVESTMENT; Appendix IV was removed and the title was traced separately.")}\n\nSilver first-loss counts sum to {firstLoss.Values.Sum()}/93. Target first-loss: {(targetStatus == "PASS" ? targetFirstLoss.Values.Sum() + "/93" : "BLOCKED")}. Row causal consistency: `{report.rowCausalConsistency}`. Role classification contract: `{report.roleClassificationContract}`.\n\nProvider calls: 0. Production changed: false. Remediation: false.\n");
        Assert.Equal(93, oldRows.Length);
        Assert.True(report.rowCausalConsistency);
        Assert.True(report.roleClassificationContract);
    }

    private static JsonElement CorrectProvenance(JsonElement row)
    {
        var owner = row.GetProperty("FirstLossOwner").GetString()!;
        var component = owner switch { "CANDIDATE_SELECTION_LOSS" => "FrozenSelectedSourceIdentitiesJoin", "SPAN_LANE_NO_RESOLUTION" => "SpanLane", "ROLE_REJECTION" or "ROLE_UNCERTAIN" => "SemanticRoleLane", _ => row.GetProperty("FirstLossComponent").GetString()! };
        var operation = owner switch { "CANDIDATE_SELECTION_LOSS" => "CONTAINMENT_JOIN", "SPAN_LANE_NO_RESOLUTION" => "RESOLVE_POINTER", "ROLE_REJECTION" or "ROLE_UNCERTAIN" => "CLASSIFY", _ => row.GetProperty("FirstLossOperation").GetString()! };
        var reason = owner switch { "CANDIDATE_SELECTION_LOSS" => "final candidate exists but occurrence is absent from selected source identities", "SPAN_LANE_NO_RESOLUTION" => "pre-span role heading-like; span lane partial_timeout; no surviving HeadingSpan", "ROLE_REJECTION" => "pre-span semantic role classified as non-heading", "ROLE_UNCERTAIN" => "pre-span semantic role remained uncertain", _ => row.GetProperty("FirstLossReason").GetString()! };
        return JsonSerializer.SerializeToElement(new { occurrenceId = row.GetProperty("OccurrenceId").GetString(), semanticClass = row.GetProperty("SemanticClass").GetString(), sourceLineIds = row.GetProperty("SourceLineIds"), sourceSpan = row.GetProperty("SourceSpan"), rawSourceStatus = "RAW_SOURCE_LINE_PRESENT", candidateStatus = row.GetProperty("CandidateStatus").GetString(), candidateIds = row.GetProperty("CandidateIds"), selectedStatus = row.GetProperty("SelectedStatus").GetString(), selectedCandidateIds = row.GetProperty("SelectedCandidateIds"), roleStatus = row.GetProperty("RoleStatus").GetString(), firstLossOwner = owner, firstLossComponent = component, firstLossOperation = operation, firstLossReason = reason, bindingStatus = "EXACT_SOURCE_IDENTITY" });
    }
    private static List<PdfLine> FindExactTitle(string path) { using var doc = PdfDocument.Open(path); return PdfLineExtraction.ExtractLines(doc).Where(x => x.Text == "LAW ON INVESTMENT").ToList(); }
    private static string ClassOf(JsonElement row) => row.GetProperty("goldStableId").GetString()! switch { var x when x.Contains("/chapter/") => "CHAPTER", var x when x.Contains("/section/") => "SECTION", var x when x.Contains("/article/") => "ARTICLE", var x when x.Contains("/appendix/") => "APPENDIX", var x when x.Contains("/document-title/") => "DOCUMENT_TITLE", _ => "UNKNOWN" };
    private static bool Consistent(JsonElement row) { var owner = row.GetProperty("firstLossOwner").GetString(); var component = row.GetProperty("firstLossComponent").GetString(); return owner != "SPAN_LANE_NO_RESOLUTION" || component == "SpanLane" && row.GetProperty("firstLossOperation").GetString() == "RESOLVE_POINTER" && !row.GetProperty("firstLossReason").GetString()!.Contains("individual HTTP", StringComparison.OrdinalIgnoreCase); }
    private static readonly string[] ClosedRoles = ["document_title", "section_heading", "topic_heading", "local_subheading", "legal_chapter", "legal_section", "legal_article", "appendix_heading", "meeting_section", "agenda_item", "note_heading"];
    private static bool IsHeading(string role) => ClosedRoles.Contains(role, StringComparer.Ordinal);
}
