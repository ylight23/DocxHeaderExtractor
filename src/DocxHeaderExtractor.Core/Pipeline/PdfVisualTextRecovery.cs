using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Vision;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Immutable visual source evidence for a region absent from the PDF text layer.</summary>
internal sealed record PdfVisualSourceFacts(
    string SourceId,
    int Page,
    double Left,
    double BottomY,
    double Right,
    double TopY,
    string StructuralScope,
    IReadOnlyList<string> ObservedEvidence,
    int ContextLinesAbove = 0,
    int ContextLinesBelow = 0);

internal sealed record PdfVisualTextRecoveryResult(
    IReadOnlyList<HeadingRecord> Headings,
    IReadOnlyList<PdfValidatedStructure> Structures,
    IReadOnlyList<PdfTextLayerRecoveryAudit> Audit,
    IReadOnlyList<RouteVisualEvidenceAudit> Evidence,
    IReadOnlyList<PdfVisualRecoveryTrace> Traces,
    IReadOnlyList<string> RawResponses);

/// <summary>
/// Recovery for visual text that has no usable PDF line. A VLM may read only a pre-existing page
/// region; it proposes observed text, while deterministic code requires one canonical DOCX span
/// before emitting a review-only heading.
/// </summary>
public static class PdfVisualTextRecovery
{
    private static readonly Regex SubordinateListItemRx = new(@"^\s*(?:[a-z]|[ivxlcdm]{1,5}|\d{1,3})[.)]\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Obsolete("Temporary Slim compatibility overload during R4-6 migration.", error: false)]
    internal static Task<PdfVisualTextRecoveryResult> RecoverAsync(
        string pdfPath, IReadOnlyList<PdfLine> lines, SlimDocument document,
        IReadOnlyList<HeadingRecord> existing, IPdfVisualQuestion visual, int dpi,
        int maximumRegions, string? producer, bool schedule, CancellationToken ct,
        IReadOnlySet<string>? completedRegionIds = null,
        Func<PdfVisualRecoveryTrace, CancellationToken, Task>? checkpoint = null,
        IReadOnlyList<PdfVisualRecoveryTrace>? resumedTraces = null, int maxConcurrency = 1)
    {
        var source = DocxSourceFactsBuilder.Build(document.SourcePath, document.Paragraphs, document.PageHeaders, document.PageFooters);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var built = DocxPolicyStateBuilder.Build(source, features, new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
        var policy = new DocxPolicyState(source, features, built.DerivedFeatures, built.Paragraphs, document.StyleTrust, document.Mode);
        return RecoverAsync(pdfPath, lines, policy, existing, visual, dpi, maximumRegions, producer, schedule, ct,
            completedRegionIds, checkpoint, resumedTraces, maxConcurrency);
    }

    internal static async Task<PdfVisualTextRecoveryResult> RecoverAsync(
        string pdfPath,
        IReadOnlyList<PdfLine> lines,
        DocxPolicyState policyState,
        IReadOnlyList<HeadingRecord> existing,
        IPdfVisualQuestion visual,
        int dpi,
        int maximumRegions,
        string? producer,
        bool schedule,
        CancellationToken ct,
        IReadOnlySet<string>? completedRegionIds = null,
        Func<PdfVisualRecoveryTrace, CancellationToken, Task>? checkpoint = null,
        IReadOnlyList<PdfVisualRecoveryTrace>? resumedTraces = null,
        int maxConcurrency = 1)
    {
        var availableRegions = BuildRegions(pdfPath, lines);
        var producerRegions = FilterByProducer(availableRegions, producer);
        var scheduledRegions = schedule ? RankForRecovery(producerRegions) : producerRegions;
        var regions = (maximumRegions <= 0 ? scheduledRegions : scheduledRegions.Take(maximumRegions))
            .Where(region => completedRegionIds is null || !completedRegionIds.Contains(region.SourceId))
            .ToArray();
        var documentRegime = DocumentDomainPolicy.InferRegime(policyState.Paragraphs.Select(paragraph => paragraph.Text));
        var repeatedArtifacts = RepeatedHeaderArtifactKeys(lines);
        var occupied = existing.Where(heading => heading.HeadingSpan is not null)
            .Select(heading => (heading.Index, heading.HeadingSpan!.Start)).ToHashSet();
        var headings = new List<HeadingRecord>();
        var structures = new List<PdfValidatedStructure>();
        var audit = new List<PdfTextLayerRecoveryAudit>();
        if (!string.IsNullOrWhiteSpace(producer))
            audit.AddRange(availableRegions.Except(producerRegions)
                .Select(region => new PdfTextLayerRecoveryAudit(region.SourceId, region.Page, "visual-producer-excluded")));
        if (maximumRegions > 0)
            audit.AddRange(scheduledRegions.Skip(maximumRegions)
                .Select(region => new PdfTextLayerRecoveryAudit(region.SourceId, region.Page, "visual-budget-excluded")));
        var evidence = new List<RouteVisualEvidenceAudit>();
        var traces = resumedTraces?.ToList() ?? [];
        var raw = new List<string>();
        using var visualGate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        foreach (var trace in traces.Where(trace => trace.Status == "visual-ocr-canonical-map"))
        {
            var restored = MapUnique(policyState.Paragraphs, trace.RegionId, trace.ObservedText, occupied);
            if (restored is null) continue;
            occupied.Add((restored.Index, restored.HeadingSpan!.Start));
            headings.Add(restored);
            audit.Add(new PdfTextLayerRecoveryAudit(trace.RegionId, trace.Page, "visual-checkpoint-resumed"));
        }
        foreach (var region in regions)
        {
            var traceCount = traces.Count;
            ct.ThrowIfCancellationRequested();
            try
            {
                var png = PdfRegionRasterizer.RenderCropPng(pdfPath, region.Page, region.Left, region.BottomY,
                    region.Right, region.TopY, dpi);
                await visualGate.WaitAsync(ct);
                string answer;
                try
                {
                    answer = await visual.AskAsync(png, Prompt(region), 220, ct);
                }
                finally
                {
                    visualGate.Release();
                }
                raw.Add(answer);
                var proposal = Parse(region.SourceId, answer);
                evidence.Add(new RouteVisualEvidenceAudit(region.SourceId, proposal.Role.ToString(), proposal.Confidence,
                    proposal.Evidence, region.ContextLinesAbove, region.ContextLinesBelow));
                if (!IsUsableForRecovery(proposal))
                {
                    audit.Add(new PdfTextLayerRecoveryAudit(region.SourceId, region.Page, "visual-proposal-unusable"));
                    traces.Add(Trace(region, proposal, "visual-proposal-unusable", attempts: Attempts(visual)));
                    continue;
                }
                if (IsRepeatedHeaderArtifact(proposal.ObservedText, repeatedArtifacts))
                {
                    audit.Add(new PdfTextLayerRecoveryAudit(region.SourceId, region.Page, "visual-running-artifact"));
                    traces.Add(Trace(region, proposal, "visual-running-artifact", validatorReason: "repeated_header_footer", attempts: Attempts(visual)));
                    continue;
                }
                if (proposal.ObservedText.Length < 8)
                {
                    audit.Add(new PdfTextLayerRecoveryAudit(region.SourceId, region.Page, "visual-ocr-text-unavailable"));
                    traces.Add(Trace(region, proposal, "visual-ocr-text-unavailable", attempts: Attempts(visual)));
                    continue;
                }
            var mapped = MapUnique(policyState.Paragraphs, region.SourceId, proposal.ObservedText, occupied);
                if (mapped is null)
                {
                    audit.Add(new PdfTextLayerRecoveryAudit(region.SourceId, region.Page, "visual-ocr-map-unresolved"));
                    traces.Add(Trace(region, proposal, "visual-ocr-map-unresolved", attempts: Attempts(visual)));
                    continue;
                }
                mapped = ReconstructMarkerSpan(mapped, documentRegime);
                if (!TryValidateMappedSource(region, mapped, documentRegime, out var domainRole, out var level))
                {
                    audit.Add(new PdfTextLayerRecoveryAudit(region.SourceId, region.Page, "visual-source-validator-rejected"));
                    traces.Add(Trace(region, proposal, "visual-source-validator-rejected", mapped,
                        ValidatorReason(region, mapped, documentRegime), Attempts(visual)));
                    continue;
                }
                mapped.Level = level;
                occupied.Add((mapped.Index, mapped.HeadingSpan!.Start));
                headings.Add(mapped);
                structures.Add(new PdfValidatedStructure(region.SourceId, level, null,
                    "visual-canonical-map-unresolved-parent", "requires_review")
                {
                    DomainRole = domainRole,
                    StructuralScope = region.StructuralScope,
                });
                audit.Add(new PdfTextLayerRecoveryAudit(region.SourceId, region.Page, "visual-ocr-canonical-map"));
                traces.Add(Trace(region, proposal, "visual-ocr-canonical-map", mapped, attempts: Attempts(visual)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                audit.Add(new PdfTextLayerRecoveryAudit(region.SourceId, region.Page, "visual-region-unavailable"));
                traces.Add(new PdfVisualRecoveryTrace(region.SourceId, region.Page, "Uncertain", 0, "", "", "visual-region-unavailable", Attempts: Attempts(visual)));
            }
            finally
            {
                if (checkpoint is not null && traces.Count > traceCount)
                    await checkpoint(traces[^1], ct);
            }
        }
        if (!string.IsNullOrWhiteSpace(producer))
            traces.AddRange(availableRegions.Except(producerRegions).Select(region =>
                new PdfVisualRecoveryTrace(region.SourceId, region.Page, "Uncertain", 0, "", "", "visual-producer-excluded")));
        if (maximumRegions > 0)
            traces.AddRange(scheduledRegions.Skip(maximumRegions).Select(region =>
                new PdfVisualRecoveryTrace(region.SourceId, region.Page, "Uncertain", 0, "", "", "visual-budget-excluded")));
        return new PdfVisualTextRecoveryResult(headings, structures, audit, evidence, traces, raw);
    }

    private static IReadOnlyList<PdfVisualSourceFacts> RankForRecovery(IReadOnlyList<PdfVisualSourceFacts> regions) => regions
        .OrderByDescending(region => (region.ObservedEvidence.Contains("labelled_marker_line", StringComparer.Ordinal) ? 3 : 0) +
            (region.ObservedEvidence.Contains("text_layer_gap", StringComparer.Ordinal) ? 2 : 0) +
            (region.ObservedEvidence.Contains("marker_fragmentation", StringComparer.Ordinal) ? 2 : 0) +
            (region.ObservedEvidence.Contains("visual_neighborhood", StringComparer.Ordinal) ? 1 : 0))
        .ThenBy(region => region.Page).ThenBy(region => region.SourceId, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Runs the same rendered region through progressively stricter visual contracts. This is a
    /// diagnostic only: it never emits a heading and makes the first visual-loss stage explicit.
    /// </summary>
    public static async Task<PdfVisualProbeResult> ProbeAsync(
        string pdfPath,
        SlimDocument document,
        IPdfVisualQuestion visual,
        int regionIndex,
        int dpi,
        CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var lines = PdfLineExtraction.ExtractLines(pdf);
        // Probe is an observability tool, not the recovery scheduler. It must be able to inspect
        // every source fact so a budget exclusion cannot masquerade as an OCR failure.
        var regions = BuildRegions(pdfPath, lines).ToArray();
        if (regionIndex < 0 || regionIndex >= regions.Length)
            throw new ArgumentOutOfRangeException(nameof(regionIndex), $"Visual region index must be 0..{Math.Max(0, regions.Length - 1)}.");
        var region = regions[regionIndex];
        var png = PdfRegionRasterizer.RenderCropPng(pdfPath, region.Page, region.Left, region.BottomY,
            region.Right, region.TopY, dpi);
        var stages = new List<PdfVisualProbeStage>();

        var transcriptionRaw = await visual.AskAsync(png, TranscriptionPrompt(region), 220, ct);
        var transcription = ParseStringField(region.SourceId, transcriptionRaw, "visible_text");
        stages.Add(new PdfVisualProbeStage("A-transcription", transcriptionRaw,
            HasObservableText(transcription) ? "pass" : "fail:no-visible-text", transcription));

        var selectionRaw = await visual.AskAsync(png, SelectionPrompt(region), 260, ct);
        var selection = ParseSelection(region.SourceId, selectionRaw, "visible_text");
        stages.Add(new PdfVisualProbeStage("B-visual-selection", selectionRaw,
            selection.Role == PdfBlockRole.HeadingTopic && HasObservableText(selection.Text) ? "pass" : "fail:not-visible-heading",
            selection.Text));

        var schemaRaw = await visual.AskAsync(png, SchemaPrompt(region), 300, ct);
        var schema = Parse(region.SourceId, schemaRaw);
        stages.Add(new PdfVisualProbeStage("C-structured-proposal", schemaRaw,
            IsUsableForRecovery(schema) && HasObservableText(schema.ObservedText) ? "pass" : "fail:proposal-contract",
            schema.ObservedText));

        var productionRaw = await visual.AskAsync(png, Prompt(region), 300, ct);
        var production = Parse(region.SourceId, productionRaw);
        var usableProduction = IsUsableForRecovery(production) && HasObservableText(production.ObservedText);
        var matchCount = usableProduction
            ? CountCanonicalMatches(document.Paragraphs.Cast<IPolicyParagraph>().ToArray(), production.ObservedText, new HashSet<(int Index, int Start)>())
            : 0;
        var mapped = usableProduction
            ? MapUnique(document.Paragraphs.Cast<IPolicyParagraph>().ToArray(), region.SourceId, production.ObservedText, new HashSet<(int Index, int Start)>())
            : null;
        stages.Add(new PdfVisualProbeStage("D-production-reconciliation", productionRaw,
            !usableProduction ? "fail:proposal-contract" :
                mapped is null ? $"fail:canonical-map:{matchCount}" : "pass:canonical-map", production.ObservedText));

        return new PdfVisualProbeResult(region.SourceId, region.Page, region.ContextLinesAbove,
            region.ContextLinesBelow, region.ObservedEvidence, stages);
    }

    /// <summary>Source-only trace for a VLM-observed string. It makes reconciliation loss observable without a model call.</summary>
    public static PdfSourceReconciliationProbe InspectSourceForAudit(SlimDocument document, string observedText)
    {
        var canonical = CanonicalMap(observedText).Text;
        var allTerms = observedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(CanonicalMap)
            .Select(item => item.Text)
            .Where(item => item.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var termMatches = document.Paragraphs
            .Where(paragraph => !paragraph.InTableOfContents)
            .Where(paragraph =>
            {
                var text = CanonicalMap(paragraph.Text).Text;
                return allTerms.All(term => text.Contains(term, StringComparison.Ordinal));
            })
            .Select(paragraph => paragraph.Index)
            .ToArray();
        return new PdfSourceReconciliationProbe(canonical, CountCanonicalMatches(document.Paragraphs.Cast<IPolicyParagraph>().ToArray(), observedText,
            new HashSet<(int Index, int Start)>()), termMatches, document.Paragraphs.Count);
    }

    /// <summary>Lists every geometry-derived visual source fact before any VLM budget is applied.</summary>
    public static IReadOnlyList<PdfVisualRegionAudit> ListRegionsForAudit(string pdfPath)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        return BuildRegions(pdfPath, PdfLineExtraction.ExtractLines(pdf))
            .Select((region, index) => new PdfVisualRegionAudit(index, region.SourceId, region.Page,
                region.Left, region.BottomY, region.Right, region.TopY,
                region.ContextLinesAbove, region.ContextLinesBelow, region.ObservedEvidence))
            .ToArray();
    }

    /// <summary>Raw text-layer facts for diagnosing a locator without a model call.</summary>
    public static IReadOnlyList<PdfVisualLineAudit> ListLinesForAudit(string pdfPath)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        return PdfLineExtraction.ExtractLines(pdf)
            .Select(line => new PdfVisualLineAudit(line.Page, line.Y, line.BoldRatio, line.Text,
                ParseLabelledArabicMarker(line.Text)?.Label, ParseLabelledArabicMarker(line.Text)?.Number))
            .ToArray();
    }

    internal static IReadOnlyList<PdfVisualSourceFacts> BuildRegionsForAudit(
        IReadOnlyList<PdfLine> lines,
        IReadOnlyDictionary<int, PdfPageBounds> pages) => BuildRegions(lines, page => pages.TryGetValue(page, out var bounds) ? bounds : null);

    private static IReadOnlyList<PdfVisualSourceFacts> BuildRegions(string pdfPath, IReadOnlyList<PdfLine> lines) =>
        BuildRegions(lines, page =>
        {
            try { return PdfRegionRasterizer.GetPageBounds(pdfPath, page); }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { return null; }
        });

    private static IReadOnlyList<PdfVisualSourceFacts> BuildRegions(
        IReadOnlyList<PdfLine> lines,
        Func<int, PdfPageBounds?> pageBounds)
    {
        var regions = new List<(PdfVisualSourceFacts Facts, double Gap)>();
        var boundsByPage = new Dictionary<int, PdfPageBounds>();
        foreach (var page in lines.GroupBy(line => line.Page))
        {
            var bounds = pageBounds(page.Key);
            if (bounds is null) continue;
            boundsByPage[page.Key] = bounds.Value;
            var ordered = page.OrderByDescending(line => line.Y).ToArray();
            for (var index = 0; index + 1 < ordered.Length; index++)
            {
                var upper = ordered[index];
                var lower = ordered[index + 1];
                var gap = upper.Y - lower.Y;
                if (gap is < 24 or > 150) continue;
                // The missing-text gap identifies the region, but the VLM must see its semantic
                // neighborhood. Include up to three full-width lines on either side so it can
                // distinguish a heading from a table label, running artifact, or prose break.
                var above = Math.Min(3, index);
                var below = Math.Min(3, ordered.Length - index - 2);
                var topLine = ordered[index - above];
                var bottomLine = ordered[index + 1 + below];
                var bottom = bottomLine.Y - Math.Max(4, bottomLine.FontSize * 1.4);
                var top = topLine.Y + Math.Max(4, topLine.FontSize * 1.4);
                if (top <= bottom) continue;
                var id = $"v-gap-{page.Key}-{index + 1}";
                regions.Add((new PdfVisualSourceFacts(id, page.Key, 0, bottom, bounds.Value.Width, top,
                    "document_body", ["text_layer_gap", "full_width_visual_crop", "visual_neighborhood"], above, below), gap));
            }
            AddMarkerLineRegions(page.Key, ordered, bounds.Value, regions);
        }
        AddMarkerSpanLossBands(lines, boundsByPage, regions);
        return regions.OrderByDescending(item => item.Gap).ThenBy(item => item.Facts.Page)
            .Select(item => item.Facts).ToArray();
    }

    private static IReadOnlyList<PdfVisualSourceFacts> FilterByProducer(
        IReadOnlyList<PdfVisualSourceFacts> regions, string? producer) => producer?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => regions,
        "marker-line" => regions.Where(region => region.SourceId.StartsWith("v-marker-line-", StringComparison.Ordinal)).ToArray(),
        "vertical-gap" => regions.Where(region => region.SourceId.StartsWith("v-gap-", StringComparison.Ordinal)).ToArray(),
        _ => throw new ArgumentException($"Unknown visual producer '{producer}'. Use all, marker-line, or vertical-gap."),
    };

    // A missing text-layer marker must not become a reconstructed heading. It only creates broad
    // visual proposals between two consecutive, independently observed labelled markers.
    private static void AddMarkerSpanLossBands(IReadOnlyList<PdfLine> lines,
        IReadOnlyDictionary<int, PdfPageBounds> boundsByPage,
        List<(PdfVisualSourceFacts Facts, double Gap)> regions)
    {
        var markers = lines.OrderBy(line => line.Page).ThenByDescending(line => line.Y)
            .Select(line => (Line: line, Marker: ParseLabelledArabicMarker(line.Text)))
            .Where(item => item.Marker is not null)
            .Select(item => (item.Line, Marker: item.Marker!.Value))
            .ToArray();
        foreach (var family in markers.GroupBy(item => item.Marker.Label, StringComparer.Ordinal))
        {
            var ordered = family.ToArray();
            for (var index = 0; index + 1 < ordered.Length; index++)
            {
                var previous = ordered[index];
                var next = ordered[index + 1];
                if (next.Marker.Number != previous.Marker.Number + 2 || next.Line.Page < previous.Line.Page)
                    continue;
                for (var page = previous.Line.Page; page <= next.Line.Page; page++)
                {
                    if (!boundsByPage.TryGetValue(page, out var bounds)) continue;
                    AddFullWidthBands(regions, bounds, page,
                        $"v-marker-gap-{previous.Marker.Label}-{previous.Marker.Number}-{next.Marker.Number}");
                }
            }
        }
    }

    // Marker lines are a second, independent visual-region producer. The text layer may mangle
    // marker spacing or split a heading across lines, but its geometry is still a useful source
    // fact for a context crop. This never emits the marker as a heading by itself.
    private static void AddMarkerLineRegions(int page, IReadOnlyList<PdfLine> ordered, PdfPageBounds bounds,
        List<(PdfVisualSourceFacts Facts, double Gap)> regions)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            var line = ordered[index];
            if (line.BoldRatio < .55 || ParseLabelledArabicMarker(line.Text) is null) continue;
            var above = Math.Min(3, index);
            var below = Math.Min(3, ordered.Count - index - 1);
            var topLine = ordered[index - above];
            var bottomLine = ordered[index + below];
            var bottom = bottomLine.Y - Math.Max(4, bottomLine.FontSize * 1.4);
            var top = topLine.Y + Math.Max(4, topLine.FontSize * 1.4);
            if (top <= bottom) continue;
            var id = $"v-marker-line-{page}-{index + 1}";
            regions.Add((new PdfVisualSourceFacts(id, page, 0, bottom, bounds.Width, top, "document_body",
                ["labelled_marker_line", "full_width_visual_crop", "visual_neighborhood"], above, below), 80));
        }
    }

    private static void AddFullWidthBands(List<(PdfVisualSourceFacts Facts, double Gap)> regions,
        PdfPageBounds bounds, int page, string prefix)
    {
        var low = Math.Max(48, bounds.Height * .10);
        var high = Math.Min(bounds.Height - 48, bounds.Height * .90);
        var step = (high - low) / 3;
        for (var band = 0; band < 3; band++)
        {
            var bottom = Math.Max(low, low + (band * step) - (step * .12));
            var top = Math.Min(high, low + ((band + 1) * step) + (step * .12));
            var id = $"{prefix}-p{page}-b{band + 1}";
            regions.Add((new PdfVisualSourceFacts(id, page, 0, bottom, bounds.Width, top, "document_body",
                ["marker_sequence_gap", "full_width_band", "visual_neighborhood"]), step));
        }
    }

    private static (string Label, int Number)? ParseLabelledArabicMarker(string text)
    {
        // Some PDF producers lose the separator between a Vietnamese/Latin label and its number.
        // The label is bounded by the first digit, so accepting zero whitespace is still a marker
        // fact, not an inferred title.
        var match = Regex.Match(text, @"^\s*(?<label>\p{L}{2,24})\s*(?<number>(?:\d\s*){1,3})\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var digits = string.Concat(match.Groups["number"].Value.Where(char.IsDigit));
        if (!match.Success || !int.TryParse(digits, out var number)) return null;
        return (CanonicalMap(match.Groups["label"].Value).Text, number);
    }

    private static string Prompt(PdfVisualSourceFacts facts) => SchemaPrompt(facts);

    private static string TranscriptionPrompt(PdfVisualSourceFacts facts) =>
        "Read this full-width PDF crop. Transcribe exactly all visible text; do not infer missing text. " +
        "Return strict JSON only: {\"id\":\"" + facts.SourceId + "\",\"visible_text\":\"\"}.";

    private static string SelectionPrompt(PdfVisualSourceFacts facts) =>
        "Read this full-width PDF crop. Decide whether a visible line is a structural document heading, " +
        "not a table label, running header, index term, reference, or prose. Return strict JSON only: " +
        "{\"id\":\"" + facts.SourceId + "\",\"role\":\"heading_topic|body_sentence|table_or_chart_label|decorative_noise|uncertain\",\"visible_text\":\"actual visible text\",\"evidence\":\"specific visual evidence\"}. Replace example values; never return a placeholder.";

    private static string SchemaPrompt(PdfVisualSourceFacts facts) =>
        "Classify this full-width PDF crop and transcribe a visible structural heading only. Never infer text. " +
        "When the heading has a visible marker or number, include that marker and the title in observed_text. " +
        "Set confidence from 0 to 1 from visible evidence; use 0 only when unreadable or non-heading. " +
        "Return strict JSON only: {\"id\":\"" + facts.SourceId + "\",\"role\":\"heading_topic|body_sentence|table_or_chart_label|decorative_noise|uncertain\",\"confidence\":0.8,\"observed_text\":\"marker plus actual visible heading text\",\"evidence\":\"specific visible detail\"}. Replace example values; never return a placeholder.";

    internal static VisualProposal ParseForAudit(string expectedId, string raw) => Parse(expectedId, raw);

    internal static bool IsUsableForRecovery(VisualProposal proposal) =>
        proposal.Role == PdfBlockRole.HeadingTopic && proposal.Confidence >= .65 && HasUsableEvidence(proposal.Evidence);

    internal static bool IsRepeatedHeaderArtifactForAudit(string observedText, IReadOnlyCollection<string> repeatedArtifacts) =>
        IsRepeatedHeaderArtifact(observedText, repeatedArtifacts);

    private static HashSet<string> RepeatedHeaderArtifactKeys(IReadOnlyList<PdfLine> lines) =>
        PdfLineBlockFilter.Analyze(lines)
            .Where(annotation => annotation.Repeated && annotation.HeaderFooterZone)
            .Select(annotation => ArtifactKey(annotation.Line.Text))
            .Where(key => key.Length >= 6)
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsRepeatedHeaderArtifact(string observedText, IReadOnlyCollection<string> repeatedArtifacts)
    {
        var observed = ArtifactKey(observedText);
        return observed.Length >= 6 && repeatedArtifacts.Any(header =>
            observed.Contains(header, StringComparison.Ordinal) || header.Contains(observed, StringComparison.Ordinal));
    }

    private static string ArtifactKey(string text) => new string(CanonicalMap(text).Text.Where(character => !char.IsDigit(character)).ToArray());

    internal static bool IsVisualMappedSourceEligibleForAudit(string text, string documentRegime = "document_body",
        IReadOnlyList<string>? observedEvidence = null) =>
        TryValidateMappedSource(new PdfVisualSourceFacts("audit", 1, 0, 0, 1, 1, "document_body", observedEvidence ?? []),
            new HeadingRecord
            {
                Index = 0,
                Level = 1,
                Text = text,
                OriginalText = text,
                HeadingSpan = new TextOffsetSpan(0, text.Length),
                Source = HeadingSource.Structure,
                Confidence = 1,
            }, documentRegime, out _, out _);

    private static bool TryValidateMappedSource(PdfVisualSourceFacts region, HeadingRecord mapped, string documentRegime,
        out PdfDomainRole domainRole, out int level)
    {
        var text = mapped.Text.Trim();
        var source = new PdfSourceFacts(region.SourceId, text, region.Page, 1, region.Left, region.TopY,
            region.Right, region.BottomY, region.StructuralScope, region.ObservedEvidence)
        {
            Marker = PdfMarkerFactsParser.Parse(text),
        };
        domainRole = ResolveVisualDomainRole(source, documentRegime, region.ObservedEvidence);
        level = Math.Clamp(DocumentDomainPolicy.HierarchyTier(domainRole) ??
            (source.Marker is { IsPath: true, Depth: > 1 } ? source.Marker.Value.Depth : 1), 1, 9);
        return text.Length is >= 3 and <= 240 && text.Any(char.IsLetter) &&
               !(SubordinateListItemRx.IsMatch(text) && !IsStructuralDomainRole(domainRole)) &&
               !DocumentDomainPolicy.IsExcludedFromOutline(domainRole) &&
               region.StructuralScope == "document_body";
    }

    private static PdfVisualRecoveryTrace Trace(PdfVisualSourceFacts region, VisualProposal proposal, string status,
        HeadingRecord? mapped = null, string? validatorReason = null, IReadOnlyList<PdfVisualAttemptOutcome>? attempts = null) => new(
            region.SourceId, region.Page, proposal.Role.ToString(), proposal.Confidence, proposal.ObservedText,
            proposal.Evidence, status, mapped?.Text, mapped?.StableId, mapped?.HeadingSpan?.Start,
            mapped?.HeadingSpan?.End, validatorReason, attempts);

    private static IReadOnlyList<PdfVisualAttemptOutcome>? Attempts(IPdfVisualQuestion visual) =>
        visual is IPdfVisualAttemptAuditable auditable ? auditable.LastAttemptOutcomes : null;

    private static string ValidatorReason(PdfVisualSourceFacts region, HeadingRecord mapped, string documentRegime)
    {
        var text = mapped.Text.Trim();
        var source = new PdfSourceFacts(region.SourceId, text, region.Page, 1, region.Left, region.TopY,
            region.Right, region.BottomY, region.StructuralScope, region.ObservedEvidence)
        {
            Marker = PdfMarkerFactsParser.Parse(text),
        };
        var role = ResolveVisualDomainRole(source, documentRegime, region.ObservedEvidence);
        if (SubordinateListItemRx.IsMatch(text) && !IsStructuralDomainRole(role)) return "list_item_body";
        if (DocumentDomainPolicy.IsExcludedFromOutline(role)) return "domain_scope_excluded";
        if (region.StructuralScope != "document_body") return "structural_scope_excluded";
        return "source_shape_rejected";
    }

    private static bool IsStructuralDomainRole(PdfDomainRole role) => role is
        PdfDomainRole.LegalPart or PdfDomainRole.LegalChapter or PdfDomainRole.LegalSection or
        PdfDomainRole.LegalArticle or PdfDomainRole.LegalClause or PdfDomainRole.LegalPoint or
        PdfDomainRole.ProcurementPart or PdfDomainRole.ProcurementSection or PdfDomainRole.ProcurementGroup or
        PdfDomainRole.ProcurementClause or PdfDomainRole.ProcurementSubclause or
        PdfDomainRole.FinancialSection or PdfDomainRole.FinancialNote or
        PdfDomainRole.MeetingSession or PdfDomainRole.MeetingAgenda;

    // A bare Arabic marker is not a legal article on its own. After the visual model selected a
    // bold marker-line crop and canonical mapping grounded it, legal document context gives that
    // marker a structural role without promoting any OCR text directly into the output.
    private static PdfDomainRole ResolveVisualDomainRole(PdfSourceFacts source, string documentRegime,
        IReadOnlyList<string> observedEvidence)
    {
        var classified = DocumentDomainPolicy.Classify(source, documentRegime);
        if (classified != PdfDomainRole.Unknown || documentRegime is not "legal" and not "VietnameseLegal")
            return classified;

        var markerLine = observedEvidence.Contains("labelled_marker_line", StringComparer.Ordinal);
        return markerLine && source.Marker is { IsPath: true, Depth: 1 }
            ? PdfDomainRole.LegalArticle
            : PdfDomainRole.Unknown;
    }

    private static VisualProposal Parse(string expectedId, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJson(raw));
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || !string.Equals(id.GetString(), expectedId, StringComparison.Ordinal))
                return new VisualProposal(PdfBlockRole.Uncertain, 0, "", "id-mismatch");
            var role = root.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : null;
            var confidence = root.TryGetProperty("confidence", out var value) && value.TryGetDouble(out var number) ? Math.Clamp(number, 0, 1) : 0;
            var observed = root.TryGetProperty("observed_text", out var text) ? text.GetString() ?? "" : "";
            var evidence = root.TryGetProperty("evidence", out var detail) ? detail.GetString() ?? "" : "";
            return new VisualProposal(ParseRole(role), confidence, observed.Trim(), evidence);
        }
        catch (JsonException) { return new VisualProposal(PdfBlockRole.Uncertain, 0, "", "invalid-json"); }
    }

    private static (PdfBlockRole Role, string Text) ParseSelection(string expectedId, string raw, string textProperty)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJson(raw));
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || !string.Equals(id.GetString(), expectedId, StringComparison.Ordinal))
                return (PdfBlockRole.Uncertain, "");
            var role = root.TryGetProperty("role", out var roleValue) ? ParseRole(roleValue.GetString()) : PdfBlockRole.Uncertain;
            return (role, root.TryGetProperty(textProperty, out var text) ? text.GetString()?.Trim() ?? "" : "");
        }
        catch (JsonException) { return (PdfBlockRole.Uncertain, ""); }
    }

    private static string ParseStringField(string expectedId, string raw, string property) =>
        ParseSelection(expectedId, raw, property).Text;

    private static bool HasObservableText(string text) => CanonicalMap(text).Text.Length >= 4;

    private static HeadingRecord? MapUnique(IReadOnlyList<IPolicyParagraph> paragraphs, string sourceId, string observedText, IReadOnlySet<(int Index, int Start)> occupied)
    {
        var matches = FindCanonicalMatches(paragraphs, observedText, occupied);
        if (matches.Count != 1) return null;
        var match = matches[0];
        return new HeadingRecord
        {
            Index = match.Paragraph.Index,
            StableId = match.Paragraph.StableId,
            SourceId = sourceId,
            Level = 1,
            Text = match.Paragraph.Text[match.Start..match.End],
            OriginalText = match.Paragraph.Text,
            HeadingSpan = new TextOffsetSpan(match.Start, match.End),
            BoundarySource = "pdf-visual-ocr-canonical-map",
            StyleId = match.Paragraph.StyleId,
            Source = HeadingSource.Structure,
            Confidence = .70,
            DecisionStatus = HeadingDecisionStatus.RequiresReview,
            ConfidenceBasis = "pdf-visual-sourcefacts-requires-review",
        };
    }

    // OCR commonly omits the label while still reading the numeric title. Once the observed text
    // has exactly one DOCX source span, recover only a contiguous legal/article label already in
    // that paragraph. This is source-span expansion, never marker generation from OCR.
    private static HeadingRecord ReconstructMarkerSpan(HeadingRecord mapped, string documentRegime)
    {
        if (documentRegime is not "legal" and not "VietnameseLegal" || mapped.HeadingSpan is null ||
            string.IsNullOrEmpty(mapped.OriginalText)) return mapped;
        var span = mapped.HeadingSpan;
        var original = mapped.OriginalText;
        var before = original[..span.Start];
        var current = original[span.Start..span.End];
        var marker = Regex.Match(current, @"^\s*(?<number>\d{1,3})[.)]\s+", RegexOptions.CultureInvariant);
        if (!marker.Success) return mapped;

        var prefix = Regex.Match(before, @"(?<label>(?:\b(?:Article)\s+|(?:Điều)\s*))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!prefix.Success) return mapped;

        var start = span.Start + prefix.Index - before.Length;
        if (start < 0 || start >= span.Start) return mapped;
        mapped.Text = original[start..span.End];
        mapped.HeadingSpan = new TextOffsetSpan(start, span.End);
        mapped.BoundarySource = "pdf-visual-marker-span-reconstruction";
        mapped.ConfidenceBasis = "pdf-visual-sourcefacts-marker-span-reconstruction-requires-review";
        return mapped;
    }

    internal static string ReconstructMarkerSpanForAudit(string originalText, int start, int end, string documentRegime) =>
        ReconstructMarkerSpan(new HeadingRecord
        {
            Index = 0,
            StableId = "audit",
            Level = 1,
            Text = originalText[start..end],
            OriginalText = originalText,
            HeadingSpan = new TextOffsetSpan(start, end),
            Source = HeadingSource.Structure,
        }, documentRegime).Text;

    private static int CountCanonicalMatches(IReadOnlyList<IPolicyParagraph> paragraphs, string observedText, IReadOnlySet<(int Index, int Start)> occupied) =>
        FindCanonicalMatches(paragraphs, observedText, occupied).Count;

    private static List<(IPolicyParagraph Paragraph, int Start, int End)> FindCanonicalMatches(
        IReadOnlyList<IPolicyParagraph> paragraphs,
        string observedText,
        IReadOnlySet<(int Index, int Start)> occupied)
    {
        var needle = CanonicalMap(observedText);
        if (needle.Text.Length < 8) return [];
        var matches = new List<(IPolicyParagraph Paragraph, int Start, int End)>();
        foreach (var paragraph in paragraphs.Where(item => !item.InTableOfContents && !string.IsNullOrWhiteSpace(item.Text)))
        {
            var map = CanonicalMap(paragraph.Text);
            var at = map.Text.IndexOf(needle.Text, StringComparison.Ordinal);
            if (at < 0 || occupied.Contains((paragraph.Index, map.Indexes[at]))) continue;
            matches.Add((paragraph, map.Indexes[at], map.Indexes[at + needle.Text.Length - 1] + 1));
        }
        return matches;
    }

    internal static string CanonicalForAudit(string text) => CanonicalMap(text).Text;

    private static CanonicalText CanonicalMap(string text)
    {
        var builder = new StringBuilder(text.Length);
        var indexes = new List<int>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            // OCR/VLM and OpenXML routinely disagree on composed versus decomposed accents. Fold
            // only for matching and retain the original character offset for source spans.
            foreach (var character in text[index].ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                if (!char.IsLetterOrDigit(character)) continue;
                builder.Append(char.ToLowerInvariant(character));
                indexes.Add(index);
            }
        }
        return new CanonicalText(builder.ToString(), indexes);
    }

    private static PdfBlockRole ParseRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "heading_topic" or "heading" => PdfBlockRole.HeadingTopic,
        "body_sentence" or "body" => PdfBlockRole.BodySentence,
        "table_or_chart_label" or "table" => PdfBlockRole.TableOrChartLabel,
        "decorative_noise" or "noise" => PdfBlockRole.DecorativeNoise,
        _ => PdfBlockRole.Uncertain,
    };

    private static bool HasUsableEvidence(string evidence)
    {
        var value = evidence.Trim(' ', '.', '"');
        return value.Length >= 15 && !string.Equals(value, "visible detail", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }

    internal sealed record VisualProposal(PdfBlockRole Role, double Confidence, string ObservedText, string Evidence);
    private sealed record CanonicalText(string Text, IReadOnlyList<int> Indexes);
}

public sealed record PdfVisualProbeStage(string Stage, string RawResponse, string Status, string VisibleText);

public sealed record PdfVisualProbeResult(
    string SourceId,
    int Page,
    int ContextLinesAbove,
    int ContextLinesBelow,
    IReadOnlyList<string> ObservedEvidence,
    IReadOnlyList<PdfVisualProbeStage> Stages);

public sealed record PdfSourceReconciliationProbe(
    string CanonicalObservedText,
    int ExactSpanMatches,
    IReadOnlyList<int> ParagraphsContainingAllObservedTerms,
    int ParagraphCount);

public sealed record PdfVisualRegionAudit(
    int Index,
    string SourceId,
    int Page,
    double Left,
    double BottomY,
    double Right,
    double TopY,
    int ContextLinesAbove,
    int ContextLinesBelow,
    IReadOnlyList<string> ObservedEvidence);

public sealed record PdfVisualLineAudit(int Page, double Y, double BoldRatio, string Text,
    string? MarkerLabel, int? MarkerNumber);
