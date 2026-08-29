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
        var paragraphs = state.Paragraphs.OrderBy(p => p.Index).ToArray();
        var typedHeadings = TypedHeadings(paragraphs);

        // These projections deliberately use structural policy facts, not IsCandidate. The legacy
        // diagnostic producers run before a candidate policy is allowed to suppress evidence.
        yield return Candidate("auto:style-declared",
            paragraphs.Where(p => p.HasBuiltInHeadingStyle && !string.IsNullOrWhiteSpace(p.Text)).ToArray(),
            true, state);
        yield return Candidate("auto:outline-level",
            paragraphs.Where(p => p.IsCandidate && OutlineEvidenceLevel(p) is not null && !p.InTableOfContents).ToArray(),
            false, state);
        yield return Candidate("auto:numbering",
            paragraphs.Where(p => p.NumberingStyleHeadingLevel is >= 1 and <= 9 && !p.InTableOfContents).ToArray(),
            false, state);
        yield return Candidate("auto:typed-numbering", typedHeadings, false, state);

        // The dictionary producers remain source-native in the policy path. Their conservative
        // diagnostic contract is retained here until their full native implementations are ported.
        yield return Candidate("auto:book-toc-dictionary", Array.Empty<DocxPolicyParagraph>(), false, state,
            bodyAnchorRatio: 0, tocCoverage: null, forcedAccepted: false,
            forcedReason: "no-book-toc-cluster");
        yield return Candidate("auto:rfc-toc-dictionary", Array.Empty<DocxPolicyParagraph>(), false, state,
            bodyAnchorRatio: 0, tocCoverage: null, forcedAccepted: false,
            forcedReason: "không có cụm TOC dày, sớm và gọn");
    }

    private static HeadingRecord[] TypedHeadings(IReadOnlyList<DocxPolicyParagraph> paragraphs)
    {
        var result = new List<HeadingRecord>();
        var seen = new HashSet<(int Index, string Text)>();
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Corrupt || paragraph.TableDepth > 0 || paragraph.InTableOfContents ||
                string.IsNullOrWhiteSpace(paragraph.Text)) continue;

            var segments = ParagraphHeadingSplitter.Segments(paragraph.Text);
            if (TypedNumberingOutline.LooksLikeDenseTypedTableOfContents(paragraph.Text, segments))
                continue;
            foreach (var segment in segments)
            {
                if (NumberingAudit.Parse(TypedNumberingOutline.StripPageArtifacts(segment)) is not { } token)
                    continue;
                var heading = TypedNumberingOutline.StripPageArtifacts(segment).Trim();
                if (TypedNumberingOutline.LooksLikeTextLayoutPageHeader(heading) ||
                    TypedNumberingOutline.LooksLikeCaptionLabel(token) ||
                    TypedNumberingOutline.HasZeroArabicPathComponent(token, heading) ||
                    TypedNumberingOutline.LooksLikeNumericMeasurement(token, heading) ||
                    TypedNumberingOutline.LooksLikeQuantitativeAmount(token, heading) ||
                    TypedNumberingOutline.LooksLikeQuantitativeTableRow(token, heading))
                    continue;
                if (!seen.Add((paragraph.Index, heading))) continue;
                result.Add(new HeadingRecord
                {
                    Index = paragraph.Index,
                    StableId = paragraph.StableId,
                    SourceId = paragraph.StableId,
                    Level = Math.Clamp(token.Depth, 1, 9),
                    Text = heading,
                    OriginalText = paragraph.Text,
                    StyleId = paragraph.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 1,
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                    ConfidenceBasis = "typed_number_depth",
                });
            }
        }
        return result.ToArray();
    }

    private static OutlineCandidateDiagnostic Candidate(string route, IEnumerable<DocxPolicyParagraph> items,
        bool styleTrustRequired, DocxPolicyState state, double? bodyAnchorRatio = null,
        double? tocCoverage = null, bool? forcedAccepted = null, string? forcedReason = null)
    {
        var headings = items.Select(p => new HeadingRecord
        {
            Index = p.Index, StableId = p.StableId, SourceId = p.StableId,
            Level = p.NumberingStyleHeadingLevel ?? OutlineEvidenceLevel(p) ?? p.GuessedLevel ?? 1,
            Text = p.Text, OriginalText = p.Text, HeadingSpan = new TextOffsetSpan(0, p.Text.Length),
            BoundarySource = "diagnostic-source-native", StyleId = p.StyleId, Source = HeadingSource.Structure,
            Confidence = 1, DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence, ConfidenceBasis = route,
        }).ToArray();
        return Candidate(route, headings, styleTrustRequired, state, bodyAnchorRatio, tocCoverage, forcedAccepted, forcedReason);
    }

    private static int? OutlineEvidenceLevel(DocxPolicyParagraph paragraph) =>
        paragraph.OutlineLevel is >= 0 and <= 8
            ? paragraph.OutlineLevel.Value + 1
            : paragraph.Style.BuiltInHeadingStyleLevel is >= 1 and <= 9
                ? paragraph.Style.BuiltInHeadingStyleLevel
                : null;

    private static OutlineCandidateDiagnostic Candidate(string route, IReadOnlyList<HeadingRecord> headings,
        bool styleTrustRequired, DocxPolicyState state, double? bodyAnchorRatio = null,
        double? tocCoverage = null, bool? forcedAccepted = null, string? forcedReason = null)
    {
        var duplicateRate = headings.Count == 0 ? 0 : (double)(headings.Count - headings.Select(h => (h.Index, h.Text.Trim())).Distinct().Count()) / headings.Count;
        var pollutionRate = TitlePollutionRate(headings);
        var ordered = headings.OrderBy(h => h.Index).ToArray();
        var jumps = ordered.Length <= 1 ? 0 : Enumerable.Range(1, ordered.Length - 1).Count(i => (ordered[i].Level ?? 0) - (ordered[i - 1].Level ?? 0) > 1);
        var jumpRate = ordered.Length <= 1 ? 0 : (double)jumps / (ordered.Length - 1);
        var rejected = styleTrustRequired && state.StyleTrust is { SelectionTrusted: false };
        var accepted = forcedAccepted ?? (headings.Count > 0 && !rejected && duplicateRate <= .02 && pollutionRate <= .05 && jumpRate <= .25);
        var reason = forcedReason ?? Reason(headings.Count, rejected, duplicateRate, pollutionRate, jumpRate, accepted);
        return new OutlineCandidateDiagnostic(route, accepted, reason, headings.Count, duplicateRate, pollutionRate, jumpRate, bodyAnchorRatio, tocCoverage);
    }

    private static string Reason(int count, bool styleRejected, double duplicateRate, double pollutionRate,
        double jumpRate, bool accepted)
    {
        if (accepted) return "accepted";
        if (count == 0) return "no_headings";
        if (styleRejected) return "style_selection_untrusted";
        if (duplicateRate > .02) return "duplicate_heading_rate_high";
        if (pollutionRate > .05) return "title_pollution_high";
        if (jumpRate > .25) return "level_jump_rate_high";
        return "weak_internal_validation";
    }

    private static double TitlePollutionRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var polluted = headings.Count(h =>
        {
            var text = (h.Text ?? string.Empty).Trim();
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return text.Length > 180 || text.Count(c => c is '.' or ';') >= 4 || words.Length >= 24 ||
                   (words.Length >= 14 && text.EndsWith('.') && !LooksLikeNumberedLabel(text));
        });
        return (double)polluted / headings.Count;
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
