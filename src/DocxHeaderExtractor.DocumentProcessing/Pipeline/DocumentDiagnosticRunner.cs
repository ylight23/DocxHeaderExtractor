using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Diagnostics over immutable source facts and source-native policy state.</summary>
public static class DocumentDiagnosticRunner
{
    private static readonly Regex TypedNumberSegmentRx = new(@"^\s*\d{1,2}(?:\.\d{1,2})+\.\s+\S", RegexOptions.Compiled);

    public static DocumentDiagnosticReport Analyze(DocxPolicyState policyState, DocumentModeReport modeReport)
    {
        ArgumentNullException.ThrowIfNull(policyState);
        ArgumentNullException.ThrowIfNull(modeReport);
        var style = StyleSignal(policyState.StyleTrust);
        var layout = LayoutSignal(policyState);
        var candidates = CandidateSignals(policyState).ToArray();
        var status = FailureStatus(style, layout, candidates, modeReport, out var reason);
        return new DocumentDiagnosticReport(status, reason, style, layout, candidates);
    }

    private static StyleSignalDiagnostic StyleSignal(StyleTrust? trust) => trust is null
        ? new StyleSignalDiagnostic(0, 0, 0, 0, 0, true, true, false)
        : new StyleSignalDiagnostic(trust.StyledCount, trust.SuspectRatio, trust.Density,
            trust.DistinctLevels, trust.NumberedDisagreeRatio, trust.SelectionTrusted,
            trust.LevelTrusted, trust.StyledCount >= StyleTrust.MinimumStyledSample &&
            (!trust.SelectionTrusted || !trust.LevelTrusted));

    private static LayoutSignalDiagnostic LayoutSignal(DocxPolicyState policyState)
    {
        var mergedParagraphs = 0;
        var mergedMarkers = 0;
        var typedSegments = 0;
        foreach (var paragraph in policyState.Source.Paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph.Text)) continue;
            var segments = ParagraphHeadingSplitter.Segments(paragraph.Text);
            if (segments.Count > 1) { mergedParagraphs++; mergedMarkers += segments.Count; }
            typedSegments += segments.Count(s => TypedNumberSegmentRx.IsMatch(s));
        }
        return new LayoutSignalDiagnostic(mergedParagraphs, mergedMarkers,
            policyState.Paragraphs.Count(p => p.InTableOfContents || p.PrecedesTableOfContents), typedSegments);
    }

    private static IEnumerable<OutlineCandidateDiagnostic> CandidateSignals(DocxPolicyState state)
    {
        var paragraphs = state.Paragraphs.Cast<IPolicyParagraph>().ToArray();
        var styleHeadings = StyleDeclaredOutline.Build(paragraphs);
        var outlineHeadings = StyleDeclaredOutline.BuildFromOutlineLevel(paragraphs);
        var numberingHeadings = StyleDeclaredOutline.BuildFromNumbering(paragraphs);
        var typedHeadings = TypedNumberingOutline.Build(paragraphs, true);
        var book = BookTocDictionaryOutline.Analyze(paragraphs);
        var rfc = RfcTocDictionaryOutline.Analyze(paragraphs);

        yield return Candidate("auto:style-declared", styleHeadings, true, state);
        yield return Candidate("auto:outline-level", outlineHeadings, false, state);
        yield return Candidate("auto:numbering", numberingHeadings, false, state);
        yield return Candidate("auto:typed-numbering", typedHeadings, false, state);
        yield return Candidate("auto:book-toc-dictionary", book.Headings, false, state,
            bodyAnchorRatio: book.Diagnostics.BodyAnchorRatio,
            tocCoverage: book.Diagnostics.DictionaryEntries == 0
                ? null
                : (double)book.Diagnostics.BodyAnchors / book.Diagnostics.DictionaryEntries,
            forcedAccepted: book.Accepted, forcedReason: book.Diagnostics.Reason);
        yield return Candidate("auto:rfc-toc-dictionary", rfc.Headings, false, state,
            bodyAnchorRatio: rfc.Diagnostics.BodyAnchorRatio,
            tocCoverage: rfc.Diagnostics.DictionaryEntries == 0
                ? null
                : (double)rfc.Diagnostics.BodyAnchors / rfc.Diagnostics.DictionaryEntries,
            forcedAccepted: rfc.Accepted, forcedReason: rfc.Diagnostics.Reason);
    }

    private static OutlineCandidateDiagnostic Candidate(string route, IReadOnlyList<HeadingRecord> headings,
        bool styleTrustRequired, DocxPolicyState state, double? bodyAnchorRatio = null,
        double? tocCoverage = null, bool? forcedAccepted = null, string? forcedReason = null)
    {
        var duplicateRate = headings.Count == 0 ? 0 : (double)(headings.Count -
            headings.Select(h => (h.Index, Text: (h.Text ?? string.Empty).Trim())).Distinct().Count()) / headings.Count;
        var polluted = headings.Count(h =>
        {
            var text = (h.Text ?? string.Empty).Trim();
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return text.Length > 180 || text.Count(c => c is '.' or ';') >= 4 || words.Length >= 24 ||
                   (words.Length >= 14 && text.EndsWith('.') && !LooksLikeNumberedLabel(text));
        });
        var pollutionRate = headings.Count == 0 ? 0 : (double)polluted / headings.Count;
        var ordered = headings.OrderBy(h => h.Index).ToArray();
        var jumps = ordered.Length <= 1 ? 0 : Enumerable.Range(1, ordered.Length - 1)
            .Count(i => (ordered[i].Level ?? 0) - (ordered[i - 1].Level ?? 0) > 1);
        var jumpRate = ordered.Length <= 1 ? 0 : (double)jumps / (ordered.Length - 1);
        var rejected = styleTrustRequired && state.StyleTrust is { SelectionTrusted: false };
        var accepted = forcedAccepted ?? (headings.Count > 0 && !rejected && duplicateRate <= .02 &&
            pollutionRate <= .05 && jumpRate <= .25);
        var reason = forcedReason ?? (accepted ? "accepted" : rejected ? "style_selection_untrusted" :
            headings.Count == 0 ? "no_headings" : pollutionRate > .05 ? "title_pollution_high" :
            jumpRate > .25 ? "level_jump_rate_high" : "weak_internal_validation");
        return new OutlineCandidateDiagnostic(route, accepted, reason, headings.Count, duplicateRate,
            pollutionRate, jumpRate, bodyAnchorRatio, tocCoverage);
    }

    private static bool LooksLikeNumberedLabel(string text) => text.Length <= 90 && text.Length > 0 &&
        (char.IsDigit(text[0]) || text.StartsWith("Appendix ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Annex ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Chapter ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Part ", StringComparison.OrdinalIgnoreCase));

    private static string FailureStatus(StyleSignalDiagnostic style, LayoutSignalDiagnostic layout,
        IReadOnlyList<OutlineCandidateDiagnostic> candidates, DocumentModeReport mode, out string reason)
    {
        if (style.Mixed) { reason = "mixed_style_signals"; return "needs_analysis"; }
        if (layout.MergedParagraphs > 0 && candidates.All(c => !c.Accepted)) { reason = "merged_layout_without_valid_candidate"; return "needs_analysis"; }
        if (mode.Mode is DocumentMode.FormatDriven or DocumentMode.SemanticOnly && candidates.All(c => !c.Accepted)) { reason = "fallback_generic_without_valid_candidate"; return "needs_analysis"; }
        reason = "signals_validated";
        return "normal";
    }
}
