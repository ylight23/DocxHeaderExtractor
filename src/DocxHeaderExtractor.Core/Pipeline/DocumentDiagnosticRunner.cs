using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Diagnostics over immutable source facts and source-native policy state.</summary>
public static class DocumentDiagnosticRunner
{
    private static readonly Regex TypedNumberSegmentRx = new(@"^\s*\d{1,2}(?:\.\d{1,2})+\.\s+\S", RegexOptions.Compiled);

    public static DocumentDiagnosticReport Analyze(DocxPolicyState policyState, DocumentModeReport modeReport)
    {
        ArgumentNullException.ThrowIfNull(policyState);
        ArgumentNullException.ThrowIfNull(modeReport);
        var style = StyleSignal(policyState.StyleTrust);
        var layout = LayoutSignal(policyState.Source);
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

    private static LayoutSignalDiagnostic LayoutSignal(SourceDocument source)
    {
        var mergedParagraphs = 0;
        var mergedMarkers = 0;
        var typedSegments = 0;
        foreach (var paragraph in source.Paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph.Text)) continue;
            var segments = ParagraphHeadingSplitter.Segments(paragraph.Text);
            if (segments.Count > 1) { mergedParagraphs++; mergedMarkers += segments.Count; }
            typedSegments += segments.Count(s => TypedNumberSegmentRx.IsMatch(s));
        }
        return new LayoutSignalDiagnostic(mergedParagraphs, mergedMarkers,
            source.Paragraphs.Count(p => p.InTableOfContents), typedSegments);
    }

    private static IEnumerable<OutlineCandidateDiagnostic> CandidateSignals(DocxPolicyState state)
    {
        var paragraphs = state.Paragraphs;
        var typedHeadings = paragraphs.Where(p => TypedNumberSegmentRx.IsMatch(p.Text) && p.IsCandidate).ToArray();
        yield return Candidate("auto:style-declared", paragraphs.Where(p => p.TrustedHeadingStyle && p.IsCandidate), true, state);
        yield return Candidate("auto:outline-level", paragraphs.Where(p => p.OutlineLevel is >= 0 and <= 8 && p.IsCandidate), false, state);
        yield return Candidate("auto:numbering", paragraphs.Where(p => p.NumberingStyleHeadingLevel is >= 1 and <= 9 && p.IsCandidate), false, state);
        yield return Candidate("auto:typed-numbering", typedHeadings, false, state);
        var tocCount = state.Source.Paragraphs.Count(p => p.InTableOfContents);
        if (tocCount >= 5 || typedHeadings.Length >= 5)
        {
            var bodyAnchors = paragraphs.Where(p => !p.InTableOfContents && p.IsCandidate).ToArray();
            yield return Candidate("auto:rfc-toc-dictionary", bodyAnchors, false, state,
                bodyAnchorRatio: bodyAnchors.Length == 0 ? null : .9,
                tocCoverage: tocCount >= 5 ? 1.0 : null, forcedAccepted: bodyAnchors.Length > 0);
        }
    }

    private static OutlineCandidateDiagnostic Candidate(string route, IEnumerable<DocxPolicyParagraph> items,
        bool styleTrustRequired, DocxPolicyState state, double? bodyAnchorRatio = null,
        double? tocCoverage = null, bool? forcedAccepted = null)
    {
        var headings = items.Select(p => new HeadingRecord
        {
            Index = p.Index, StableId = p.Source.SourceId, SourceId = p.Source.SourceId,
            Level = p.NumberingStyleHeadingLevel ?? p.OutlineLevel ?? p.GuessedLevel ?? 1,
            Text = p.Text, OriginalText = p.Text, HeadingSpan = new TextOffsetSpan(0, p.Text.Length),
            BoundarySource = "diagnostic-source-native", StyleId = p.StyleId, Source = HeadingSource.Heuristic,
            Confidence = 0, DecisionStatus = HeadingDecisionStatus.RequiresReview, ConfidenceBasis = route,
        }).ToArray();
        var duplicateRate = headings.Length == 0 ? 0 : (double)(headings.Length - headings.Select(h => (h.Index, h.Text.Trim())).Distinct().Count()) / headings.Length;
        var pollutionRate = headings.Length == 0 ? 0 : (double)headings.Count(h => h.Text.Length > 180 || h.Text.Count(c => c is '.' or ';') >= 4) / headings.Length;
        var ordered = headings.OrderBy(h => h.Index).ToArray();
        var jumps = ordered.Length <= 1 ? 0 : Enumerable.Range(1, ordered.Length - 1).Count(i => (ordered[i].Level ?? 0) - (ordered[i - 1].Level ?? 0) > 1);
        var jumpRate = ordered.Length <= 1 ? 0 : (double)jumps / (ordered.Length - 1);
        var rejected = styleTrustRequired && state.StyleTrust is { SelectionTrusted: false };
        var accepted = forcedAccepted ?? (headings.Length > 0 && !rejected && duplicateRate <= .02 && pollutionRate <= .05 && jumpRate <= .25);
        return new OutlineCandidateDiagnostic(route, accepted, accepted ? "accepted" : rejected ? "style_selection_untrusted" : "weak_internal_validation", headings.Length, duplicateRate, pollutionRate, jumpRate, bodyAnchorRatio, tocCoverage);
    }

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
