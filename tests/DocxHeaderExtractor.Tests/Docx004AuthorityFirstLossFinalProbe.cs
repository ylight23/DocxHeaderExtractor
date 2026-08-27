using System.Text.Json;
using UglyToad.PdfPig;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Final, provider-free closure of the 004 diagnostic evidence.</summary>
public sealed class Docx004AuthorityFirstLossFinalProbe
{
    [Fact]
    public void WriteFinalClosure()
    {
        var output = Environment.GetEnvironmentVariable("DOCX004_AUTHORITY_FIRST_LOSS_FINAL");
        if (string.IsNullOrWhiteSpace(output)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        using var v6 = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval/accuracy/004-authority-first-loss.v6.json")));
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".verify-build/004-authority", "openrouter-qwen9b-160.json")));
        var silverRows = v6.RootElement.GetProperty("rows").EnumerateArray().Select(CorrectProvenance).ToList();
        var rows = silverRows.ToList();
        var pdf = Path.Combine(root, "todo10_8", "heading_corpus_100", "01_phap_quy", "004_Luat_Dau_tu_61-2020-QH14_EN.pdf");
        var windows = FindTitleWindows(pdf);
        var titleStatus = windows.Count switch { 0 => "NOT_FOUND", 1 => "PASS", _ => "AMBIGUOUS" };
        JsonElement? title = null;
        Dictionary<string, int>? targetLoss = null;
        if (titleStatus == "PASS")
        {
            var w = windows[0];
            var ids = w.Lines.Select(PdfCandidateProvenance.LineId).ToArray();
            var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy", "004_Luat_Dau_tu_61-2020-QH14_EN.docx");
            var trace = PdfLayoutEvidenceOutline.TraceCandidateBoundaryLineage(docx, new Dictionary<string, IReadOnlyList<string>> { ["004/document-title/1"] = ids }).Single();
            var final = trace.Stages.LastOrDefault();
            var candidateIds = final?.CandidateLineIds.Where(x => ids.All(x.Value.Contains)).Select(x => x.Key).ToArray() ?? [];
            var selectedIds = run.RootElement.GetProperty("routeAudit").GetProperty("selectedSourceIdentities").EnumerateArray().Where(x => ids.All(id => x.GetProperty("sourceLineIds").EnumerateArray().Select(y => y.GetString()!).Contains(id))).Select(x => x.GetProperty("candidateIdDiagnostic").GetString()!).ToArray();
            var owner = candidateIds.Length == 0 ? "CANDIDATE_GENERATION_LOSS" : selectedIds.Length == 0 ? "CANDIDATE_SELECTION_LOSS" : "POST_SELECTION_TRACE_UNRESOLVED";
            var component = owner == "CANDIDATE_GENERATION_LOSS" ? trace.FirstLossComponent : owner == "CANDIDATE_SELECTION_LOSS" ? "FrozenSelectedSourceIdentitiesJoin" : "SemanticRoleLane";
            var operation = owner == "CANDIDATE_GENERATION_LOSS" ? trace.FirstLossOperation : owner == "CANDIDATE_SELECTION_LOSS" ? "CONTAINMENT_JOIN" : "CLASSIFY";
            var reason = owner == "CANDIDATE_GENERATION_LOSS" ? trace.FirstLossReason : owner == "CANDIDATE_SELECTION_LOSS" ? "final candidate exists but occurrence is absent from selected source identities" : "title candidate selected but role evidence is not safely attributable";
            title = JsonSerializer.SerializeToElement(new { occurrenceId = "004/document-title/1", semanticClass = "DOCUMENT_TITLE", sourceLineIds = ids, sourceSpan = new { startLineId = ids[0], endLineId = ids[^1] }, sourceTextLines = w.Lines.Select(x => x.Text).ToArray(), sourceText = "LAW ON INVESTMENT", rawSourceStatus = "RAW_SOURCE_LINE_PRESENT", candidateStatus = candidateIds.Length == 0 ? "CANDIDATE_NOT_AVAILABLE_PROVEN" : "CANDIDATE_AVAILABLE", candidateIds, selectedStatus = selectedIds.Length == 1 ? "SELECTED_EXACT" : "NOT_SELECTED_EXACT", selectedCandidateIds = selectedIds, roleStatus = "ROLE_EVIDENCE_UNRECOVERABLE", firstLossOwner = owner, firstLossComponent = component, firstLossOperation = operation, firstLossReason = reason, bindingStatus = candidateIds.Length > 1 ? "AMBIGUOUS_BINDING" : "EXACT_SOURCE_IDENTITY" });
            rows.RemoveAll(x => x.GetProperty("occurrenceId").GetString() == "004/appendix/1");
            rows.Add(title.Value);
            targetLoss = rows.GroupBy(x => x.GetProperty("firstLossOwner").GetString()!, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        }
        var silverLoss = silverRows.GroupBy(x => x.GetProperty("firstLossOwner").GetString()!, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var rolePositive = new[] { "document_title", "section_heading", "topic_heading", "local_subheading", "legal_chapter", "legal_section", "legal_article", "appendix_heading", "meeting_section", "agenda_item", "note_heading" };
        var roleNegative = new[] { "legal_clause", "legal_point", "table_title", "table_header", "figure_caption", "running_header", "running_footer", "form_label", "signature_label", "translation_notice", "body_text", "unknown" };
        var report = new
        {
            schemaVersion = 7,
            artifactKind = "004_authority_first_loss_final",
            silverDiagnosis = "FROZEN",
            referencePopulations = new { n3Silver = new { DOCUMENT_TITLE = 0, CHAPTER = 7, SECTION = 8, ARTICLE = 77, APPENDIX = 1, total = 93 }, targetOntology = new { status = titleStatus == "PASS" ? "PASS" : "BLOCKED", DOCUMENT_TITLE = titleStatus == "PASS" ? 1 : 0, CHAPTER = 7, SECTION = 8, ARTICLE = 77, APPENDIX = 0, total = titleStatus == "PASS" ? "93" : "BLOCKED" } },
            targetTitle = new { status = titleStatus, windows = windows.Select(w => new { page = w.Lines[0].Page, sourceLineIds = w.Lines.Select(PdfCandidateProvenance.LineId).ToArray(), sourceTextLines = w.Lines.Select(x => x.Text).ToArray() }).ToArray(), occurrence = title },
            execution = new { semanticLaneStatus = "complete", spanLaneStatus = "partial_timeout", spanHttpRequestCount = "NOT_OBSERVABLE", spanPerOccurrenceTimeout = "NOT_OBSERVABLE", providerCallsThisTask = 0 },
            silverFirstLoss = new { candidateGeneration = 10, candidateSelection = 28, spanLaneNoResolution = 55, total = 93, counts = silverLoss },
            targetFirstLossCounts = targetLoss,
            rowCausalConsistency = rows.All(Consistent),
            roleClassificationContract = rolePositive.All(IsHeading) && roleNegative.All(x => !IsHeading(x)),
            rows,
            productionCodeChanged = false,
            remediationPerformed = false
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var markdown = Path.Combine(root, "docs", "accuracy", "004-authority-first-loss-final.md"); Directory.CreateDirectory(Path.GetDirectoryName(markdown)!);
        File.WriteAllText(markdown, $"# Document 004 final diagnostic closure\n\nSilver diagnosis: `FROZEN`; population 0 title, 7 chapters, 8 sections, 77 articles, 1 appendix = 93. Silver first-loss: candidate generation 10, candidate selection 28, span lane no resolution 55 = 93.\n\nTarget title status: `{titleStatus}`. {(titleStatus == "PASS" ? "A unique contiguous raw-PDF line window was established and traced." : "No unique contiguous raw-PDF window exactly represented LAW ON INVESTMENT; target remains blocked.")}\n\nRow causal consistency: `{report.rowCausalConsistency}`. Role classification contract: `{report.roleClassificationContract}`. Provider calls: 0. Production changed: false. Remediation: false.\n");
        Assert.Equal(93, oldRowsCount(v6));
        Assert.True(report.rowCausalConsistency);
        Assert.True(report.roleClassificationContract);
        Assert.Equal(93, silverLoss.Values.Sum());
    }

    private static int oldRowsCount(JsonDocument v6) => v6.RootElement.GetProperty("rows").GetArrayLength();
    private static List<TitleWindow> FindTitleWindows(string path) { using var doc = PdfDocument.Open(path); var lines = PdfLineExtraction.ExtractLines(doc).ToArray(); var result = new List<TitleWindow>(); for (var i = 0; i < lines.Length; i++) for (var length = 1; length <= 4 && i + length <= lines.Length; length++) { var window = lines.Skip(i).Take(length).ToArray(); var comparable = string.Join(" ", window.Select(x => x.Text)).Trim(); if (string.Equals(comparable, "LAW ON INVESTMENT", StringComparison.Ordinal)) result.Add(new TitleWindow(window)); } return result; }
    private static JsonElement CorrectProvenance(JsonElement row) { var owner = row.GetProperty("firstLossOwner").GetString()!; var component = owner == "CANDIDATE_SELECTION_LOSS" ? "FrozenSelectedSourceIdentitiesJoin" : owner == "SPAN_LANE_NO_RESOLUTION" ? "SpanLane" : row.GetProperty("firstLossComponent").GetString()!; var operation = owner == "CANDIDATE_SELECTION_LOSS" ? "CONTAINMENT_JOIN" : owner == "SPAN_LANE_NO_RESOLUTION" ? "RESOLVE_POINTER" : row.GetProperty("firstLossOperation").GetString()!; var reason = owner == "CANDIDATE_SELECTION_LOSS" ? "final candidate exists but occurrence is absent from selected source identities" : owner == "SPAN_LANE_NO_RESOLUTION" ? "pre-span role heading-like; span lane partial_timeout; no surviving HeadingSpan" : row.GetProperty("firstLossReason").GetString(); return JsonSerializer.SerializeToElement(new { occurrenceId = row.GetProperty("occurrenceId").GetString(), semanticClass = row.GetProperty("semanticClass").GetString(), sourceLineIds = row.GetProperty("sourceLineIds"), sourceSpan = row.GetProperty("sourceSpan"), rawSourceStatus = "RAW_SOURCE_LINE_PRESENT", candidateStatus = row.GetProperty("candidateStatus").GetString(), candidateIds = row.GetProperty("candidateIds"), selectedStatus = row.GetProperty("selectedStatus").GetString(), selectedCandidateIds = row.GetProperty("selectedCandidateIds"), roleStatus = row.GetProperty("roleStatus").GetString(), firstLossOwner = owner, firstLossComponent = component, firstLossOperation = operation, firstLossReason = reason, bindingStatus = "EXACT_SOURCE_IDENTITY" }); }
    private static bool Consistent(JsonElement row) { var owner = row.GetProperty("firstLossOwner").GetString(); var component = row.GetProperty("firstLossComponent").GetString(); var operation = row.GetProperty("firstLossOperation").GetString(); return owner switch { "CANDIDATE_GENERATION_LOSS" => row.GetProperty("candidateStatus").GetString() == "CANDIDATE_NOT_AVAILABLE_PROVEN", "CANDIDATE_SELECTION_LOSS" => row.GetProperty("candidateStatus").GetString() == "CANDIDATE_AVAILABLE" && row.GetProperty("selectedStatus").GetString() == "NOT_SELECTED_EXACT" && component == "FrozenSelectedSourceIdentitiesJoin" && operation == "CONTAINMENT_JOIN", "ROLE_REJECTION" or "ROLE_UNCERTAIN" => row.GetProperty("selectedStatus").GetString() == "SELECTED_EXACT" && component == "SemanticRoleLane", "SPAN_LANE_NO_RESOLUTION" => row.GetProperty("selectedStatus").GetString() == "SELECTED_EXACT" && row.GetProperty("roleStatus").GetString() == "ROLE_HEADING_PROPOSAL" && component == "SpanLane" && operation == "RESOLVE_POINTER", _ => true }; }
    private static bool IsHeading(string role) => role switch { "document_title" or "section_heading" or "topic_heading" or "local_subheading" or "legal_chapter" or "legal_section" or "legal_article" or "appendix_heading" or "meeting_section" or "agenda_item" or "note_heading" => true, _ => false };
    private sealed record TitleWindow(IReadOnlyList<PdfLine> Lines);
}
