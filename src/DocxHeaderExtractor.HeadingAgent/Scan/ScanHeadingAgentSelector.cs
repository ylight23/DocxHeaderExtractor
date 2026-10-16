using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>One line of a heading of a scan: where it stands (page, OCR line, box in the page image's pixels), the reading the agent took
/// (view and origin), the heading text in it with the extent, and the primary view's reading of the same line, kept beside it.</summary>
public sealed record ScanHeadingPart(string Alias, int Page, string LineId, PixelBox Box, string ViewId, string ReadingOrigin, string Text,
    int ExtentStart, int ExtentEnd, string PrimaryReading, IReadOnlyList<string> ReadingsThatAgree, string Verification);

/// <summary>
/// A heading of a scanned document, anchored to page images and OCR evidence, never to an OOXML paragraph that does not exist. TextStatus says
/// which reading the text comes from (VLM_TRANSCRIBED for the vision model's blind transcription, OCR_PRIMARY_VIEW, OCR_OTHER_VIEW, or
/// CORRECTED_UNVERIFIED for a vision proposal); Verification is the weakest of its parts: CORROBORATED when two readers read the same (an OCR
/// view and the transcription, or an OCR view and the vision model's choice), CONFLICT when the vision model chose another reading than the one
/// taken, UNVERIFIED otherwise. The agent decided that it is a heading, where it ends and which reading to take.
/// </summary>
public sealed record ScanHeading(int Index, IReadOnlyList<ScanHeadingPart> Parts, string Text, string PrimaryReading, string TextStatus, string Verification);

/// <summary>The headings of a scan with the audit of the run: units, failures, the model's answers, calls and cost, the vision pages, and the
/// OCR views that could not be made. Hierarchy (levels and parents) is not part of this result yet.</summary>
public sealed record ScanHeadingResult(string SourceSha256, string SelectorId, IReadOnlyList<ScanHeading> Headings, int UnitsComplete, int UnitsTotal,
    IReadOnlyList<string> UnitFailures, IReadOnlyList<string> ModelAnswers, int ProviderCalls, double ProviderCostUsd,
    int VisionPagesObserved, IReadOnlyList<string> VisionFailures, IReadOnlyList<OcrViewGap> OcrGaps, int PartsRejected)
{
    /// <summary>Lines transcribed by the vision model, and how many passed the checks and are shown.</summary>
    public int TranscribedLines { get; init; }
    public int TranscriptionsAccepted { get; init; }
    public IReadOnlyDictionary<string, int> TranscriptionStatuses { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<string> TranscriptionFailures { get; init; } = [];
    /// <summary>Every transcription the checks refused, with the OCR reading it was checked against: the program never shows these.</summary>
    public IReadOnlyList<TranscriptionRejection> TranscriptionRejections { get; init; } = [];

    /// <summary>The vision observation and transcription calls sent before the conversation, and their cost; not in ProviderCalls.</summary>
    public int PreflightCalls { get; init; }
    public double PreflightCostUsd { get; init; }

    /// <summary>Wall-clock seconds of each stage: ocr, vision and transcription (which run side by side, so their sum is not the time spent),
    /// preflight (ocr to the end of both), conversation. A stage read back from the store takes almost none.</summary>
    public IReadOnlyDictionary<string, double> StageSeconds { get; init; } = new Dictionary<string, double>();

    /// <summary>The provider calls of each stage (vision, transcription, conversation): count, median, 90th percentile, slowest, sum.</summary>
    public IReadOnlyDictionary<string, CallStats> CallSeconds { get; init; } = new Dictionary<string, CallStats>();
    /// <summary>Seconds the transcription spent making its crops, before any call.</summary>
    public double TranscriptionCropSeconds { get; init; }
    /// <summary>The conversation's units whose calls took longest in all (a unit's calls run one after another).</summary>
    public IReadOnlyList<UnitTime> SlowestUnits { get; init; } = [];
}

/// <summary>A transcription the checks refused: the line, why, what the vision model wrote and the primary OCR reading of the same box.</summary>
public sealed record TranscriptionRejection(string Alias, string LineId, string Status, string? Transcription, string OcrReading);

/// <summary>
/// The heading route of a scanned DOCX: OCR preflight (evidence store), vision preflight (observations of the page images by the agent's own
/// model), then the V6 conversation over the scan source, in which the agent reads the evidence through the scan tools and decides which lines
/// are headings, their extents and the readings it takes. C# measures, stores and checks; it decides no heading and corrects no reading.
/// </summary>
public sealed class ScanHeadingAgentSelector(string storeRoot, Func<HttpMessageHandler>? handlerFactory = null, int? parallelism = null,
    Action<ModelReply>? onModelReply = null, string? instruction = null, bool vision = false,
    ScanOcrPreflight.Options? ocrOptions = null, ScanVisionPreflight.Options? visionOptions = null, bool transcribe = true,
    ScanTranscriptionPreflight.Options? transcriptionOptions = null)
{
    /// <summary>The folder of the store root that holds the OCR evidence; <c>dhx scan-ocr</c> writes to the same one.</summary>
    public const string OcrFolder = "ocr";

    readonly string instruction = instruction ?? ScanAgentProtocol.Instruction;
    /// <summary>Calls at once in the conversation: its units wait on the provider, not on this machine.</summary>
    public const int DefaultParallelism = 32;

    readonly int parallelism = parallelism ?? DefaultParallelism;

    public string SelectorId => "scan-v6:" + ScanAgentSource.Version + ":instruction-" + AgentProtocol.Sha(instruction)[..8];

    public Task<ScanHeadingResult> ExtractAsync(string docxPath, string apiKey, CancellationToken ct) => Task.Run(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var seconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ocr = new ScanOcrPreflight(Path.Combine(storeRoot, OcrFolder));
        var evidence = ocr.Run(docxPath, ocrOptions);
        seconds["ocr"] = clock.Elapsed.TotalSeconds;
        // The type of every line, measured on the page images in .NET: what the vision model was asked page by page (bold, size) before.
        var typeClock = System.Diagnostics.Stopwatch.StartNew();
        var typography = ScanTypography.MeasureAll(evidence, ocr.EntryOf(evidence), TesseractOcr.FindImageMagick());
        seconds["typography"] = typeClock.Elapsed.TotalSeconds;
        var seer = new ScanVisionPreflight(Path.Combine(storeRoot, "vlm"), handlerFactory);
        // With the blind transcription, the vision preflight asks no reading choices: choosing among OCR readings drew the model to a wrong one
        // (4 of 11 right on 023's headings), and the agent then took an OCR reading the transcription had read right.
        var seeOptions = visionOptions ?? (transcribe ? new ScanVisionPreflight.Options(MaxReadingsPerPage: 0) : null);
        IReadOnlyList<VlmPageObservation> observations = [];
        TranscriptionSet? transcriptions = null;
        // Both read only the OCR evidence and the page images, and both wait on the provider: they run side by side.
        Parallel.Invoke(
            () =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                if (vision) observations = seer.Run(evidence, ocr.EntryOf(evidence), apiKey, seeOptions);
                lock (seconds) seconds["vision"] = t.Elapsed.TotalSeconds;
            },
            () =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                if (transcribe) transcriptions = new ScanTranscriptionPreflight(storeRoot, handlerFactory).Run(evidence, ocr.EntryOf(evidence), apiKey, transcriptionOptions);
                lock (seconds) seconds["transcription"] = t.Elapsed.TotalSeconds;
            });
        seconds["preflight"] = clock.Elapsed.TotalSeconds;
        var talk = System.Diagnostics.Stopwatch.StartNew();
        var result = ExtractFrom(evidence, observations, apiKey, ct, transcriptions, typography);
        seconds["conversation"] = talk.Elapsed.TotalSeconds;
        return result with
        {
            PreflightCalls = seer.LastCalls + (transcriptions?.ProviderCalls ?? 0),
            PreflightCostUsd = seer.LastCostUsd + (transcriptions?.ProviderCostUsd ?? 0),
            StageSeconds = seconds.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1)),
            CallSeconds = new Dictionary<string, CallStats>(result.CallSeconds)
            {
                ["vision"] = seer.LastCallTimes ?? new CallStats(0, 0, 0, 0, 0),
                ["transcription"] = transcriptions?.Calls ?? new CallStats(0, 0, 0, 0, 0),
            },
            TranscriptionCropSeconds = transcriptions?.CropSeconds ?? 0,
        };
    }, ct);

    /// <summary>The conversation over stored evidence, observations and transcriptions.</summary>
    internal ScanHeadingResult ExtractFrom(ScanOcrEvidence evidence, IReadOnlyList<VlmPageObservation> observations, string apiKey, CancellationToken ct,
        TranscriptionSet? transcriptions = null, IReadOnlyDictionary<string, LineTypography>? typography = null)
    {
        var source = new ScanAgentSource(evidence, observations, transcriptions, typography);
        var budget = HeadingAgentSelector.BudgetFor(source, instruction);
        using var handler = (handlerFactory ?? (() => AgentModel.CreateHandler()))();
        using var model = new AgentModel(handler, apiKey, budget);
        var traced = HeadingAgentSelector.Traced(model, onModelReply);
        var timings = new CallTimings();
        AgentUnit.Model timed = (unit, turn, request) => timings.Time(unit, () => traced(unit, turn, request));
        var selection = HeadingAgentSelector.Select(source, timed, budget, parallelism, ct, retryDelayMs: 5000,
            costOf: () => budget.ActualUsd, instruction);
        var (headings, rejected) = Headings(source, selection);
        return new ScanHeadingResult(evidence.SourceSha256, SelectorId, headings, selection.UnitsComplete, selection.UnitsTotal, selection.UnitFailures,
            selection.ModelAnswers, selection.ProviderCalls, selection.ProviderCostUsd, observations.Count(o => o.Failure is null),
            observations.Where(o => o.Failure is not null).Select(o => $"P{o.Page:000}:{o.Failure}").ToArray(), evidence.Gaps, rejected)
        {
            TranscribedLines = transcriptions?.Lines.Count(l => l.Text is not null) ?? 0,
            TranscriptionsAccepted = transcriptions?.Accepted ?? 0,
            TranscriptionStatuses = (transcriptions?.Lines ?? []).GroupBy(l => l.Status).ToDictionary(g => g.Key, g => g.Count()),
            TranscriptionFailures = transcriptions?.Failures ?? [],
            TranscriptionRejections = source.ScanLines.Select(l => (l, t: source.Transcription(l.Alias)))
                .Where(x => x.t is { Status: not (ScanTranscription.Accepted or ScanTranscription.NotTranscribed) })
                .Select(x => new TranscriptionRejection(x.l.Alias, x.l.Primary.Id, x.t!.Status, x.t.Text, x.l.Primary.Text)).ToArray(),
            CallSeconds = new Dictionary<string, CallStats> { ["conversation"] = timings.Stats() },
            SlowestUnits = timings.SlowestUnits(5),
        };
    }

    /// <summary>The selected lines as headings, in reading order: the agent's division when it gave one, else one heading per line. A part whose
    /// extent does not lie in its reading is rejected and counted, never widened to the whole line.</summary>
    internal static (IReadOnlyList<ScanHeading> Headings, int Rejected) Headings(ScanAgentSource source, HeadingSelection selection)
    {
        var groups = (selection.Groups ?? [])
            .Select(g => g.Where(selection.SelectedAliases.Contains).OrderBy(source.Index).ToList()).Where(g => g.Count > 0).ToList();
        var grouped = groups.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
        groups.AddRange(selection.SelectedAliases.Where(a => !grouped.Contains(a)).Select(a => new List<string> { a }));
        var rejected = 0;
        var headings = new List<ScanHeading>();
        foreach (var group in groups.OrderBy(g => source.Index(g[0])))
        {
            var parts = new List<ScanHeadingPart>();
            foreach (var alias in group)
            {
                var line = source.ScanLines[source.Index(alias)];
                var readings = source.Readings(alias);
                var view = selection.HeadingReadings?.GetValueOrDefault(alias) ?? source.DefaultReading(alias);
                if (!readings.TryGetValue(view, out var reading)) { rejected++; continue; }
                var (start, end) = selection.HeadingExtents?.TryGetValue(alias, out var e) == true ? (e.Start, e.End) : (0, reading.Length);
                if (start < 0 || end <= start || end > reading.Length) { rejected++; continue; }
                if (view == ScanAgentSource.VlmTranscribedReading)
                {
                    // The transcription was made without any OCR reading in sight: an OCR view that reads the same is a second, independent reader.
                    var agreeing = source.OcrViewsAgreeingWithTranscription(alias);
                    parts.Add(new ScanHeadingPart(alias, line.Page, line.Primary.Id, line.Primary.Box, view, "VLM_TRANSCRIBED", reading[start..end], start, end,
                        line.Primary.Text, agreeing.Prepend(view).ToArray(), agreeing.Count > 0 ? "CORROBORATED" : "UNVERIFIED"));
                    continue;
                }
                var agree = readings.Where(r => r.Key is not (ScanAgentSource.VlmProposedReading or ScanAgentSource.VlmTranscribedReading) && r.Value == reading).Select(r => r.Key).ToList();
                var choice = source.VisionChoice(alias);
                var visionAgrees = choice is not null && (choice.Choice == view || (view == ScanAgentSource.VlmProposedReading && choice.Choice == "NONE")
                    || (readings.TryGetValue(choice.Choice, out var chosenText) && chosenText == reading));
                if (visionAgrees) agree.Add("vlm:" + choice!.Choice);
                var verification = choice is not null && !visionAgrees && choice.Choice != "UNREADABLE" ? "CONFLICT"
                    : visionAgrees && agree.Any(a => !a.StartsWith("vlm:", StringComparison.Ordinal)) ? "CORROBORATED"
                    : "UNVERIFIED";
                parts.Add(new ScanHeadingPart(alias, line.Page, line.Primary.Id, line.Primary.Box, view,
                    view == ScanAgentSource.VlmProposedReading ? "CORRECTION_CANDIDATE" : "OCR_DERIVED",
                    reading[start..end], start, end, line.Primary.Text, agree, verification));
            }
            if (parts.Count == 0) continue;
            var status = parts.Any(p => p.ViewId == ScanAgentSource.VlmProposedReading) ? "CORRECTED_UNVERIFIED"
                : parts.Any(p => p.ViewId == ScanAgentSource.VlmTranscribedReading) ? "VLM_TRANSCRIBED"
                : parts.Any(p => p.ViewId != ScanOcrPreflight.OriginalView) ? "OCR_OTHER_VIEW" : "OCR_PRIMARY_VIEW";
            var worst = parts.Any(p => p.Verification == "CONFLICT") ? "CONFLICT" : parts.All(p => p.Verification == "CORROBORATED") ? "CORROBORATED" : "UNVERIFIED";
            headings.Add(new ScanHeading(headings.Count + 1, parts, string.Join(" ", parts.Select(p => p.Text)),
                string.Join(" ", parts.Select(p => p.PrimaryReading)), status, worst));
        }
        return (headings, rejected);
    }
}
