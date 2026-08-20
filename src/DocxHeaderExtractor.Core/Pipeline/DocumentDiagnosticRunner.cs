using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Code-first triage cho tài liệu nhiễu: đo tín hiệu trước, chạy các candidate deterministic trong
/// sandbox, rồi ghi report để pipeline/LLM phân tích. Lớp này không sửa code và không chọn output
/// thay pipeline; nó chỉ cung cấp bằng chứng có thể validate.
/// </summary>
public static class DocumentDiagnosticRunner
{
    private static readonly Regex TypedNumberSegmentRx = new(@"^\s*\d{1,2}(?:\.\d{1,2})+\.\s+\S", RegexOptions.Compiled);

    public static DocumentDiagnosticReport Analyze(SlimDocument document, DocumentModeReport modeReport)
    {
        var style = StyleSignal(document);
        var layout = LayoutSignal(document);
        var candidates = CandidateSignals(document, modeReport).ToList();

        var status = FailureStatus(style, layout, candidates, modeReport, out var reason);
        return new DocumentDiagnosticReport(status, reason, style, layout, candidates);
    }

    private static StyleSignalDiagnostic StyleSignal(SlimDocument document)
    {
        var trust = document.StyleTrust;
        if (trust is null)
            return new StyleSignalDiagnostic(0, 0, 0, 0, 0, true, true, false);

        var mixed = trust.StyledCount >= StyleTrust.MinimumStyledSample &&
                    (!trust.SelectionTrusted || !trust.LevelTrusted);
        return new StyleSignalDiagnostic(
            trust.StyledCount,
            trust.SuspectRatio,
            trust.Density,
            trust.DistinctLevels,
            trust.NumberedDisagreeRatio,
            trust.SelectionTrusted,
            trust.LevelTrusted,
            mixed);
    }

    private static LayoutSignalDiagnostic LayoutSignal(SlimDocument document)
    {
        var mergedParagraphs = 0;
        var mergedMarkers = 0;
        var typedSegments = 0;
        foreach (var paragraph in document.Paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph.Text)) continue;
            var segments = ParagraphHeadingSplitter.Segments(paragraph.Text);
            if (segments.Count > 1)
            {
                mergedParagraphs++;
                mergedMarkers += segments.Count;
            }

            typedSegments += segments.Count(s => TypedNumberSegmentRx.IsMatch(s));
        }

        return new LayoutSignalDiagnostic(
            mergedParagraphs,
            mergedMarkers,
            document.Paragraphs.Count(p => p.InTableOfContents || p.PrecedesTableOfContents),
            typedSegments);
    }

    private static IEnumerable<OutlineCandidateDiagnostic> CandidateSignals(SlimDocument document, DocumentModeReport modeReport)
    {
        yield return Candidate("auto:style-declared", StyleDeclaredOutline.Build(document), styleTrustRequired: true, document);
        yield return Candidate("auto:outline-level", StyleDeclaredOutline.BuildFromOutlineLevel(document), styleTrustRequired: false, document);
        yield return Candidate("auto:numbering", StyleDeclaredOutline.BuildFromNumbering(document), styleTrustRequired: false, document);
        yield return Candidate("auto:typed-numbering", TypedNumberingOutline.Build(document, splitMergedParagraphs: true), styleTrustRequired: false, document);

        var book = BookTocDictionaryOutline.Analyze(document);
        yield return Candidate(
            "auto:book-toc-dictionary",
            book.Headings,
            styleTrustRequired: false,
            document,
            bodyAnchorRatio: book.Diagnostics.BodyAnchorRatio,
            tocCoverage: book.Diagnostics.DictionaryEntries == 0
                ? null
                : (double)book.Diagnostics.BodyAnchors / book.Diagnostics.DictionaryEntries,
            forcedAccepted: book.Accepted,
            forcedReason: book.Diagnostics.Reason);

        var rfc = RfcTocDictionaryOutline.Analyze(document);
        yield return Candidate(
            "auto:rfc-toc-dictionary",
            rfc.Headings,
            styleTrustRequired: false,
            document,
            bodyAnchorRatio: rfc.Diagnostics.BodyAnchorRatio,
            tocCoverage: rfc.Diagnostics.DictionaryEntries == 0
                ? null
                : (double)rfc.Diagnostics.BodyAnchors / rfc.Diagnostics.DictionaryEntries,
            forcedAccepted: rfc.Accepted,
            forcedReason: rfc.Diagnostics.Reason);

        var docling = DoclingLayoutOutline.TryBuild(document.SourcePath, document, modeReport);
        if (docling.Reason != "no-docling-json")
        {
            yield return Candidate(
                "auto:docling-layout",
                docling.Headings,
                styleTrustRequired: false,
                document,
                bodyAnchorRatio: null,
                tocCoverage: null,
                forcedAccepted: docling.Headings.Count > 0,
                forcedReason: docling.Headings.Count > 0 ? docling.Reason : docling.Reason);
        }
    }

    private static OutlineCandidateDiagnostic Candidate(
        string route,
        IReadOnlyList<HeadingRecord> headings,
        bool styleTrustRequired,
        SlimDocument document,
        double? bodyAnchorRatio = null,
        double? tocCoverage = null,
        bool? forcedAccepted = null,
        string? forcedReason = null)
    {
        var duplicateRate = DuplicateRate(headings);
        var pollutionRate = TitlePollutionRate(headings);
        var jumpRate = LevelJumpRate(headings);
        var styleRejected = styleTrustRequired && document.StyleTrust is { SelectionTrusted: false };

        var accepted = forcedAccepted ??
                       (headings.Count > 0 &&
                        !styleRejected &&
                        duplicateRate <= 0.02 &&
                        pollutionRate <= 0.05 &&
                        jumpRate <= 0.25);
        var reason = forcedReason ?? Reason(headings.Count, styleRejected, duplicateRate, pollutionRate, jumpRate, accepted);
        return new OutlineCandidateDiagnostic(
            route,
            accepted,
            reason,
            headings.Count,
            duplicateRate,
            pollutionRate,
            jumpRate,
            bodyAnchorRatio,
            tocCoverage);
    }

    private static string FailureStatus(
        StyleSignalDiagnostic style,
        LayoutSignalDiagnostic layout,
        IReadOnlyList<OutlineCandidateDiagnostic> candidates,
        DocumentModeReport modeReport,
        out string reason)
    {
        if (style.Mixed)
        {
            reason = "mixed_style_signals";
            return "needs_analysis";
        }

        if (layout.MergedParagraphs > 0 && candidates.All(c => !c.Accepted))
        {
            reason = "merged_layout_without_valid_candidate";
            return "needs_analysis";
        }

        if (modeReport.Mode is DocumentMode.FormatDriven or DocumentMode.SemanticOnly &&
            candidates.All(c => !c.Accepted))
        {
            reason = "fallback_generic_without_valid_candidate";
            return "needs_analysis";
        }

        reason = "signals_validated";
        return "normal";
    }

    private static string Reason(
        int count,
        bool styleRejected,
        double duplicateRate,
        double pollutionRate,
        double jumpRate,
        bool accepted)
    {
        if (accepted) return "accepted";
        if (count == 0) return "no_headings";
        if (styleRejected) return "style_selection_untrusted";
        if (duplicateRate > 0.02) return "duplicate_heading_rate_high";
        if (pollutionRate > 0.05) return "title_pollution_high";
        if (jumpRate > 0.25) return "level_jump_rate_high";
        return "weak_internal_validation";
    }

    private static double DuplicateRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var unique = headings.Select(h => (h.Index, Text: (h.Text ?? "").Trim())).Distinct().Count();
        return (double)(headings.Count - unique) / headings.Count;
    }

    private static double TitlePollutionRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var polluted = headings.Count(h =>
        {
            var text = h.Text?.Trim() ?? "";
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return text.Length > 180 ||
                   text.Count(c => c is '.' or ';') >= 4 ||
                   words.Length >= 24 ||
                   (words.Length >= 14 && text.EndsWith('.') && !LooksLikeNumberedLabel(text));
        });
        return (double)polluted / headings.Count;
    }

    private static bool LooksLikeNumberedLabel(string text) =>
        text.Length <= 90 &&
        (char.IsDigit(text[0]) ||
         text.StartsWith("Appendix ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Annex ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Chapter ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Part ", StringComparison.OrdinalIgnoreCase));

    private static double LevelJumpRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count <= 1) return 0;
        var ordered = headings.OrderBy(h => h.Index).ToList();
        var jumps = 0;
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Level - ordered[i - 1].Level > 1)
                jumps++;
        return (double)jumps / (ordered.Count - 1);
    }
}
