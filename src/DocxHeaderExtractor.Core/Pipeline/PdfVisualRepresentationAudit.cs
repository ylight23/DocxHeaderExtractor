using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using UglyToad.PdfPig;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Deterministic observability audit for visual recovery. It deliberately does not rasterize,
/// OCR, or call a model: a region is only potentially covered by geometry inferred from
/// observable neighbouring document anchors. Pixel identity remains unknown until visual
/// evidence is collected by the optional VLM/OCR enrichment stage.
/// </summary>
public static class PdfVisualRepresentationAudit
{
    public static PdfVisualRepresentationReport Evaluate(string pdfPath, SlimDocument document, AnswerKey key) =>
        Evaluate(pdfPath, document.SourcePath, document.Paragraphs.Cast<IPolicyParagraph>().ToArray(), key);

    public static PdfVisualRepresentationReport Evaluate(
        string pdfPath, DocxPolicyState policyState, AnswerKey key) =>
        Evaluate(pdfPath, policyState.Source.SourcePath,
            policyState.Paragraphs.Cast<IPolicyParagraph>().ToArray(), key);

    internal static PdfVisualRepresentationReport Evaluate(
        string pdfPath, string sourcePath, IReadOnlyList<IPolicyParagraph> paragraphs, AnswerKey key)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var lines = PdfLineExtraction.ExtractLines(pdf);
        var regions = PdfVisualTextRecovery.ListRegionsForAudit(pdfPath);
        var titles = key.PositiveEntries.Where(entry => !string.IsNullOrWhiteSpace(entry.Text))
            .Select(entry => entry.Text!).Distinct(StringComparer.Ordinal).ToArray();
        var retrieval = PdfLayoutEvidenceOutline.TraceCandidateRetrieval(sourcePath, titles)
            .ToDictionary(trace => trace.ExpectedText, StringComparer.Ordinal);
        return EvaluateForAudit(paragraphs, key, lines, regions,
            text => retrieval.TryGetValue(text, out var trace) && trace.FoundInRawWindow);
    }

    internal static PdfVisualRepresentationReport EvaluateForAudit(
        SlimDocument document,
        AnswerKey key,
        IReadOnlyList<PdfLine> lines,
        IReadOnlyList<PdfVisualRegionAudit> regions,
        Func<string, bool>? isTextObservable = null)
        => EvaluateForAudit(document.Paragraphs.Cast<IPolicyParagraph>().ToArray(), key, lines, regions, isTextObservable);

    internal static PdfVisualRepresentationReport EvaluateForAudit(
        IReadOnlyList<IPolicyParagraph> paragraphs,
        AnswerKey key,
        IReadOnlyList<PdfLine> lines,
        IReadOnlyList<PdfVisualRegionAudit> regions,
        Func<string, bool>? isTextObservable = null)
    {
        var gold = key.PositiveEntries
            .Where(entry => entry.Index is not null && !string.IsNullOrWhiteSpace(entry.Text))
            .Select((entry, ordinal) => new GoldEntry(ordinal, entry.Index!.Value, entry.Text!.Trim()))
            .OrderBy(entry => entry.ParagraphIndex).ThenBy(entry => entry.Ordinal)
            .ToArray();

        var anchors = gold.ToDictionary(entry => entry.Ordinal, entry =>
            isTextObservable is not null && !isTextObservable(entry.Text) ? (IReadOnlyList<PdfAnchor>)[] : FindAnchor(lines, entry.Text));
        var missing = gold.Where(entry => anchors[entry.Ordinal].Count == 0).ToArray();
        var entries = missing.Select(entry => BuildEntry(entry, gold, anchors, regions)).ToArray();
        return new PdfVisualRepresentationReport(gold.Length, gold.Length - missing.Length, missing.Length,
            regions.Count, entries.Count(entry => entry.VisualRepresentable), entries);
    }

    private static PdfVisualGoldCoverage BuildEntry(GoldEntry target, IReadOnlyList<GoldEntry> gold,
        IReadOnlyDictionary<int, IReadOnlyList<PdfAnchor>> anchors, IReadOnlyList<PdfVisualRegionAudit> regions)
    {
        var position = Array.FindIndex(gold.ToArray(), entry => entry.Ordinal == target.Ordinal);
        var previous = gold.Take(position).Reverse().FirstOrDefault(entry => anchors[entry.Ordinal].Count > 0);
        var next = gold.Skip(position + 1).FirstOrDefault(entry => anchors[entry.Ordinal].Count > 0);
        var lower = previous is null ? null : anchors[previous.Ordinal].Last();
        var upper = next is null ? null : anchors[next.Ordinal].First();
        var pages = CandidatePages(lower, upper);
        var coverage = regions.Where(region => pages.Contains(region.Page))
            .Select(region => new PdfVisualRegionCoverage(region.SourceId, region.Page, region.Left,
                region.BottomY, region.Right, region.TopY, CoversCorridor(region, lower, upper), region.ObservedEvidence))
            .ToArray();
        var representable = coverage.Any(region => region.CoversExpectedHeadingArea);
        return new PdfVisualGoldCoverage(target.Text, target.ParagraphIndex, false, "not-measured-without-ocr-or-vlm", pages,
            lower is null ? null : new PdfAnchorAudit(lower.Page, lower.Y, lower.Text),
            upper is null ? null : new PdfAnchorAudit(upper.Page, upper.Y, upper.Text),
            regions.Count(region => pages.Contains(region.Page)), coverage, representable,
            representable ? "not-lost-before-visual-model" : "visual-region-generation");
    }

    private static IReadOnlyList<int> CandidatePages(PdfAnchor? previous, PdfAnchor? next)
    {
        if (previous is null && next is null) return [];
        var first = previous?.Page ?? next!.Page;
        var last = next?.Page ?? previous!.Page;
        if (last < first || last - first > 2) return [];
        return Enumerable.Range(first, last - first + 1).ToArray();
    }

    private static bool CoversCorridor(PdfVisualRegionAudit region, PdfAnchor? previous, PdfAnchor? next)
    {
        // PDF Y grows upward. The crop may include either neighbouring anchor as context, but it
        // must extend below the prior anchor and above the next anchor to reach their corridor.
        if (previous is not null && previous.Page == region.Page && region.BottomY >= previous.Y) return false;
        if (next is not null && next.Page == region.Page && region.TopY <= next.Y) return false;
        return true;
    }

    private static IReadOnlyList<PdfAnchor> FindAnchor(IReadOnlyList<PdfLine> lines, string text)
    {
        var canonical = Canonical(text);
        if (canonical.Length < 8) return [];
        var marker = MarkerPrefix(text);
        return lines.Where(line =>
            Canonical(line.Text).Contains(canonical, StringComparison.Ordinal) ||
            (marker is not null && Canonical(line.Text).StartsWith(marker, StringComparison.Ordinal)))
            .Select(line => new PdfAnchor(line.Page, line.Y, line.Text))
            .OrderBy(anchor => anchor.Page).ThenByDescending(anchor => anchor.Y).ToArray();
    }

    // A marker-only line is still an observable structural source fact. This permits legal and
    // numbered headings whose title wraps across PDF lines, while requiring marker position zero
    // so an inline citation cannot turn into a false anchor.
    private static string? MarkerPrefix(string text)
    {
        var match = Regex.Match(text,
            @"^\s*(?:[\p{L}]+\s+(?:\d{1,4}|[IVXLCDM]{1,8})|\d{1,4}(?:[.\-]\d{1,4}){0,4}|[A-Z](?:\.\d{1,4})*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? Canonical(match.Value) : null;
    }

    private static string Canonical(string value) => new string(value.Normalize(System.Text.NormalizationForm.FormD)
        .Where(character => char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record GoldEntry(int Ordinal, int ParagraphIndex, string Text);
    private sealed record PdfAnchor(int Page, double Y, string Text);
}

public sealed record PdfVisualRepresentationReport(int GoldHeadings, int PdfTextObservable,
    int PdfTextUnobservable, int VisualRegionsTotal, int GoldVisualRepresentable,
    IReadOnlyList<PdfVisualGoldCoverage> Entries);

public sealed record PdfVisualGoldCoverage(string Gold, int ParagraphIndex, bool PdfTextObservable,
    string PixelPresence, IReadOnlyList<int> CandidatePages, PdfAnchorAudit? PreviousObservableAnchor,
    PdfAnchorAudit? NextObservableAnchor, int VisualRegionsOnCandidatePages,
    IReadOnlyList<PdfVisualRegionCoverage> RegionCoverage, bool VisualRepresentable, string FirstLoss);

public sealed record PdfAnchorAudit(int Page, double Y, string Text);

public sealed record PdfVisualRegionCoverage(string RegionId, int Page, double Left, double BottomY,
    double Right, double TopY, bool CoversExpectedHeadingArea, IReadOnlyList<string> Signals);

/// <summary>
/// Key-guided, model-free first-loss audit. It classifies evidence already produced by the PDF
/// reader and candidate builders; it never changes candidate selection or emits a heading.
/// </summary>
public static class PdfFirstLossAudit
{
    public static PdfFirstLossReport Evaluate(string documentPath, SlimDocument document, AnswerKey key,
        int selectedBudget = 160,
        PdfReviewedOccurrenceBridge? occurrenceBridge = null,
        IReadOnlyList<string?>? goldStableIds = null)
        => Evaluate(documentPath, document.Paragraphs.Cast<IPolicyParagraph>().ToArray(), key,
            selectedBudget, occurrenceBridge, goldStableIds);

    public static PdfFirstLossReport Evaluate(string documentPath, DocxPolicyState policyState, AnswerKey key,
        int selectedBudget = 160,
        PdfReviewedOccurrenceBridge? occurrenceBridge = null,
        IReadOnlyList<string?>? goldStableIds = null)
        => Evaluate(documentPath, policyState.Paragraphs.Cast<IPolicyParagraph>().ToArray(), key,
            selectedBudget, occurrenceBridge, goldStableIds);

    private static PdfFirstLossReport Evaluate(string documentPath,
        IReadOnlyList<IPolicyParagraph> paragraphs, AnswerKey key,
        int selectedBudget,
        PdfReviewedOccurrenceBridge? occurrenceBridge,
        IReadOnlyList<string?>? goldStableIds)
    {
        var positives = key.PositiveEntries
            .Where(entry => !entry.Excluded && !string.IsNullOrWhiteSpace(entry.Text))
            .Select((entry, ordinal) => new Gold(ordinal, entry.Index, entry.Level, entry.Text!.Trim()))
            .ToArray();
        var titles = positives.Select(entry => entry.Text).Distinct(StringComparer.Ordinal).ToArray();
        var retrieval = PdfLayoutEvidenceOutline.TraceCandidateRetrieval(documentPath, titles)
            .ToDictionary(trace => trace.ExpectedText, StringComparer.Ordinal);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(documentPath);
        var ranking = snapshot.Audit;
        var pdf = PdfTextbookOutline.FindSiblingPdf(documentPath);
        // Visual geometry is needed only for text-unobservable gold. Building it for every
        // financial title is expensive and cannot add evidence to a title already in SourceFacts.
        var visual = pdf is not null && retrieval.Values.Any(trace => !trace.FoundInRawWindow)
            ? PdfVisualRepresentationAudit.Evaluate(pdf, documentPath, paragraphs, key)
            : null;
        var visualByTitle = visual?.Entries
            .GroupBy(entry => entry.Gold, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
            ?? new Dictionary<string, PdfVisualGoldCoverage>(StringComparer.Ordinal);
        var rankedWithRanks = ranking.Candidates.Select((candidate, index) => new Ranked(candidate, index + 1)).ToArray();

        var entries = positives.Select(gold =>
        {
            var trace = retrieval[gold.Text];
            visualByTitle.TryGetValue(gold.Text, out var visualCoverage);
            var match = ranking.Status == "ranked"
                ? rankedWithRanks.FirstOrDefault(item => Matches(item.Candidate.Text, gold.Text))
                : null;
            var occurrences = ranking.Status == "ranked"
                ? rankedWithRanks.Where(item => IsOccurrence(item.Candidate.Text, gold.Text))
                    .Select(item => new PdfFirstLossCandidateOccurrence(item.Rank, item.Candidate.SourceId,
                        item.Candidate.Page, item.Candidate.Scope, item.Candidate.Text, item.Candidate.CandidateScore,
                        item.Candidate.EscalationScore, item.Candidate.PositiveSignals, item.Candidate.NegativeSignals,
                        item.Candidate.AmbiguitySignals)).ToArray()
                : [];
            var exactOccurrenceCount = ranking.Status == "ranked"
                ? rankedWithRanks.Count(item => Canonical(item.Candidate.Text) == Canonical(gold.Text))
                : 0;
            var reconciliation = Reconciliation(gold.Text, match, occurrences, exactOccurrenceCount);
            var representation = ClassifyRepresentation(trace, visualCoverage);
            var occurrence = ResolveOccurrence(gold, goldStableIds, occurrenceBridge, snapshot, rankedWithRanks,
                selectedBudget);
            var firstLoss = occurrence is null
                ? FirstLoss(trace, representation, match?.Rank, selectedBudget, reconciliation)
                : occurrence.FirstLoss;
            return new PdfFirstLossEntry(
                gold.Ordinal, gold.ParagraphIndex, gold.Level, gold.Text, representation,
                trace.FoundInRawWindow, trace.FoundExactSourceLine, trace.RawFilterReasons, trace.FoundInStandardBlock,
                trace.FoundInBroadCandidate, trace.FoundInWideCandidate, trace.FoundInSupplementCandidate,
                match?.Rank, match?.Candidate.SourceId, match?.Candidate.Page, reconciliation, occurrences, firstLoss,
                visualCoverage?.VisualRepresentable ?? false,
                visualCoverage?.PixelPresence ?? "no-sibling-pdf-or-not-measured")
            {
                OccurrenceRank = occurrence?.Rank,
                CandidateMultiplicity = occurrence?.Multiplicity,
                RepresentationType = occurrence?.RepresentationType ?? "not_measured",
                OccurrenceCandidateSourceId = occurrence?.CandidateSourceId,
                CandidateCoverage = occurrence?.CandidateCoverage ?? "not_measured",
                BestPartialCoverageRank = occurrence?.BestPartialCoverageRank,
                SelectedCoverage = occurrence?.SelectedCoverage ?? "not_measured",
            };
        }).ToArray();

        var cutoffs = new[] { 25, 50, 100, selectedBudget, 200, 400, 800, ranking.CandidateCount }
            .Where(cutoff => cutoff > 0 && cutoff <= ranking.CandidateCount).Distinct().Order().ToArray();
        var curve = cutoffs.Select(cutoff => new PdfFirstLossRecallAt(cutoff,
            entries.Count(entry => entry.CandidateRank is > 0 && entry.CandidateRank <= cutoff), positives.Length)).ToArray();
        var window = rankedWithRanks.Where(item => item.Rank >= Math.Max(1, selectedBudget - 20) &&
                                                   item.Rank <= Math.Min(ranking.CandidateCount, selectedBudget + 20))
            .Select(item => new PdfFirstLossCandidateOccurrence(item.Rank, item.Candidate.SourceId, item.Candidate.Page,
                item.Candidate.Scope, item.Candidate.Text, item.Candidate.CandidateScore, item.Candidate.EscalationScore,
                item.Candidate.PositiveSignals, item.Candidate.NegativeSignals, item.Candidate.AmbiguitySignals)).ToArray();

        return new PdfFirstLossReport(
            ranking.Status,
            positives.Length,
            entries.Count(entry => entry.FoundInRawWindow),
            entries.Count(entry => entry.CandidateRank is not null),
            selectedBudget,
            entries.Count(entry => entry.CandidateRank is > 0 && entry.CandidateRank <= selectedBudget),
            entries.GroupBy(entry => entry.Representation).ToDictionary(group => group.Key, group => group.Count()),
            entries.GroupBy(entry => entry.FirstLoss).ToDictionary(group => group.Key, group => group.Count()),
            curve, window, entries);
    }


    /// <summary>
    /// Ranks a gold heading by the candidates that were built from the source lines the heading
    /// actually occupies, rather than by any candidate whose text happens to contain its words.
    /// <para>
    /// The distinction is not academic: a cross-reference quoting a section title contains the same
    /// characters as the heading and previously supplied its rank, which made a representation
    /// problem look like a ranking problem. Coverage is containment of the occurrence's required
    /// source lines - a window that also holds the body text after the heading still represents it -
    /// and never a second text comparison.
    /// </para>
    /// <para>
    /// Without a reviewed occurrence there is no answer, and the caller is told so. There is
    /// deliberately no fall back to text matching, because that is the behaviour being replaced.
    /// </para>
    /// </summary>
    private static OccurrenceResolution? ResolveOccurrence(
        Gold gold,
        IReadOnlyList<string?>? goldStableIds,
        PdfReviewedOccurrenceBridge? bridge,
        PdfCandidateRankingSnapshot snapshot,
        IReadOnlyList<Ranked> ranked,
        int selectedBudget)
    {
        if (bridge is null) return null;
        var stableId = goldStableIds is not null && gold.Ordinal < goldStableIds.Count
            ? goldStableIds[gold.Ordinal]
            : null;
        var reviewed = stableId is null ? null : bridge.Find(stableId);
        if (reviewed is null)
            return new OccurrenceResolution(null, null, "not_measured", null, "occurrence_bridge_unresolved",
                "not_measured", null, "not_measured");

        var required = reviewed.RequiredLines.Select(line => line.Index).ToArray();
        var covering = ranked
            .Where(item => snapshot.Provenance.TryGetValue(item.Candidate.SourceId, out var provenance) &&
                           provenance.Covers(required))
            .OrderBy(item => item.Rank)
            .ToArray();
        // A candidate carrying some but not all of the heading's lines is a real, separate state.
        // Reporting only full coverage would say the model never saw the heading, when what it saw
        // was a truncated one - which is a different defect with a different owner.
        var partial = ranked
            .Where(item => snapshot.Provenance.TryGetValue(item.Candidate.SourceId, out var provenance) &&
                           !provenance.Covers(required) &&
                           required.Any(provenance.LineIndexes.Contains))
            .OrderBy(item => item.Rank)
            .ToArray();
        var bestPartialRank = partial.Length == 0 ? (int?)null : partial[0].Rank;

        if (covering.Length == 0)
            return new OccurrenceResolution(null, 0, "none", null, "candidate_representation",
                partial.Length == 0 ? "none" : "partial", bestPartialRank,
                bestPartialRank <= selectedBudget ? "partial" : "none");

        var best = covering[0];
        var kinds = covering
            .Select(item => snapshot.Provenance[item.Candidate.SourceId].RepresentationKind)
            .ToArray();
        var representationType = kinds.Contains(PdfCandidateRepresentationKind.StandardBlock)
            ? "standard_block"
            : "window_only";
        var firstLoss = best.Rank <= selectedBudget ? "selected" : "ranking_or_budget";
        var selectedCoverage = best.Rank <= selectedBudget ? "full"
            : bestPartialRank <= selectedBudget ? "partial"
            : "none";
        return new OccurrenceResolution(best.Rank, covering.Length, representationType,
            best.Candidate.SourceId, firstLoss, "full", bestPartialRank, selectedCoverage);
    }

    private sealed record OccurrenceResolution(
        int? Rank, int? Multiplicity, string RepresentationType, string? CandidateSourceId, string FirstLoss,
        string CandidateCoverage, int? BestPartialCoverageRank, string SelectedCoverage);

    private static string ClassifyRepresentation(PdfCandidateRetrievalTrace trace, PdfVisualGoldCoverage? visual)
    {
        if (trace.FoundInRawWindow)
        {
            if (trace.RawFilterReasons.Any(reason => reason.Contains("table", StringComparison.OrdinalIgnoreCase) ||
                                                     reason.Contains("layout", StringComparison.OrdinalIgnoreCase)))
                return "layout_or_table_text_malformed";
            return trace.FoundExactSourceLine ? "present_exact_pdf_text_fact" : "present_fragmented_pdf_text_fact";
        }

        // Geometry proves only that a crop can be sent to visual inference. It cannot prove
        // pixel presence without OCR/VLM, so preserve that uncertainty explicitly.
        return visual?.VisualRepresentable == true
            ? "visual_only_not_measured"
            : "absent_from_generated_source_facts";
    }

    private static string FirstLoss(PdfCandidateRetrievalTrace trace, string representation, int? rank, int selectedBudget,
        string reconciliation)
    {
        if (representation is "absent_from_generated_source_facts" or "visual_only_not_measured")
            return "representation";
        if (reconciliation is "ambiguous_short_title" or "ambiguous_candidate_occurrence")
            return reconciliation;
        if (!trace.FoundInStandardBlock) return "semantic_block_grouping";
        if (!trace.FoundInBroadCandidate && !trace.FoundInWideCandidate && !trace.FoundInSupplementCandidate)
            return "candidate_producer";
        if (rank is null) return reconciliation == "ambiguous_short_title"
            ? "ambiguous_short_title"
            : "candidate_pool_reconciliation";
        return rank <= selectedBudget ? "selected" : "ranking_or_budget";
    }

    private static string Reconciliation(string gold, Ranked? containmentMatch,
        IReadOnlyList<PdfFirstLossCandidateOccurrence> occurrences, int exactOccurrenceCount)
    {
        if (exactOccurrenceCount == 1) return "exact_normalized_unique";
        if (exactOccurrenceCount > 1) return Canonical(gold).Length < 12
            ? "ambiguous_short_title"
            : "ambiguous_candidate_occurrence";
        if (Canonical(gold).Length < 12 && occurrences.Count > 1) return "ambiguous_short_title";
        if (occurrences.Count > 1) return "ambiguous_candidate_occurrence";
        return containmentMatch is not null ? "containment_unique" :
            occurrences.Count > 0 ? "candidate_context_or_fragment_mismatch" : "not_reconciled";
    }

    private static bool Matches(string candidate, string expected)
    {
        var left = Canonical(candidate);
        var right = Canonical(expected);
        return left == right || (right.Length >= 12 && left.Contains(right, StringComparison.Ordinal));
    }

    private static string Canonical(string? value) => new string((value ?? "").Normalize(System.Text.NormalizationForm.FormD)
        .Where(character => char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsOccurrence(string candidate, string expected)
    {
        var left = Canonical(candidate);
        var right = Canonical(expected);
        return right.Length > 0 && (left == right || left.Contains(right, StringComparison.Ordinal));
    }

    private sealed record Gold(int Ordinal, int? ParagraphIndex, int? Level, string Text);
    private sealed record Ranked(RankedCandidate Candidate, int Rank);
}

public sealed record PdfFirstLossReport(string Status, int GoldHeadings, int RepresentationObservable,
    int CandidatePoolObservable, int SelectedBudget, int SelectedAtBudget,
    IReadOnlyDictionary<string, int> RepresentationCounts, IReadOnlyDictionary<string, int> FirstLossCounts,
    IReadOnlyList<PdfFirstLossRecallAt> RecallAt, IReadOnlyList<PdfFirstLossCandidateOccurrence> CutoffWindow,
    IReadOnlyList<PdfFirstLossEntry> Entries);

public sealed record PdfFirstLossEntry(int Ordinal, int? ParagraphIndex, int? Level, string Gold,
    string Representation, bool FoundInRawWindow, bool FoundExactSourceLine, IReadOnlyList<string> RawFilterReasons,
    bool FoundInStandardBlock, bool FoundInBroadCandidate, bool FoundInWideCandidate,
    bool FoundInSupplementCandidate, int? CandidateRank, string? CandidateSourceId, int? CandidatePage,
    string Reconciliation, IReadOnlyList<PdfFirstLossCandidateOccurrence> CandidateOccurrences,
    string FirstLoss, bool VisualRegionCanCover, string PixelPresence)
{
    /// <summary>Best rank among candidates built from this occurrence's source lines.</summary>
    public int? OccurrenceRank { get; init; }

    /// <summary>
    /// How many candidates represent the same occurrence. One heading can produce several windows;
    /// counting them as separate occurrences would read candidate duplication as ambiguity.
    /// </summary>
    public int? CandidateMultiplicity { get; init; }

    /// <summary>`standard_block`, `window_only`, `none`, or `not_measured` without a reviewed bridge.</summary>
    public string RepresentationType { get; init; } = "not_measured";

    public string? OccurrenceCandidateSourceId { get; init; }

    /// <summary>`full`, `partial`, `none`: how completely any candidate represents the occurrence.</summary>
    public string CandidateCoverage { get; init; } = "not_measured";

    /// <summary>Best rank among candidates carrying part of the heading but not all of it.</summary>
    public int? BestPartialCoverageRank { get; init; }

    /// <summary>
    /// What actually reached the model within the budget. `partial` says the heading was seen
    /// truncated, which is neither a clean pass nor the same failure as never being seen.
    /// </summary>
    public string SelectedCoverage { get; init; } = "not_measured";
}

public sealed record PdfFirstLossRecallAt(int K, int Hits, int Total);
public sealed record PdfFirstLossCandidateOccurrence(int Rank, string SourceId, int Page, string Scope, string Text,
    double CandidateScore, double EscalationScore, IReadOnlyList<string> PositiveSignals,
    IReadOnlyList<string> NegativeSignals, IReadOnlyList<string> AmbiguitySignals);

/// <summary>
/// Evaluation-only occurrence resolver. It uses the reviewed/rebased DOCX anchor from the key to
/// identify which same-title PDF candidate is the gold occurrence. Production code must never
/// call this resolver or consume its result.
/// </summary>
public static class PdfGoldOccurrenceEvaluator
{
    public static PdfGoldOccurrenceReport Evaluate(SlimDocument document, AnswerKey key, PdfFirstLossReport firstLoss) =>
        Evaluate(document.SourcePath, document.Paragraphs.Cast<IPolicyParagraph>().ToArray(), key, firstLoss);

    public static PdfGoldOccurrenceReport Evaluate(
        DocxPolicyState policyState, AnswerKey key, PdfFirstLossReport firstLoss) =>
        Evaluate(policyState.Source.SourcePath,
            policyState.Paragraphs.Cast<IPolicyParagraph>().ToArray(), key, firstLoss);

    private static PdfGoldOccurrenceReport Evaluate(
        string documentPath, IReadOnlyList<IPolicyParagraph> sourceParagraphs,
        AnswerKey key, PdfFirstLossReport firstLoss)
    {
        var positive = key.PositiveEntries.Where(entry => !entry.Excluded && !string.IsNullOrWhiteSpace(entry.Text)).ToArray();
        var paragraphs = sourceParagraphs.ToDictionary(paragraph => paragraph.Index);
        var pageEvidence = BuildAnchorPageEvidence(documentPath, positive, paragraphs);
        var entries = firstLoss.Entries.Select(entry =>
        {
            var gold = entry.Ordinal < positive.Length ? positive[entry.Ordinal] : null;
            if (gold?.Index is not { } index || !paragraphs.TryGetValue(index, out var anchor))
                return new PdfGoldOccurrenceEntry(entry.Ordinal, entry.Gold, gold?.Index, null, null, null, 0,
                    "gold_anchor_unresolved", [], null);

            var page = pageEvidence.TryGetValue(entry.Ordinal, out var evidence) ? evidence : AnchorPage.Unresolved;
            if (page.Page is null)
                return new PdfGoldOccurrenceEntry(entry.Ordinal, entry.Gold, index, anchor.StableId, anchor.Text, null,
                    page.Score, "gold_anchor_pdf_page_unresolved", [], null);

            var matching = entry.CandidateOccurrences
                .Where(candidate => candidate.Page == page.Page && MapsToAnchor(candidate.Text, entry.Gold, anchor.Text))
                .ToArray();
            var status = matching.Length switch
            {
                0 => "candidate_occurrence_not_mapped_to_gold_anchor",
                1 => "correct_occurrence_resolved",
                _ => "multiple_source_facts_same_gold_anchor",
            };
            return new PdfGoldOccurrenceEntry(entry.Ordinal, entry.Gold, index, anchor.StableId, anchor.Text, page.Page,
                page.Score, status, matching, matching.Length == 0 ? null : matching.Min(candidate => candidate.Rank));
        }).ToArray();
        var cutoffs = firstLoss.RecallAt.Select(point => point.K).Distinct().Order().ToArray();
        return new PdfGoldOccurrenceReport(entries.Length,
            entries.Count(entry => entry.CorrectOccurrenceRank is not null),
            cutoffs.Select(cutoff => new PdfFirstLossRecallAt(cutoff,
                entries.Count(entry => entry.CorrectOccurrenceRank is > 0 && entry.CorrectOccurrenceRank <= cutoff),
                entries.Length)).ToArray(), entries);
    }

    private static bool MapsToAnchor(string candidate, string gold, string anchor)
    {
        var candidateText = Canonical(candidate);
        var goldText = Canonical(gold);
        var anchorText = Canonical(anchor);
        return candidateText.Length >= goldText.Length && candidateText.Contains(goldText, StringComparison.Ordinal) &&
            anchorText.Contains(candidateText, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<int, AnchorPage> BuildAnchorPageEvidence(string documentPath,
        IReadOnlyList<AnswerKeyEntry> positive, IReadOnlyDictionary<int, IPolicyParagraph> paragraphs)
    {
        var pdfPath = PdfTextbookOutline.FindSiblingPdf(documentPath);
        if (pdfPath is null) return new Dictionary<int, AnchorPage>();
        using var pdf = PdfDocument.Open(pdfPath);
        var lines = PdfLineExtraction.ExtractLines(pdf)
            .Select(line => (line.Page, Text: Canonical(PdfTextUtilities.Readable(line.Text))))
            .Where(line => line.Text.Length >= 10).ToArray();
        return positive.Select((entry, ordinal) =>
        {
            if (entry.Index is not { } index || !paragraphs.TryGetValue(index, out var paragraph))
                return (ordinal, Evidence: AnchorPage.Unresolved);
            var anchor = Canonical(paragraph.Text);
            var scores = lines.Where(line => anchor.Contains(line.Text, StringComparison.Ordinal))
                .GroupBy(line => line.Page)
                .Select(group => new { Page = group.Key, Score = group.Sum(line => Math.Min(120, line.Text.Length)) })
                .OrderByDescending(item => item.Score).ThenBy(item => item.Page).ToArray();
            var evidence = scores.Length == 0 || (scores.Length > 1 && scores[0].Score == scores[1].Score)
                ? AnchorPage.Unresolved
                : new AnchorPage(scores[0].Page, scores[0].Score);
            return (ordinal, Evidence: evidence);
        }).ToDictionary(item => item.ordinal, item => item.Evidence);
    }

    private static string Canonical(string value) => new string(value.Normalize(System.Text.NormalizationForm.FormD)
        .Where(character => char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record AnchorPage(int? Page, int Score)
    {
        public static readonly AnchorPage Unresolved = new(null, 0);
    }
}

public sealed record PdfGoldOccurrenceReport(int GoldHeadings, int GoldOccurrencesResolved,
    IReadOnlyList<PdfFirstLossRecallAt> CorrectOccurrenceRecallAt,
    IReadOnlyList<PdfGoldOccurrenceEntry> Entries);

public sealed record PdfGoldOccurrenceEntry(int Ordinal, string Gold, int? GoldParagraphIndex,
    string? GoldStableId, string? GoldAnchorText, int? ExpectedPdfPage, int ExpectedPageEvidenceScore, string Status,
    IReadOnlyList<PdfFirstLossCandidateOccurrence> CorrectCandidateOccurrences, int? CorrectOccurrenceRank);

/// <summary>
/// Offline counterfactual only. It evaluates production occurrence decisions using the M7.16
/// gold occurrence resolver, while keeping the production resolver itself free of gold inputs.
/// </summary>
public static class PdfOccurrenceCounterfactualEvaluator
{
    public static PdfOccurrenceCounterfactualReport Evaluate(PdfGoldOccurrenceReport gold,
        PdfProductionOccurrenceReport production)
    {
        var entries = gold.Entries.Select(entry =>
        {
            var correct = entry.CorrectCandidateOccurrences.Select(candidate => candidate.SourceId).ToHashSet(StringComparer.Ordinal);
            var selected = entry.CorrectCandidateOccurrences
                .Where(candidate => production.FindFamily(candidate.SourceId) is { Resolution: PdfOccurrenceResolution.Preferred } family &&
                                    family.PreferredCandidateId == candidate.SourceId)
                .Select(candidate => candidate.SourceId).ToArray();
            var unique = entry.CorrectCandidateOccurrences
                .Where(candidate => production.FindFamily(candidate.SourceId)?.Resolution == PdfOccurrenceResolution.Unique)
                .Select(candidate => candidate.SourceId).ToArray();
            return new PdfOccurrenceCounterfactualEntry(entry.Ordinal, entry.Gold, entry.Status,
                entry.CorrectOccurrenceRank, correct.ToArray(), unique, selected,
                selected.Length > 0 ? "production_selected_correct_occurrence" :
                unique.Length > 0 ? "production_unique_correct_occurrence" :
                correct.Count == 0 ? "gold_occurrence_not_in_candidate_pool" : "production_unresolved_or_not_preferred");
        }).ToArray();
        return new PdfOccurrenceCounterfactualReport(entries.Length,
            entries.Count(entry => entry.CorrectCandidateSourceIds.Count > 0),
            entries.Count(entry => entry.ProductionUniqueCorrectSourceIds.Count > 0),
            entries.Count(entry => entry.ProductionSelectedCorrectSourceIds.Count > 0), entries);
    }
}

public sealed record PdfOccurrenceCounterfactualReport(int GoldHeadings, int CorrectOccurrenceInPool,
    int ProductionUniqueCorrectOccurrence, int ProductionPreferredCorrectOccurrence,
    IReadOnlyList<PdfOccurrenceCounterfactualEntry> Entries);

public sealed record PdfOccurrenceCounterfactualEntry(int Ordinal, string Gold, string GoldOccurrenceStatus,
    int? CorrectOccurrenceRank, IReadOnlyList<string> CorrectCandidateSourceIds,
    IReadOnlyList<string> ProductionUniqueCorrectSourceIds,
    IReadOnlyList<string> ProductionSelectedCorrectSourceIds, string CounterfactualStatus);

/// <summary>Offline evaluator over immutable per-region inference facts; never calls a model.</summary>
public static class PdfVisualInferenceEvaluator
{
    public static PdfVisualInferenceEvaluation Evaluate(IEnumerable<string> goldTitles,
        IReadOnlyList<PdfVisualRecoveryTrace> traces,
        IReadOnlyList<PdfVisualGoldCoverage>? representation = null)
    {
        var entries = goldTitles.Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.Ordinal)
            .Select(title => EvaluateTitle(title, traces, representation?.FirstOrDefault(item => Same(item.Gold, title))))
            .ToArray();
        return new PdfVisualInferenceEvaluation(entries.Length, entries.Count(entry => entry.Recovered), entries);
    }

    private static PdfVisualGoldInference EvaluateTitle(string gold, IReadOnlyList<PdfVisualRecoveryTrace> all,
        PdfVisualGoldCoverage? representation)
    {
        // `CandidateRegions` means every visual fact on the inferred structural pages. Corridor
        // coverage remains a separate L1 signal; filtering here would hide processed crops and
        // make a broad corridor look like a scheduler loss.
        var regionIds = representation?.RegionCoverage
            .Select(region => region.RegionId).ToHashSet(StringComparer.Ordinal);
        var traces = regionIds is null ? all : all.Where(trace => regionIds.Contains(trace.RegionId)).ToArray();
        // Scheduler/producers may leave an audit trace for regions intentionally not sent to the
        // model. Keep that evidence in the artifact, but do not count it as processed inference.
        var processed = traces.Where(trace => trace.Status is not "visual-producer-excluded" and not "visual-budget-excluded").ToArray();
        var observed = processed.Where(trace => Related(trace.ObservedText, gold)).ToArray();
        var selected = observed.Where(trace => string.Equals(trace.Role, "HeadingTopic", StringComparison.OrdinalIgnoreCase)).ToArray();
        var mapped = observed.Where(trace => Same(trace.MappedText, gold)).ToArray();
        var emitted = mapped.Where(trace => trace.Status == "visual-ocr-canonical-map").ToArray();
        var firstLoss = emitted.Length > 0 ? null :
            processed.Length == 0 ? "visual-region-generation" :
            observed.Length == 0 ? "visual-transcription-or-crop" :
            selected.Length == 0 ? "visual-heading-selection" :
            mapped.Length == 0 ? "canonical-reconciliation" : "source-validation";
        return new PdfVisualGoldInference(gold, representation?.VisualRegionsOnCandidatePages ?? 0,
            representation?.VisualRepresentable ?? false, processed.Length, observed.Length, selected.Length,
            mapped.Length, mapped.Count(trace => trace.Status == "visual-ocr-canonical-map"), emitted.Length > 0, firstLoss);
    }

    private static bool Related(string? observed, string gold)
    {
        var left = Canonical(observed ?? "");
        var right = Canonical(gold);
        return left.Length >= 8 && (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal));
    }

    private static bool Same(string? left, string right) => string.Equals(Canonical(left ?? ""), Canonical(right), StringComparison.Ordinal);

    private static string Canonical(string value) => new string(value.Normalize(System.Text.NormalizationForm.FormD)
        .Where(character => char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed record PdfVisualInferenceEvaluation(int GoldTargets, int RecoveredTargets,
    IReadOnlyList<PdfVisualGoldInference> Entries);

public sealed record PdfVisualGoldInference(string Gold, int CandidateRegions, bool CorridorCovered,
    int RegionsProcessed, int ObservedTextMatches, int HeadingSelections, int CanonicalMapUnique,
    int SourceValidatorAccepted, bool Recovered, string? FirstLoss);

/// <summary>Offline-only aggregation of replayable visual artifacts. A producer is provenance,
/// while a reconciled DOCX span is the structural identity used for dedupe.</summary>
public static class PdfVisualCrossProducerEvaluator
{
    public static PdfVisualCrossProducerReport Evaluate(IEnumerable<PdfVisualRecoveryTrace> traces)
    {
        var all = traces.ToArray();
        var producers = all.GroupBy(trace => Producer(trace.RegionId), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PdfVisualProducerStats(group.Key,
                group.Select(trace => trace.RegionId).Distinct(StringComparer.Ordinal).Count(),
                group.Where(IsAcceptedMap).Select(Identity).Distinct().Count()))
            .ToArray();

        var mapped = all.Where(trace => trace.MappedStableId is not null && trace.MappedSpanStart is not null && trace.MappedSpanEnd is not null)
            .ToArray();
        var bySpan = mapped.GroupBy(SpanIdentity).ToArray();
        var overlap = bySpan.Where(group => group.Select(trace => Producer(trace.RegionId)).Distinct(StringComparer.Ordinal).Count() >= 2).ToArray();
        var accepted = all.Where(IsAcceptedMap).ToArray();
        var byIdentity = accepted.GroupBy(Identity).ToArray();
        var acceptedSpans = accepted.Select(SpanIdentity).ToHashSet();
        return new PdfVisualCrossProducerReport(producers,
            new PdfVisualCrossProducerStats(
                overlap.Length,
                byIdentity.Sum(group => Math.Max(0, group.Count() - 1)),
                bySpan.Count(group => group.Select(trace => trace.Role).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1),
                0,
                overlap.Count(group => !acceptedSpans.Contains(SpanIdentity(group.First())))));
    }

    private static bool IsAcceptedMap(PdfVisualRecoveryTrace trace) => trace.Status == "visual-ocr-canonical-map" &&
        trace.MappedStableId is not null && trace.MappedSpanStart is not null && trace.MappedSpanEnd is not null;

    private static string Producer(string regionId) => regionId.StartsWith("v-marker-line-", StringComparison.Ordinal) ? "marker-line" :
        regionId.StartsWith("v-marker-gap-", StringComparison.Ordinal) ? "marker-span-loss" :
        regionId.StartsWith("v-gap-", StringComparison.Ordinal) ? "vertical-gap" : "unknown";

    private static PdfVisualStructuralIdentity Identity(PdfVisualRecoveryTrace trace) => new(trace.MappedStableId!,
        trace.MappedSpanStart!.Value, trace.MappedSpanEnd!.Value, trace.Role, "document_body");
    private static PdfVisualSourceSpan SpanIdentity(PdfVisualRecoveryTrace trace) => new(trace.MappedStableId!,
        trace.MappedSpanStart!.Value, trace.MappedSpanEnd!.Value);
}

public sealed record PdfVisualProducerStats(string Producer, int Regions, int CanonicalUnique);
public sealed record PdfVisualCrossProducerReport(IReadOnlyList<PdfVisualProducerStats> VisualProducerStats,
    PdfVisualCrossProducerStats CrossProducer);
public sealed record PdfVisualCrossProducerStats(int CanonicalOverlap, int DuplicatesCollapsed,
    int RoleConflicts, int ScopeConflicts, int OverlapRejectedBeforeValidation);
public sealed record PdfVisualStructuralIdentity(string DocxStableId, int SpanStart, int SpanEnd, string Role, string Scope);
public sealed record PdfVisualSourceSpan(string DocxStableId, int SpanStart, int SpanEnd);

/// <summary>Retrospective scheduler benchmark. Ranking uses only pre-model source facts; replay
/// outcomes are labels for measuring utility, never score inputs.</summary>
public static class PdfVisualSchedulerBenchmark
{
    public static PdfVisualSchedulerReport Evaluate(string documentRegime, IReadOnlyList<PdfVisualRegionAudit> regions,
        IReadOnlyList<PdfVisualRecoveryTrace> traces)
    {
        var outcomes = traces.GroupBy(trace => trace.RegionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Any(trace => trace.Status == "visual-ocr-canonical-map"), StringComparer.Ordinal);
        var ranked = regions.Select(region =>
        {
            var marker = region.ObservedEvidence.Contains("labelled_marker_line", StringComparer.Ordinal) ? 3 : 0;
            var layout = region.ObservedEvidence.Contains("text_layer_gap", StringComparer.Ordinal) ? 2 : 0;
            var anomaly = region.ObservedEvidence.Contains("marker_fragmentation", StringComparer.Ordinal) ? 2 : 0;
            var context = region.ObservedEvidence.Contains("visual_neighborhood", StringComparer.Ordinal) ? 1 : 0;
            return new PdfVisualScheduledRegion(region.SourceId, Producer(region.SourceId), region.Page, marker + layout + anomaly + context,
                marker, layout, anomaly, outcomes.TryGetValue(region.SourceId, out var accepted) && accepted);
        }).OrderByDescending(item => item.Score).ThenBy(item => item.Page).ThenBy(item => item.RegionId, StringComparer.Ordinal).ToArray();
        var budgets = new[] { 10, 25, 43 }.Where(budget => budget <= ranked.Length).Select(budget =>
            new PdfVisualSchedulerBudget(budget, ranked.Take(budget).Count(item => item.CanonicalAccepted))).ToArray();
        return new PdfVisualSchedulerReport(documentRegime, ranked.Length, ranked.Count(item => item.CanonicalAccepted), budgets, ranked);
    }

    private static string Producer(string id) => id.StartsWith("v-marker-line-", StringComparison.Ordinal) ? "marker-line" :
        id.StartsWith("v-marker-gap-", StringComparison.Ordinal) ? "marker-span-loss" :
        id.StartsWith("v-gap-", StringComparison.Ordinal) ? "vertical-gap" : "unknown";
}

public sealed record PdfVisualSchedulerReport(string DocumentRegime, int RegionCount, int CanonicalAcceptedTotal,
    IReadOnlyList<PdfVisualSchedulerBudget> Budgets, IReadOnlyList<PdfVisualScheduledRegion> RankedRegions);
public sealed record PdfVisualSchedulerBudget(int Budget, int CanonicalAccepted);
public sealed record PdfVisualScheduledRegion(string RegionId, string Producer, int Page, int Score,
    int MarkerStrength, int LayoutStrength, int RepresentationAnomaly, bool CanonicalAccepted);
