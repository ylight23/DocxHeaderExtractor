using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Key-guided diagnostic only; never participates in PDF candidate selection.</summary>
public sealed record PdfCandidateRetrievalTrace(
    string ExpectedText,
    bool FoundInRawWindow,
    IReadOnlyList<string> RawFilterReasons,
    bool FoundInStandardBlock,
    bool FoundInBroadCandidate,
    bool FoundInWideCandidate,
    bool FoundInSupplementCandidate,
    string FirstLossStage,
    string? RawWindowText);

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

    /// <summary>
    /// Traces a known title through the PDF retrieval pipeline. This is deliberately key-guided
    /// observability, not a key-guided extraction rule: callers use it to discover general losses.
    /// </summary>
    public static IReadOnlyList<PdfCandidateRetrievalTrace> TraceCandidateRetrieval(
        string originalInputPath,
        IEnumerable<string> expectedTitles)
    {
        var expected = expectedTitles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0) return [];
        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null) return expected.Select(t => new PdfCandidateRetrievalTrace(
            t, false, [], false, false, false, false, "no-sibling-pdf", null)).ToArray();

        try
        {
            using var document = PdfDocument.Open(pdf);
            var annotations = PdfLineBlockFilter.Analyze(PdfLineExtraction.ExtractLines(document));
            var standard = PdfSemanticBlockGrouper.Build(annotations);
            var profile = PdfStyleClusterProfile.Learn(annotations.Where(a => !a.ExcludeFromSemanticSamples).Select(a => a.Line).ToArray());
            var broad = BuildBroadCandidates(standard, profile);
            var wide = BuildWideAuditCandidates(standard);
            var supplement = BuildSupplementCandidates(annotations, broad);
            return expected.Select(title => TraceTitle(title, annotations, standard, broad, wide, supplement)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return expected.Select(t => new PdfCandidateRetrievalTrace(
                t, false, [], false, false, false, false, "pdf-read-failed", null)).ToArray();
        }
    }

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
    /// Audit-only PDF-first lane. It deliberately starts from the broader candidate generator used
    /// by <c>pdf-clusters</c>, rather than the sparse-style production experiment above. DOCX is
    /// only used after PDF selection to map an accepted PDF block to a stable writeback span.
    /// This method is intentionally not called by <see cref="HeaderExtractionPipeline"/>.
    /// </summary>
    public static async Task<PdfTextbookOutlineResult> TryBuildBroadAuditWithAnalystAsync(
        string originalInputPath,
        SlimDocument slim,
        IHeaderClassifier analyst,
        int maximumAnalystBlocks = MaximumAnalystBlocks,
        bool includeAllVisualStyles = false,
        bool includeSupplementCandidates = false,
        CancellationToken ct = default)
    {
        if (maximumAnalystBlocks < 0)
            return PdfTextbookOutlineResult.NotApplicable("invalid-analyst-block-budget");

        var context = TryBuildBroadAuditContext(
            originalInputPath, includeAllVisualStyles, includeSupplementCandidates, out var reason);
        if (context is null) return PdfTextbookOutlineResult.NotApplicable(reason);

        var priorityIds = context.PriorityCandidateIds.Count > 0 ? context.PriorityCandidateIds : null;
        var effectiveBudget = maximumAnalystBlocks == 0 ? context.Candidates.Count : maximumAnalystBlocks;
        var selection = SelectAnalystCandidates(
            context.Candidates, effectiveBudget, priorityIds, context.SupplementCandidateRanks);
        var selected = selection.Selected;
        var excluded = context.Annotations.Where(a => a.ExcludeFromSemanticSamples).Select(a => a.Line).ToHashSet();
        var samples = PdfSemanticClusterAnalyst.BuildSamples(context.Profile, context.Lines, excluded);
        var clusters = await PdfSemanticClusterAnalyst.AnalyzeAsync(analyst, context.Profile, context.Lines, ct);
        var blockAnalysis = await PdfBlockAnalyst.AnalyzeAsync(analyst, selected, ct);
        var grounded = PdfBlockGrounder.Ground(selected, blockAnalysis.Decisions, context.Profile, samples, clusters.Decisions);
        var acceptedIds = grounded.Headings.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var accepted = selected.Where(b => acceptedIds.Contains(b.Id)).ToArray();
        var alignment = AlignToDocx(accepted, slim, context.Profile, AnalystBasis);

        var lane = includeAllVisualStyles ? "wide" : "broad";
        if (includeSupplementCandidates) lane += "+supplement";
        var summary = $"audit-only {lane} PDF lane; pdf={Path.GetFileName(context.Pdf)}, candidateBlocks={selected.Count}/{selection.Available}, " +
                      $"pages={selection.SelectedPages}/{selection.AvailablePages}, grounded={accepted.Length}, aligned={alignment.Headings.Count}/{accepted.Length}";
        var audit = new RouteExecutionAudit(
            summary,
            selection.Available,
            selected.Count,
            selection.AvailablePages,
            selection.SelectedPages,
            context.Candidates.Select(ToAudit).ToArray(),
            selected.Select(ToAudit).ToArray(),
            context.Candidates.Where(b => !selected.Any(choice => choice.Id == b.Id)).Select(ToAudit).ToArray(),
            blockAnalysis.Decisions.Select(d => new RouteBlockDecisionAudit(d.Id, d.Role.ToString(), d.Confidence)).ToArray(),
            accepted.Select(b => b.Id).ToArray(),
            grounded.Rejected.Select(r => new RouteBlockRejectionAudit(r.Id, r.Role, r.Confidence, r.Reason)).ToArray(),
            alignment.AlignedBlockIds.ToArray())
        {
            RawAnalystResponses = blockAnalysis.RawResponses,
        };

        // Audit must preserve partial output and every loss even when the production acceptance
        // thresholds would abstain. Otherwise the stage that lost a key title is unobservable.
        var auditReason = accepted.Length < 3
            ? $"audit-only:analyst-grounded-too-few:{accepted.Length}/{selected.Count}"
            : alignment.Headings.Count < Math.Max(3, (int)Math.Ceiling(accepted.Length * 0.65))
                ? $"audit-only:analyst-low-docx-alignment:{alignment.Headings.Count}/{accepted.Length}"
                : summary;
        return new PdfTextbookOutlineResult(alignment.Headings, auditReason, audit);
    }

    /// <summary>
    /// Shared PDF-first broad candidate generator. It keeps PDF line filtering and title-shape
    /// safeguards, but does not require a sparse visual style: semantic selection belongs to the
    /// analyst and grounding stages that follow.
    /// </summary>
    internal static IReadOnlyList<PdfSemanticBlock> BuildBroadCandidates(
        IReadOnlyList<PdfSemanticBlock> blocks,
        PdfStyleClusterProfile profile) =>
        blocks.Where(b => profile.CandidateStyles.Contains(b.PrimaryStyle) && LooksLikeBroadCandidateBlock(b))
            .OrderBy(b => b.Page)
            .ThenByDescending(b => b.TopY)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Wide audit candidate generation deliberately removes learned style as a precondition. It is
    /// for measuring whether an LLM can supply semantics for blocks that layout clustering missed;
    /// it must not be used by production routing without independent precision evidence.
    /// </summary>
    internal static IReadOnlyList<PdfSemanticBlock> BuildWideAuditCandidates(IReadOnlyList<PdfSemanticBlock> blocks) =>
        blocks.Where(LooksLikeWideAuditBlock)
            .OrderBy(b => b.Page)
            .ThenByDescending(b => b.TopY)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Builds a second, longer grouping over the same filtered PDF lines. It is a retrieval repair:
    /// no DOCX text is introduced and the LLM can only classify a recovered PDF block.
    /// </summary>
    internal static IReadOnlyList<PdfSemanticBlock> BuildSupplementCandidates(
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        IReadOnlyList<PdfSemanticBlock> existing)
    {
        var existingCanonical = existing.Select(b => b.CanonicalText).ToHashSet(StringComparer.Ordinal);
        var atomic = annotations
            .Where(a => !a.ExcludeFromSemanticSamples)
            .Select((a, index) => new PdfSemanticBlock(
                $"s-line-{index + 1}", [a.Line], PdfStyleClusterProfile.StyleOf(a.Line), a.Line.Page,
                a.Line.Y, a.Line.Y, a.Line.Left, a.Line.Right, PdfTextUtilities.Readable(a.Line.Text)))
            .Where(LooksLikeSupplementBlock);
        var loose = PdfSemanticBlockGrouper.Build(annotations, maxLinesPerBlock: 12, allowSemicolonContinuation: true)
            .Where(LooksLikeSupplementBlock)
            .Where(b => !existingCanonical.Contains(b.CanonicalText))
            .Select((b, index) => b with { Id = $"s-block-{index + 1}" });
        var fragments = BuildAdjacentFragmentWindows(annotations).Where(LooksLikeSupplementBlock);
        var seen = new HashSet<string>(existingCanonical, StringComparer.Ordinal);
        return atomic.Concat(loose).Concat(fragments)
            .Where(b => seen.Add(b.CanonicalText))
            .ToArray();
    }

    private static IEnumerable<PdfSemanticBlock> BuildAdjacentFragmentWindows(
        IReadOnlyList<PdfLineBlockAnnotation> annotations)
    {
        var id = 1;
        foreach (var page in annotations.Where(a => !a.ExcludeFromSemanticSamples)
                     .OrderBy(a => a.Line.Page).ThenByDescending(a => a.Line.Y).ThenBy(a => a.Line.Left)
                     .GroupBy(a => a.Line.Page))
        {
            var lines = page.ToArray();
            for (var start = 0; start < lines.Length; start++)
            {
                var window = new List<PdfLine> { lines[start].Line };
                for (var offset = 1; offset < 4 && start + offset < lines.Length; offset++)
                {
                    var next = lines[start + offset].Line;
                    if (window[^1].Y - next.Y is <= 0 or > 34) break;
                    window.Add(next);
                    yield return CreateFragmentBlock($"s-window-{id++}", window);
                }
            }
        }
    }

    private static PdfSemanticBlock CreateFragmentBlock(string id, IReadOnlyList<PdfLine> lines)
    {
        var primary = lines.GroupBy(line => PdfStyleClusterProfile.StyleOf(line))
            .OrderByDescending(g => g.Sum(line => PdfTextUtilities.Readable(line.Text).Length))
            .Select(g => g.Key).First();
        return new PdfSemanticBlock(
            id, lines, primary, lines[0].Page, lines.Max(line => line.Y), lines.Min(line => line.Y),
            lines.Min(line => line.Left), lines.Max(line => line.Right),
            PdfTextUtilities.Readable(string.Join(" ", lines.Select(line => line.Text))));
    }

    /// <summary>
    /// Bounded analyst work must cover the document before spending a second slot on an earlier
    /// page. Taking the first N blocks systematically hid late chapters in long PDFs.
    /// </summary>
    internal static PdfAnalystCandidateSelection SelectAnalystCandidates(
        IReadOnlyList<PdfSemanticBlock> candidates,
        int maximum,
        IReadOnlySet<string>? priorityIds = null,
        IReadOnlyDictionary<string, int>? supplementalRanks = null)
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

        var selected = priorityIds is { Count: > 0 }
            ? SelectAcrossPages(ordered.Where(b => priorityIds.Contains(b.Id)).ToArray(), maximum)
            : [];
        if (selected.Count < maximum)
        {
            var selectedIds = selected.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            if (supplementalRanks is { Count: > 0 })
            {
                foreach (var rank in supplementalRanks.Values.Distinct().OrderDescending())
                {
                    if (selected.Count == maximum) break;
                    var tier = ordered.Where(b =>
                        !selectedIds.Contains(b.Id) &&
                        supplementalRanks.TryGetValue(b.Id, out var candidateRank) &&
                        candidateRank == rank).ToArray();
                    var picked = SelectAcrossPages(tier, maximum - selected.Count);
                    selected.AddRange(picked);
                    selectedIds.UnionWith(picked.Select(b => b.Id));
                }
            }

            if (selected.Count < maximum)
                selected.AddRange(SelectAcrossPages(
                    ordered.Where(b => !selectedIds.Contains(b.Id)).ToArray(), maximum - selected.Count));
        }

        return new PdfAnalystCandidateSelection(
            selected.OrderBy(b => b.Page).ThenByDescending(b => b.TopY).ThenBy(b => b.Id, StringComparer.Ordinal).ToArray(),
            ordered.Length,
            byPage.Length,
            selected.Select(b => b.Page).Distinct().Count());
    }

    private static List<PdfSemanticBlock> SelectAcrossPages(
        IReadOnlyList<PdfSemanticBlock> ordered,
        int maximum)
    {
        if (maximum <= 0 || ordered.Count == 0) return [];
        if (ordered.Count <= maximum) return ordered.ToList();

        var byPage = ordered
            .GroupBy(b => b.Page)
            .Select(g => g.ToArray())
            .ToArray();
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
        return selected;
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
        return new LayoutContext(
            pdf, lines, annotations, profile, headingStyles, candidates,
            new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal));
    }

    private static LayoutContext? TryBuildBroadAuditContext(
        string originalInputPath,
        bool includeAllVisualStyles,
        bool includeSupplementCandidates,
        out string reason)
    {
        reason = "";
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
        var blocks = PdfSemanticBlockGrouper.Build(annotations);
        var broadCandidates = BuildBroadCandidates(blocks, profile);
        var primaryCandidates = includeAllVisualStyles
            ? BuildWideAuditCandidates(blocks)
            : broadCandidates;
        var supplemental = includeSupplementCandidates
            ? BuildSupplementCandidates(annotations, primaryCandidates)
            : Array.Empty<PdfSemanticBlock>();
        var candidates = includeSupplementCandidates
            ? MergeCandidateSets(primaryCandidates, supplemental)
            : primaryCandidates;
        if (candidates.Count == 0) { reason = "no-broad-layout-blocks"; return null; }
        // Broad candidates are measured seeds. Supplemental blocks are intentionally a second
        // retrieval tier: selecting all of them with equal page priority would crowd seeds out.
        var priorityIds = includeAllVisualStyles || includeSupplementCandidates
            ? broadCandidates.Select(b => b.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var supplementRanks = supplemental
            .Where(block => candidates.Any(candidate => candidate.Id == block.Id))
            .ToDictionary(block => block.Id, ScoreSupplementForAnalyst, StringComparer.Ordinal);
        return new LayoutContext(
            pdf, lines, annotations, profile, profile.CandidateStyles, candidates, priorityIds, supplementRanks);
    }

    /// <summary>
    /// Orders lossless retrieval candidates for bounded analyst attention. This is intentionally
    /// only a ranking signal: a low-scoring block remains available as a later fallback.
    /// </summary>
    internal static int ScoreSupplementForAnalyst(PdfSemanticBlock block)
    {
        var text = block.DisplayText.Trim();
        var score = 0;
        if (NumberingAudit.Parse(text) is not null) score += 100;
        if (block.LineCount is >= 2 and <= 4) score += 12;
        if (text.Length is >= 4 and <= 180) score += 8;

        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length >= 4 && letters.Count(char.IsUpper) / (double)letters.Length >= 0.55)
            score += 40;
        if (!text.EndsWith('.') && !text.EndsWith(';')) score += 4;
        return score;
    }

    private static IReadOnlyList<PdfSemanticBlock> MergeCandidateSets(
        IReadOnlyList<PdfSemanticBlock> primary,
        IReadOnlyList<PdfSemanticBlock> supplemental)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return primary.Concat(supplemental)
            .Where(b => seen.Add(b.CanonicalText))
            .OrderBy(b => b.Page).ThenByDescending(b => b.TopY).ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();
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

    private static bool LooksLikeBroadCandidateBlock(PdfSemanticBlock block)
    {
        var text = block.DisplayText.Trim();
        if (text.Length is < 3 or > 180 || !text.Any(char.IsLetter)) return false;
        if (block.LineCount > 3 && text.Length > 120) return false;
        if (text.Count(c => c is '.' or ';') >= 2) return false;
        if (text.Length >= 80 && text.EndsWith('.')) return false;
        if (text.Length >= 40 && Regex.IsMatch(text, @"^(?:\d{1,3}|\*)\s+\S")) return false;
        if (LooksLikeSpacedLogoFragment(text)) return false;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 24;
    }

    private static bool LooksLikeWideAuditBlock(PdfSemanticBlock block)
    {
        var text = block.DisplayText.Trim();
        if (text.Length is < 3 or > 320 || !text.Any(char.IsLetter)) return false;
        if (block.LineCount > 8) return false;
        if (LooksLikeSpacedLogoFragment(text)) return false;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 56;
    }

    private static bool LooksLikeSupplementBlock(PdfSemanticBlock block)
    {
        var text = block.DisplayText.Trim();
        if (text.Length is < 3 or > 900 || !text.Any(char.IsLetter) || block.LineCount > 12) return false;
        if (LooksLikeSpacedLogoFragment(text)) return false;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 140) return false;
        if (NumberingAudit.Parse(text) is not null) return true;
        if (Regex.IsMatch(text, @"^\s*(?:chapter|chương|section|article|điều)\b", RegexOptions.IgnoreCase)) return true;
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length >= 4 && letters.Count(char.IsUpper) / (double)letters.Length >= 0.55;
    }

    private static PdfCandidateRetrievalTrace TraceTitle(
        string expected,
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        IReadOnlyList<PdfSemanticBlock> standard,
        IReadOnlyList<PdfSemanticBlock> broad,
        IReadOnlyList<PdfSemanticBlock> wide,
        IReadOnlyList<PdfSemanticBlock> supplement)
    {
        var target = PdfTextUtilities.CanonicalForMatch(expected);
        var rawWindow = FindRawWindow(annotations, target);
        var foundRaw = rawWindow.Count > 0;
        var relevantLines = rawWindow
            .Where(a =>
            {
                var line = PdfTextUtilities.CanonicalForMatch(a.Line.Text);
                return line.Length >= 4 && (target.Contains(line, StringComparison.Ordinal) || line.Contains(target, StringComparison.Ordinal));
            })
            .ToArray();
        if (relevantLines.Length == 0) relevantLines = rawWindow.ToArray();
        var reasons = relevantLines.Select(a => a.Reason).Where(r => r != "semantic-candidate").Distinct().ToArray();
        bool Contains(IReadOnlyList<PdfSemanticBlock> blocks) => blocks.Any(b => b.CanonicalText.Contains(target, StringComparison.Ordinal));
        var inStandard = Contains(standard);
        var inBroad = Contains(broad);
        var inWide = Contains(wide);
        var inSupplement = Contains(supplement);
        var firstLoss = !foundRaw ? "absent-from-raw-windows"
            : reasons.Length > 0 ? "line-filtered:" + string.Join(",", reasons)
            : !inStandard ? "semantic-block-grouping"
            : !inBroad ? "broad-style-or-shape-gate"
            : "candidate-available";
        var rawText = foundRaw ? string.Join(" ", relevantLines.Select(a => PdfTextUtilities.Readable(a.Line.Text))) : null;
        if (rawText is { Length: > 360 }) rawText = rawText[..360];
        return new PdfCandidateRetrievalTrace(
            expected, foundRaw, reasons, inStandard, inBroad, inWide, inSupplement, firstLoss, rawText);
    }

    private static IReadOnlyList<PdfLineBlockAnnotation> FindRawWindow(
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        string target)
    {
        if (target.Length == 0) return [];
        var ordered = annotations.OrderBy(a => a.Line.Page).ThenByDescending(a => a.Line.Y).ThenBy(a => a.Line.Left).ToArray();
        for (var start = 0; start < ordered.Length; start++)
        {
            var window = new List<PdfLineBlockAnnotation>(12);
            for (var offset = 0; offset < 12 && start + offset < ordered.Length; offset++)
            {
                var current = ordered[start + offset];
                if (current.Line.Page != ordered[start].Line.Page) break;
                window.Add(current);
                var text = PdfTextUtilities.CanonicalForMatch(string.Join(" ", window.Select(a => a.Line.Text)));
                if (text.Contains(target, StringComparison.Ordinal)) return window;
            }
        }

        return [];
    }

    private static bool LooksLikeSpacedLogoFragment(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return false;
        return tokens.Count(t => t.Length == 1 && char.IsLetter(t[0])) / (double)tokens.Length >= 0.70;
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
        IReadOnlyList<PdfSemanticBlock> Candidates,
        IReadOnlySet<string> PriorityCandidateIds,
        IReadOnlyDictionary<string, int> SupplementCandidateRanks);

    private static RouteBlockAudit ToAudit(PdfSemanticBlock block) =>
        new(block.Id, block.Page, block.DisplayText);
}

internal sealed record PdfAnalystCandidateSelection(
    IReadOnlyList<PdfSemanticBlock> Selected,
    int Available,
    int AvailablePages,
    int SelectedPages);
