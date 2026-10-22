using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.HeadingAgent.Tools;
using DocxHeaderExtractor.Agents.Transport;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>
/// The heading route of a scanned DOCX: OCR preflight (evidence store), vision preflight (observations of the page images by the agent's own
/// model), then the V6 conversation over the scan source, in which the agent reads the evidence through the scan tools and decides which lines
/// are headings, their extents and the readings it takes. C# measures, stores and checks; it decides no heading and corrects no reading.
/// </summary>
public sealed class ScanSelector(string storeRoot, Func<HttpMessageHandler>? handlerFactory = null, int? parallelism = null,
    Action<ModelReply>? onModelReply = null, string? instruction = null, bool vision = false,
    ScanOcrPreflight.Options? ocrOptions = null, ScanVisionPreflight.Options? visionOptions = null, bool transcribe = true,
    ScanTranscriptionPreflight.Options? transcriptionOptions = null, bool reuseSelection = true, bool paragraphUnits = true, bool segmentedLines = true) : IScanHeadingSelector
{
    /// <summary>The folder of the store root that holds the evidence: the OpenCV segments, or the Tesseract OCR with <c>--tesseract-lines</c>.</summary>
    public const string OcrFolder = "ocr";
    /// <summary>The folder of the store root that holds the selection answers already paid for (<see cref="SelectionCache"/>); with
    /// reuseSelection false (CLI <c>--fresh-selection</c>) it is neither read nor written.</summary>
    public const string SelectionFolder = "selection";
    /// <summary>The folder of the store root that holds the grouping answers already paid for (<see cref="GroupingCache"/>).</summary>
    public const string GroupingFolder = "grouping";
    /// <summary>The folder of the store root that holds one transcript per run: every request and raw reply, with the run's status beside it.</summary>
    public const string TranscriptFolder = "transcripts";
    /// <summary>The largest scan request admitted: the context lines' facts make a request larger than the 128 KB default.</summary>
    public const int ScanRequestByteCap = 262144;

    readonly string instruction = instruction ?? ScanAgentProtocol.Instruction;
    /// <summary>Calls at once in the conversation: its units wait on the provider, not on this machine.</summary>
    public const int DefaultParallelism = 32;

    readonly int parallelism = parallelism ?? DefaultParallelism;

    public string SelectorId => "scan-v6:" + ScanAgentSource.Version + ":facts-in-request:instruction-" + AgentProtocol.Sha(instruction)[..8] + ":" + AgentSelector.BindingPolicyVersion;

    public async Task<ScanHeadingResult> ExtractAsync(string docxPath, string apiKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return (await RunAsync(docxPath, apiKey, ct).ConfigureAwait(false)).Result;
    }

    public async Task<ScanDocumentSelection> PrepareAsync(string path, string? apiKey, CancellationToken ct) =>
        (await RunAsync(path, apiKey, ct).ConfigureAwait(false)).Prepared;

    Task<(ScanHeadingResult Result, ScanDocumentSelection Prepared)> RunAsync(string docxPath, string? apiKey, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (apiKey is not null) ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var seconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ocr = new ScanOcrPreflight(Path.Combine(storeRoot, OcrFolder));
        // Segmented lines: the evidence is made with OpenCV and the stamp-ring removal, no OCR engine; the vision model reads each line.
        var evidence = segmentedLines ? new DocxHeaderExtractor.Sources.Scan.ScanSegmentPreflight(Path.Combine(storeRoot, OcrFolder)).Run(docxPath, ct)
            : ocr.Run(docxPath, ocrOptions, ct);
        seconds["ocr"] = clock.Elapsed.TotalSeconds;
        // The type of every line, measured on the page images in .NET: what the vision model was asked page by page (bold, size) before.
        var typeClock = System.Diagnostics.Stopwatch.StartNew();
        var typography = ScanTypography.MeasureAll(evidence, ocr.EntryOf(evidence), TesseractOcr.FindImageMagick(), ct);
        seconds["typography"] = typeClock.Elapsed.TotalSeconds;
        var seer = new ScanVisionPreflight(Path.Combine(storeRoot, "vlm"), handlerFactory);
        // With the blind transcription, the vision preflight asks no reading choices: choosing among OCR readings drew the model to a wrong one
        // (4 of 11 right on 023's headings), and the agent then took an OCR reading the transcription had read right.
        var seeOptions = visionOptions ?? (transcribe ? new ScanVisionPreflight.Options(MaxReadingsPerPage: 0) : null);
        IReadOnlyList<VlmPageObservation> observations = [];
        TranscriptionSet? transcriptions = null;
        // Both read only the OCR evidence and the page images, and both wait on the provider: they run side by side.
        Parallel.Invoke(new ParallelOptions { CancellationToken = ct },
            () =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                if (vision && apiKey is not null) observations = seer.Run(evidence, ocr.EntryOf(evidence), apiKey, seeOptions, ct);
                lock (seconds) seconds["vision"] = t.Elapsed.TotalSeconds;
            },
            () =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                if (transcribe && apiKey is not null) transcriptions = new ScanTranscriptionPreflight(storeRoot, handlerFactory).Run(evidence, ocr.EntryOf(evidence), apiKey, transcriptionOptions, ct);
                lock (seconds) seconds["transcription"] = t.Elapsed.TotalSeconds;
            });
        seconds["preflight"] = clock.Elapsed.TotalSeconds;
        ct.ThrowIfCancellationRequested();
        var talk = System.Diagnostics.Stopwatch.StartNew();
        ScanDocumentSelection? prepared = null;
        ScanHeadingResult result;
        if (apiKey is null)
        {
            var source = new ScanAgentSource(evidence, [], null, typography);
            var empty = new HeadingSelection(new HashSet<string>(), 0, 0, [], [], 0, 0);
            prepared = PrepareSelection(source, empty);
            result = new ScanHeadingResult(evidence.SourceSha256, SelectorId, [], 0, 0, [], [], 0, 0, 0, [], evidence.Gaps, 0);
        }
        else result = ExtractFrom(evidence, observations, apiKey, ct, transcriptions, typography,
            (source, selection) => prepared = PrepareSelection(source, selection));
        seconds["conversation"] = talk.Elapsed.TotalSeconds;
        foreach (var kv in result.StageSeconds) seconds[kv.Key] = kv.Value;
        result = result with
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
        ct.ThrowIfCancellationRequested();
        return (result, prepared! with { Diagnostics = new ScanRunDiagnostics(result.PreflightCalls, result.PreflightCostUsd,
            result.ProviderCalls, result.ProviderCostUsd, result.TranscribedLines, result.TranscriptionsAccepted, result.TranscriptionStatuses,
            result.TranscriptionFailures, result.VisionFailures, result.OcrGaps, result.StageSeconds) { UnreadableLines = prepared.Diagnostics.UnreadableLines } });
    }, ct);

    /// <summary>The conversation over stored evidence, observations and transcriptions.</summary>
    internal ScanHeadingResult ExtractFrom(ScanOcrEvidence evidence, IReadOnlyList<VlmPageObservation> observations, string apiKey, CancellationToken ct,
        TranscriptionSet? transcriptions = null, IReadOnlyDictionary<string, LineTypography>? typography = null,
        Action<ScanAgentSource, HeadingSelection>? onSelected = null)
    {
        var entry = new ScanOcrPreflight(Path.Combine(storeRoot, OcrFolder)).EntryOf(evidence);
        var source = new ScanAgentSource(evidence, observations, transcriptions, typography, entry, ScanFrames.MeasureAll(evidence, entry, ct), paragraphUnits);
        var budget = AgentSelector.BudgetFor(source, instruction);
        budget.ByteCap = ScanRequestByteCap;
        using var handler = (handlerFactory ?? (() => AgentModel.CreateHandler()))();
        using var model = new AgentModel(handler, apiKey, budget, cancellationToken: ct, rateLimitRetries: AgentModel.ProductionRateLimitRetries);
        var traced = AgentSelector.Traced(model, onModelReply);
        // Every call of the run (selection, grouping, audit) is recorded with its request and raw reply before any parser sees it.
        using var transcript = new ScanRunTranscript(Path.Combine(storeRoot, TranscriptFolder), Guid.NewGuid().ToString("N")[..12]);
        AgentUnit.Model recorded = (unit, turn, request) => transcript.Record(unit, turn, request, () => traced(unit, turn, request));
        var timings = new CallTimings();
        AgentUnit.Model timed = (unit, turn, request) => timings.Time(unit, () => recorded(unit, turn, request));
        var selection = AgentSelector.Select(source, timed, budget, parallelism, ct, retryDelayMs: 5000,
            costOf: () => budget.ActualUsd, instruction, cache: reuseSelection ? new SelectionCache(Path.Combine(storeRoot, SelectionFolder)) : null,
            groupingCache: reuseSelection ? new GroupingCache(Path.Combine(storeRoot, GroupingFolder)) : null,
            audit: (answers, headings) => SelectionAudit.Run(source, answers, headings, recorded, parallelism, ct), siblingReview: true);
        var (headings, rejected) = Headings(source, selection);
        onSelected?.Invoke(source, selection);
        return new ScanHeadingResult(evidence.SourceSha256, SelectorId, headings, selection.UnitsComplete, selection.UnitsTotal, selection.UnitFailures,
            selection.ModelAnswers, selection.ProviderCalls, selection.ProviderCostUsd, observations.Count(o => o.Failure is null),
            observations.Where(o => o.Failure is not null).Select(o => $"P{o.Page:000}:{o.Failure}").ToArray(), evidence.Gaps, rejected)
        {
            Grouping = selection.Grouping,
            ToolUse = selection.ToolUse,
            StageSeconds = selection.StageSeconds ?? new Dictionary<string, double>(),
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

    /// <summary>The source the binder reads. A line with no readable text in its chosen reading (the original view carries no text since the
    /// scan dropped Tesseract, so a line without an accepted transcription reads empty) cannot be a heading: it is left out and counted.</summary>
    internal static ScanDocumentSelection PrepareSelection(ScanAgentSource source, HeadingSelection selection)
    {
        var lines = source.ScanLines.Select(l =>
        {
            var readings = source.Readings(l.Alias);
            var view = selection.HeadingReadings?.GetValueOrDefault(l.Alias) ?? source.DefaultReading(l.Alias);
            if (!readings.TryGetValue(view, out var text)) throw new InvalidOperationException("SCAN_READING_UNKNOWN:" + l.Alias);
            return new ScanSourceLine(l.Alias, l.Primary.Id, l.Page, l.Primary.Box, l.PageWidth, l.PageHeight, text,
                l.Primary.Text, view, readings, source.Transcription(l.Alias)?.Status, source.Facts[l.Alias].ToJsonString(), source.Transcription(l.Alias)?.Text);
        }).ToArray();
        var unreadable = lines.Where(l => string.IsNullOrWhiteSpace(l.Text)).Select(l => l.Alias).ToArray();
        return new ScanDocumentSelection(source.SourceSha256, lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToArray(), selection,
            new ScanRunDiagnostics(0, 0, selection.ProviderCalls, selection.ProviderCostUsd, 0, 0,
                new Dictionary<string, int>(), [], [], source.Evidence.Gaps, new Dictionary<string, double>()) { UnreadableLines = unreadable });
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
