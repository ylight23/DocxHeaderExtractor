using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Language-neutral PDF navigation-outline extractor. It learns the body baseline and visual
/// outliers from the current PDF, removes repeated/table-like lines, groups nearby lines into
/// blocks, and grounds accepted blocks back to the DOCX source. It intentionally abstains when
/// visual candidates are dense: a deep content/table index is not a navigation outline.
/// </summary>
public static class PdfLayoutEvidenceOutline
{
    // These are deliberately not deterministic-declared `const Basis` values: both routes are
    // experimental until their precision is measured against independent keys.
    public static readonly string Basis = "pdf_layout_evidence";
    public static readonly string AnalystBasis = "pdf_layout_block_grounded";
    private const int MaximumAnalystBlocks = 40;

    public static PdfTextbookOutlineResult TryBuild(string originalInputPath, SlimDocument slim)
    {
        var context = TryBuildContext(originalInputPath, slim, out var reason);
        if (context is null) return PdfTextbookOutlineResult.NotApplicable(reason);

        var alignment = AlignToDocx(context.Candidates, slim, context.Profile, Basis);
        if (alignment.Headings.Count < Math.Max(3, (int)Math.Ceiling(context.Candidates.Count * 0.65)))
            return PdfTextbookOutlineResult.NotApplicable($"low-docx-alignment:{alignment.Headings.Count}/{context.Candidates.Count}");

        return new PdfTextbookOutlineResult(
            alignment.Headings,
            $"pdf={Path.GetFileName(context.Pdf)}, styles={context.HeadingStyles.Count}, aligned={alignment.Headings.Count}/{context.Candidates.Count}");
    }

    /// <summary>
    /// Slow lane: only already-filtered visual blocks are sent to the language model. The returned
    /// roles are grounded back to the same blocks before DOCX alignment; the model cannot invent a
    /// title or a source span.
    /// </summary>
    public static async Task<PdfTextbookOutlineResult> TryBuildWithAnalystAsync(
        string originalInputPath,
        SlimDocument slim,
        IHeaderClassifier analyst,
        CancellationToken ct = default)
    {
        var context = TryBuildContext(originalInputPath, slim, out var reason);
        if (context is null) return PdfTextbookOutlineResult.NotApplicable(reason);

        var selection = SelectAnalystCandidates(context.Candidates, MaximumAnalystBlocks);
        var candidates = selection.Selected;
        var excluded = context.Annotations.Where(a => a.ExcludeFromSemanticSamples).Select(a => a.Line).ToHashSet();
        var samples = PdfSemanticClusterAnalyst.BuildSamples(context.Profile, context.Lines, excluded);
        var clusters = await PdfSemanticClusterAnalyst.AnalyzeAsync(analyst, context.Profile, context.Lines, ct);
        var blockAnalysis = await PdfBlockAnalyst.AnalyzeAsync(analyst, candidates, ct);
        var grounded = PdfBlockGrounder.Ground(candidates, blockAnalysis.Decisions, context.Profile, samples, clusters.Decisions);
        var acceptedIds = grounded.Headings.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var accepted = candidates.Where(b => acceptedIds.Contains(b.Id)).ToArray();
        if (accepted.Length < 3)
            return PdfTextbookOutlineResult.NotApplicable($"analyst-grounded-too-few:{accepted.Length}/{candidates.Count}");

        var alignment = AlignToDocx(accepted, slim, context.Profile, AnalystBasis);
        if (alignment.Headings.Count < Math.Max(3, (int)Math.Ceiling(accepted.Length * 0.65)))
            return PdfTextbookOutlineResult.NotApplicable($"analyst-low-docx-alignment:{alignment.Headings.Count}/{accepted.Length}");

        var summary = $"pdf={Path.GetFileName(context.Pdf)}, candidateBlocks={candidates.Count}/{selection.Available}, " +
                      $"pages={selection.SelectedPages}/{selection.AvailablePages}, grounded={accepted.Length}, aligned={alignment.Headings.Count}/{accepted.Length}";
        return new PdfTextbookOutlineResult(
            alignment.Headings,
            summary,
            new RouteExecutionAudit(
                summary,
                selection.Available,
                candidates.Count,
                selection.AvailablePages,
                selection.SelectedPages,
                context.Candidates.Select(ToAudit).ToArray(),
                candidates.Select(ToAudit).ToArray(),
                context.Candidates.Where(b => !candidates.Any(selected => selected.Id == b.Id)).Select(ToAudit).ToArray(),
                blockAnalysis.Decisions.Select(d => new RouteBlockDecisionAudit(d.Id, d.Role.ToString(), d.Confidence)).ToArray(),
                accepted.Select(b => b.Id).ToArray(),
                grounded.Rejected.Select(r => new RouteBlockRejectionAudit(r.Id, r.Role, r.Confidence, r.Reason)).ToArray(),
                alignment.AlignedBlockIds.ToArray()));
    }

    /// <summary>
    /// Bounded analyst work must cover the document before spending a second slot on an earlier
    /// page. Taking the first N blocks systematically hid late chapters in long PDFs.
    /// </summary>
    internal static PdfAnalystCandidateSelection SelectAnalystCandidates(
        IReadOnlyList<PdfSemanticBlock> candidates,
        int maximum)
    {
        var ordered = candidates
            .OrderBy(b => b.Page)
            .ThenByDescending(b => b.TopY)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();
        var byPage = ordered
            .GroupBy(b => b.Page)
            .Select(g => g.ToArray())
            .ToArray();
        if (maximum <= 0 || ordered.Length == 0)
            return new PdfAnalystCandidateSelection([], ordered.Length, byPage.Length, 0);
        if (ordered.Length <= maximum)
            return new PdfAnalystCandidateSelection(ordered, ordered.Length, byPage.Length, byPage.Length);

        var selected = new List<PdfSemanticBlock>(maximum);
        for (var slot = 0; selected.Count < maximum; slot++)
        {
            var added = false;
            foreach (var page in byPage)
            {
                if (slot >= page.Length) continue;
                selected.Add(page[slot]);
                added = true;
                if (selected.Count == maximum) break;
            }

            if (!added) break;
        }

        return new PdfAnalystCandidateSelection(
            selected,
            ordered.Length,
            byPage.Length,
            selected.Select(b => b.Page).Distinct().Count());
    }

    private static LayoutContext? TryBuildContext(string originalInputPath, SlimDocument slim, out string reason)
    {
        reason = "";
        if (DocumentStructureEvidence.HasNativeSemanticStructure(slim)) { reason = "docx-structure-present"; return null; }
        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null) { reason = "no-pdf"; return null; }

        IReadOnlyList<PdfLine> lines;
        try
        {
            using var document = PdfDocument.Open(pdf);
            lines = PdfLineExtraction.ExtractLines(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            reason = "pdf-read-failed";
            return null;
        }

        var annotations = PdfLineBlockFilter.Analyze(lines);
        var semanticLines = annotations.Where(a => !a.ExcludeFromSemanticSamples).Select(a => a.Line).ToList();
        if (semanticLines.Count < 3) { reason = "too-few-semantic-lines"; return null; }
        var profile = PdfStyleClusterProfile.Learn(semanticLines);
        var headingStyles = SelectNavigationStyles(profile);
        if (headingStyles.Count == 0) { reason = "no-sparse-visual-style"; return null; }
        var candidates = PdfSemanticBlockGrouper.Build(annotations)
            .Where(b => headingStyles.Contains(b.PrimaryStyle) && LooksLikeTopicBlock(b))
            .OrderBy(b => b.Page).ThenByDescending(b => b.TopY).ToList();
        var pages = Math.Max(1, lines.Select(l => l.Page).Distinct().Count());
        if (candidates.Count < 3) { reason = $"too-few-layout-blocks:{candidates.Count}"; return null; }
        if (candidates.Count > pages * 2) { reason = $"layout-candidates-too-dense:{candidates.Count}/{pages}"; return null; }
        return new LayoutContext(pdf, lines, annotations, profile, headingStyles, candidates);
    }

    private static HashSet<PdfStyleKey> SelectNavigationStyles(PdfStyleClusterProfile profile)
    {
        var body = profile.Clusters.FirstOrDefault(c => c.Style == profile.BodyStyle);
        if (body is null || body.Lines == 0) return [];
        var bodyAverageLength = body.Characters / (double)body.Lines;
        var pageCount = Math.Max(1, profile.Clusters.Max(c => c.Pages));

        return profile.Clusters
            .Where(c => c.Style != profile.BodyStyle && profile.CandidateStyles.Contains(c.Style))
            .Where(c => c.Lines <= pageCount * 1.5)
            .Where(c => c.Characters / (double)Math.Max(1, c.Lines) <= Math.Max(120, bodyAverageLength * 0.85))
            .Where(c => IsVisuallyDistinct(c.Style, profile.BodyStyle))
            .Select(c => c.Style)
            .ToHashSet();
    }

    private static bool IsVisuallyDistinct(PdfStyleKey candidate, PdfStyleKey body) =>
        candidate.FontSizeBucket >= body.FontSizeBucket + 0.5 ||
        !string.Equals(candidate.FontName, body.FontName, StringComparison.Ordinal) ||
        !string.Equals(candidate.FillColorKey, body.FillColorKey, StringComparison.Ordinal);

    private static bool LooksLikeTopicBlock(PdfSemanticBlock block)
    {
        var text = block.DisplayText;
        if (text.Length is < 3 or > 160 || !text.Any(char.IsLetter)) return false;
        if (text.Count(char.IsDigit) > text.Length * 0.25) return false;
        if (block.LineCount > 3) return false;
        if (text.Length >= 70 && Regex.IsMatch(text, @"[.!?]\s*$")) return false;
        if (text.Count(c => c is '.' or ';') >= 2) return false;
        return true;
    }

    private static PdfLayoutAlignmentResult AlignToDocx(
        IReadOnlyList<PdfSemanticBlock> candidates,
        SlimDocument slim,
        PdfStyleClusterProfile profile,
        string confidenceBasis)
    {
        var paragraphs = slim.Paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !p.InTableOfContents && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .Select(p => new CanonParagraph(p, CanonicalMap(p.Text)))
            .Where(p => p.Map.Text.Length > 0)
            .ToList();
        var styles = candidates.Select(b => b.PrimaryStyle).Distinct()
            .OrderByDescending(s => s.FontSizeBucket)
            .ThenBy(s => s.FontName, StringComparer.Ordinal)
            .ThenBy(s => s.FillColorKey, StringComparer.Ordinal)
            .Select((style, index) => (style, level: index + 1))
            .ToDictionary(x => x.style, x => x.level);

        var result = new List<HeadingRecord>();
        var alignedBlockIds = new HashSet<string>(StringComparer.Ordinal);
        // PDF blocks arrive in page order. Keep an occurrence occupied only for the same visual
        // style: a repeated page title must advance to the next DOCX page blob, while a group label
        // and its title may legitimately share one source span when they have different PDF styles.
        var seen = new HashSet<(int Index, int Start, PdfStyleKey Style)>();
        var occupiedSpans = new HashSet<(int Index, int Start)>();
        var cursor = 0;
        foreach (var block in candidates)
        {
            var needle = block.CanonicalText;
            if (needle.Length < 4) continue;
            var match = FindMatch(paragraphs, needle, cursor, block.PrimaryStyle, seen, occupiedSpans, requireFreshSpan: true) ??
                        FindMatch(paragraphs, needle, 0, block.PrimaryStyle, seen, occupiedSpans, requireFreshSpan: true) ??
                        FindMatch(paragraphs, needle, cursor, block.PrimaryStyle, seen, occupiedSpans, requireFreshSpan: false) ??
                        FindMatch(paragraphs, needle, 0, block.PrimaryStyle, seen, occupiedSpans, requireFreshSpan: false);
            if (match is null) continue;
            if (!seen.Add((match.Value.Paragraph.Index, match.Value.Start, block.PrimaryStyle))) continue;
            occupiedSpans.Add((match.Value.Paragraph.Index, match.Value.Start));

            result.Add(new HeadingRecord
            {
                Index = match.Value.Paragraph.Index,
                StableId = match.Value.Paragraph.StableId,
                Level = styles[block.PrimaryStyle],
                Text = block.DisplayText,
                OriginalText = match.Value.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(match.Value.Start, match.Value.End),
                BoundarySource = "pdf-layout-evidence",
                StyleId = match.Value.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.90,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = confidenceBasis,
            });
            alignedBlockIds.Add(block.Id);
            cursor = match.Value.Paragraph.Index;
        }

        return new PdfLayoutAlignmentResult(
            result.OrderBy(h => h.Index).ThenBy(h => h.HeadingSpan?.Start ?? 0).ToList(),
            alignedBlockIds);
    }

    private static MatchResult? FindMatch(
        IReadOnlyList<CanonParagraph> paragraphs,
        string needle,
        int minimumIndex,
        PdfStyleKey style,
        IReadOnlySet<(int Index, int Start, PdfStyleKey Style)> occupied,
        IReadOnlySet<(int Index, int Start)> occupiedSpans,
        bool requireFreshSpan)
    {
        foreach (var paragraph in paragraphs.Where(p => p.Paragraph.Index >= minimumIndex))
        {
            var offset = 0;
            while (offset <= paragraph.Map.Text.Length - needle.Length)
            {
                var at = paragraph.Map.Text.IndexOf(needle, offset, StringComparison.Ordinal);
                if (at < 0) break;
                var start = paragraph.Map.SourceIndexes[at];
                var sameStyleOccupied = occupied.Contains((paragraph.Paragraph.Index, start, style));
                var anyStyleOccupied = occupiedSpans.Contains((paragraph.Paragraph.Index, start));
                if (!sameStyleOccupied && (!requireFreshSpan || !anyStyleOccupied))
                {
                    return new MatchResult(
                        paragraph.Paragraph,
                        start,
                        paragraph.Map.SourceIndexes[at + needle.Length - 1] + 1);
                }

                offset = at + 1;
            }
        }

        return null;
    }

    private static CanonMap CanonicalMap(string text)
    {
        var canonical = new StringBuilder(text.Length);
        var indexes = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetterOrDigit(text[i])) continue;
            canonical.Append(char.ToLowerInvariant(text[i]));
            indexes.Add(i);
        }
        return new CanonMap(canonical.ToString(), indexes);
    }

    private sealed record CanonMap(string Text, IReadOnlyList<int> SourceIndexes);
    private sealed record CanonParagraph(SlimParagraph Paragraph, CanonMap Map);
    private readonly record struct MatchResult(SlimParagraph Paragraph, int Start, int End);
    private sealed record PdfLayoutAlignmentResult(
        IReadOnlyList<HeadingRecord> Headings,
        IReadOnlySet<string> AlignedBlockIds);
    private sealed record LayoutContext(
        string Pdf,
        IReadOnlyList<PdfLine> Lines,
        IReadOnlyList<PdfLineBlockAnnotation> Annotations,
        PdfStyleClusterProfile Profile,
        IReadOnlySet<PdfStyleKey> HeadingStyles,
        IReadOnlyList<PdfSemanticBlock> Candidates);

    private static RouteBlockAudit ToAudit(PdfSemanticBlock block) =>
        new(block.Id, block.Page, block.DisplayText);
}

internal sealed record PdfAnalystCandidateSelection(
    IReadOnlyList<PdfSemanticBlock> Selected,
    int Available,
    int AvailablePages,
    int SelectedPages);
