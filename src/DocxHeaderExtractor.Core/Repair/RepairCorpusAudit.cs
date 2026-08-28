using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Repair;

public sealed record RepairCorpusAuditReport(
    string FormatVersion,
    DateTimeOffset CreatedAt,
    int Documents,
    int GateFailed,
    int NeedsAnalysis,
    int MissingKey,
    IReadOnlyDictionary<string, int> RouteDistribution,
    IReadOnlyDictionary<string, int> DiagnosticDistribution,
    IReadOnlyList<string> RareRoutes,
    double CorpusMedianReviewRate,
    IReadOnlyList<string> SuspectedUpstreamErrors,
    IReadOnlyList<RepairCorpusAuditRow> Rows);

public sealed record RepairCorpusAuditRow(
    string File,
    string SourcePath,
    string Group,
    bool HasKey,
    IReadOnlyList<string> KeyPaths,
    string? DocumentMode,
    string? CurrentRoute,
    string? BestRoute,
    string? BaselineRoute,
    bool BaselineMatchedCurrent,
    bool GatePassed,
    IReadOnlyList<string> FailedGates,
    bool NeedsAnalysis,
    string? DiagnosticStatus,
    string? DiagnosticReason,
    int ParagraphCount,
    int CandidateCount,
    int HeadingCount,
    int AutoAcceptedCalibrated,
    int AutoAcceptedDeterministic,
    int AutoAcceptedUncalibratedEvidence,
    int HumanVerified,
    int DisputedCount,
    double ReviewRate,
    bool SuspectedUpstreamError,
    string? DiagnosticGateReason,
    string? Error,
    bool TaggedPdfEvidenceAccepted = false,
    int TaggedPdfEvidenceHeadings = 0,
    string? TaggedPdfEvidenceReason = null,
    int PdfBookmarkEntries = 0,
    int PdfTocEntries = 0,
    int PdfTocDocxAnchors = 0,
    int DocxTocParagraphs = 0,
    int DocxOutlineLevelParagraphs = 0,
    int DocxBuiltInHeadingParagraphs = 0,
    int DocxNumberedParagraphs = 0,
    int TextNumberMarkerParagraphs = 0,
    int LegalMarkerParagraphs = 0,
    string? StructureSourceAuditError = null);

public static class RepairCorpusAudit
{
    public const string FormatVersion = "dhx-repair-corpus-audit/v1";

    public static async Task<RepairCorpusAuditReport> RunAsync(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, IReadOnlyList<string>> keyIndex,
        PipelineOptions options,
        CancellationToken ct = default)
    {
        var rows = new List<RepairCorpusAuditRow>();
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var keys = keyIndex.TryGetValue(Path.GetFileNameWithoutExtension(file), out var found)
                ? found
                : [];

            try
            {
                using var repairRunner = new AuthorityRepairOutlineRunner(options);
                var outline = await repairRunner.RunAsync(file, ct);
                var structureSources = ProbeStructureSources(file, outline.DocumentMode);
                var candidateReport = RepairCandidateRunner.Analyze(outline);
                var validation = RepairValidationGate.Validate(outline, candidateReport);
                var baseline = BaselineRouteTree(outline, candidateReport);
                var failedGates = validation.Gates
                    .Where(g => !g.Passed && g.Severity == "blocker")
                    .Select(g => g.Name)
                    .ToList();
                var needsAnalysis = outline.Diagnostics?.Status != "normal" ||
                                    !validation.Passed ||
                                    outline.Headings.Any(h =>
                                        h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);
                var reviewRate = RepairDiagnosticGate.ReviewRate(outline.Headings);

                rows.Add(new RepairCorpusAuditRow(
                    Path.GetFileName(file),
                    Path.GetFullPath(file),
                    GroupName(file),
                    keys.Count > 0,
                    keys,
                    outline.DocumentMode?.Mode.ToString(),
                    outline.DeterministicRoute,
                    candidateReport.BestRoute,
                    baseline,
                    string.Equals(baseline, outline.DeterministicRoute, StringComparison.Ordinal),
                    validation.Passed,
                    failedGates,
                    needsAnalysis,
                    outline.Diagnostics?.Status,
                    outline.Diagnostics?.Reason,
                    outline.ParagraphCount,
                    outline.CandidateCount,
                    outline.Headings.Count,
                    outline.DecisionAudit?.AutoAcceptedCalibrated ?? 0,
                    outline.DecisionAudit?.AutoAcceptedDeterministic ?? 0,
                    outline.DecisionAudit?.AutoAcceptedUncalibratedEvidence ?? 0,
                    outline.DecisionAudit?.HumanVerified ?? 0,
                    outline.DisputedCount,
                    reviewRate,
                    false, // điền lại ở bước gộp corpus bên dưới, cần trung vị toàn corpus trước
                    null,
                    null,
                    structureSources.TaggedAccepted,
                    structureSources.TaggedHeadings,
                    structureSources.TaggedReason,
                    structureSources.BookmarkEntries,
                    structureSources.PdfTocEntries,
                    structureSources.PdfTocDocxAnchors,
                    structureSources.DocxTocParagraphs,
                    structureSources.DocxOutlineLevelParagraphs,
                    structureSources.DocxBuiltInHeadingParagraphs,
                    structureSources.DocxNumberedParagraphs,
                    structureSources.TextNumberMarkerParagraphs,
                    structureSources.LegalMarkerParagraphs,
                    structureSources.Error));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                rows.Add(new RepairCorpusAuditRow(
                    Path.GetFileName(file),
                    Path.GetFullPath(file),
                    GroupName(file),
                    keys.Count > 0,
                    keys,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    ["exception"],
                    true,
                    "error",
                    ex.Message,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    null,
                    ex.Message,
                    false,
                    0,
                    "pipeline-error",
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    null));
            }
        }

        var gateResults = RepairDiagnosticGate.Evaluate(rows);
        var gateByFile = gateResults.ToDictionary(g => g.File, StringComparer.OrdinalIgnoreCase);
        rows = rows
            .Select(r => gateByFile.TryGetValue(r.File, out var g)
                ? r with { SuspectedUpstreamError = g.SuspectedUpstreamError, DiagnosticGateReason = g.Reason }
                : r)
            .ToList();
        var corpusMedianReviewRate = gateResults.Count > 0 ? gateResults[0].CorpusMedianReviewRate : 0;

        var routeDistribution = rows
            .GroupBy(r => r.CurrentRoute ?? "(none)")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var diagnosticDistribution = rows
            .GroupBy(r => $"{r.DiagnosticStatus ?? "(none)"}:{r.DiagnosticReason ?? "(none)"}")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var rareRoutes = routeDistribution
            .Where(kv => kv.Value <= 2 || kv.Key == "(none)")
            .Select(kv => $"{kv.Key}:{kv.Value}")
            .ToList();
        var suspectedUpstreamErrors = rows
            .Where(r => r.SuspectedUpstreamError)
            .Select(r => r.File)
            .ToList();

        return new RepairCorpusAuditReport(
            FormatVersion,
            DateTimeOffset.UtcNow,
            rows.Count,
            rows.Count(r => !r.GatePassed),
            rows.Count(r => r.NeedsAnalysis),
            rows.Count(r => !r.HasKey),
            routeDistribution,
            diagnosticDistribution,
            rareRoutes,
            corpusMedianReviewRate,
            suspectedUpstreamErrors,
            rows);
    }

    public static string ToJson(RepairCorpusAuditReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    public static string ToCsv(RepairCorpusAuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("file,group,hasKey,keyCount,mode,currentRoute,bestRoute,baselineRoute,baselineMatchedCurrent,gatePassed,failedGates,needsAnalysis,diagnosticStatus,diagnosticReason,paragraphs,candidates,headings,autoAcceptedCalibrated,autoAcceptedDeterministic,autoAcceptedUncalibratedEvidence,humanVerified,disputed,reviewRate,suspectedUpstreamError,diagnosticGateReason,taggedPdfEvidenceAccepted,taggedPdfEvidenceHeadings,taggedPdfEvidenceReason,pdfBookmarkEntries,pdfTocEntries,pdfTocDocxAnchors,docxTocParagraphs,docxOutlineLevelParagraphs,docxBuiltInHeadingParagraphs,docxNumberedParagraphs,textNumberMarkerParagraphs,legalMarkerParagraphs,structureSourceAuditError,error,path");
        foreach (var r in report.Rows)
        {
            sb.Append(Escape(r.File)).Append(',')
              .Append(Escape(r.Group)).Append(',')
              .Append(r.HasKey).Append(',')
              .Append(r.KeyPaths.Count).Append(',')
              .Append(Escape(r.DocumentMode)).Append(',')
              .Append(Escape(r.CurrentRoute)).Append(',')
              .Append(Escape(r.BestRoute)).Append(',')
              .Append(Escape(r.BaselineRoute)).Append(',')
              .Append(r.BaselineMatchedCurrent).Append(',')
              .Append(r.GatePassed).Append(',')
              .Append(Escape(string.Join(';', r.FailedGates))).Append(',')
              .Append(r.NeedsAnalysis).Append(',')
              .Append(Escape(r.DiagnosticStatus)).Append(',')
              .Append(Escape(r.DiagnosticReason)).Append(',')
              .Append(r.ParagraphCount).Append(',')
              .Append(r.CandidateCount).Append(',')
              .Append(r.HeadingCount).Append(',')
              .Append(r.AutoAcceptedCalibrated).Append(',')
              .Append(r.AutoAcceptedDeterministic).Append(',')
              .Append(r.AutoAcceptedUncalibratedEvidence).Append(',')
              .Append(r.HumanVerified).Append(',')
              .Append(r.DisputedCount).Append(',')
              .Append(r.ReviewRate.ToString("F4")).Append(',')
              .Append(r.SuspectedUpstreamError).Append(',')
              .Append(Escape(r.DiagnosticGateReason)).Append(',')
              .Append(r.TaggedPdfEvidenceAccepted).Append(',')
              .Append(r.TaggedPdfEvidenceHeadings).Append(',')
              .Append(Escape(r.TaggedPdfEvidenceReason)).Append(',')
              .Append(r.PdfBookmarkEntries).Append(',')
              .Append(r.PdfTocEntries).Append(',')
              .Append(r.PdfTocDocxAnchors).Append(',')
              .Append(r.DocxTocParagraphs).Append(',')
              .Append(r.DocxOutlineLevelParagraphs).Append(',')
              .Append(r.DocxBuiltInHeadingParagraphs).Append(',')
              .Append(r.DocxNumberedParagraphs).Append(',')
              .Append(r.TextNumberMarkerParagraphs).Append(',')
              .Append(r.LegalMarkerParagraphs).Append(',')
              .Append(Escape(r.StructureSourceAuditError)).Append(',')
              .Append(Escape(r.Error)).Append(',')
              .Append(Escape(r.SourcePath))
              .AppendLine();
        }
        return sb.ToString();
    }

    private static StructureSourceAudit ProbeStructureSources(string file, DocumentModeReport? mode)
    {
        var extension = Path.GetExtension(file);
        if (!extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".docm", StringComparison.OrdinalIgnoreCase))
            return new StructureSourceAudit(false, 0, "unsupported-docx-source", 0, 0, 0, 0, 0, 0, 0, 0, 0, null);

        try
        {
            var extraction = new DocxSlimExtractor().ExtractForAuthority(file);
            var source = extraction.Source;
            var features = NumberingStyleFeatures.FromSourceDocument(source);
            var slim = extraction.Compatibility.ForLegacyCompatibility();
            var tagged = PdfTaggedEvidenceOutline.TryBuild(file, slim);
            var toc = mode is null
                ? PdfTocDictionaryOutlineResult.NotApplicable("no-document-mode")
                : PdfTocDictionaryOutline.TryBuild(file, slim, mode);
            var pdf = PdfTextbookOutline.FindSiblingPdf(file);
            var bookmarks = pdf is null ? 0 : PdfBookmarkProbe.Analyze(pdf).Candidates.Count;
            return new StructureSourceAudit(
                tagged.Accepted,
                tagged.Headings.Count,
                tagged.Reason,
                bookmarks,
                toc.Probe.Entries,
                toc.Probe.RelaxedPageAnchors,
                slim.Paragraphs.Count(p => p.InTableOfContents),
                features.Styles.Count(style => style.OutlineLevel is not null),
                slim.Paragraphs.Count(p => p.HasBuiltInHeadingStyle),
                features.Numbering.Count(numbering => numbering.NumberingId is not null || numbering.NumberingLevel is not null),
                slim.Paragraphs.Count(p => NumberingAudit.Parse(p.Text) is not null),
                slim.Paragraphs.Count(p => DocumentModeClassifier.IsLegalMarker(p.Text)),
                null);
        }
        catch (Exception ex)
        {
            return new StructureSourceAudit(false, 0, $"probe-error:{ex.GetType().Name}", 0, 0, 0, 0, 0, 0, 0, 0, 0,
                $"probe-error:{ex.GetType().Name}");
        }
    }

    private sealed record StructureSourceAudit(
        bool TaggedAccepted,
        int TaggedHeadings,
        string TaggedReason,
        int BookmarkEntries,
        int PdfTocEntries,
        int PdfTocDocxAnchors,
        int DocxTocParagraphs,
        int DocxOutlineLevelParagraphs,
        int DocxBuiltInHeadingParagraphs,
        int DocxNumberedParagraphs,
        int TextNumberMarkerParagraphs,
        int LegalMarkerParagraphs,
        string? Error);

    private static string? BaselineRouteTree(DocumentOutline outline, RepairCandidateReport candidates)
    {
        var current = outline.DeterministicRoute;
        bool CandidateAccepted(string route) =>
            candidates.Candidates.Any(c => string.Equals(c.Route, route, StringComparison.Ordinal) && c.Accepted);
        bool CandidateStrong(string route) =>
            candidates.Candidates.Any(c =>
                string.Equals(c.Route, route, StringComparison.Ordinal) &&
                (c.RouteValidationStatus == "route_metrics_strong" || c.Accepted));

        return outline.DocumentMode?.Mode switch
        {
            DocumentMode.VietnameseLegal => "auto:vietnamese-legal",
            DocumentMode.OutlineLevelDriven => "auto:outline-level",
            DocumentMode.TypedNumbering when current == "auto:pdf-textbook-layout" => "auto:pdf-textbook-layout",
            DocumentMode.TypedNumbering when CandidateStrong("auto:rfc-toc-dictionary") => "auto:rfc-toc-dictionary",
            DocumentMode.TypedNumbering when CandidateAccepted("auto:part-section-text-toc") => "auto:part-section-text-toc",
            DocumentMode.TypedNumbering => "auto:typed-numbering",
            DocumentMode.FormatDriven when current == "auto:pdf-bold-label" || CandidateAccepted("auto:pdf-bold-label") => "auto:pdf-bold-label",
            DocumentMode.FormatDriven when current == "auto:vietnamese-administrative" => "auto:vietnamese-administrative",
            _ => current,
        };
    }

    private static string GroupName(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        return string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir);
    }

    private static string Escape(string? value)
    {
        value ??= "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
