using DocxHeaderExtractor.Core.Eval;
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
    public static PdfVisualRepresentationReport Evaluate(string pdfPath, SlimDocument document, AnswerKey key)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var lines = PdfLineExtraction.ExtractLines(pdf);
        var regions = PdfVisualTextRecovery.ListRegionsForAudit(pdfPath);
        var titles = key.PositiveEntries.Where(entry => !string.IsNullOrWhiteSpace(entry.Text))
            .Select(entry => entry.Text!).Distinct(StringComparer.Ordinal).ToArray();
        var retrieval = PdfLayoutEvidenceOutline.TraceCandidateRetrieval(document.SourcePath, titles)
            .ToDictionary(trace => trace.ExpectedText, StringComparer.Ordinal);
        return EvaluateForAudit(document, key, lines, regions,
            text => retrieval.TryGetValue(text, out var trace) && trace.FoundInRawWindow);
    }

    internal static PdfVisualRepresentationReport EvaluateForAudit(
        SlimDocument document,
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
