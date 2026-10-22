using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>One line of a scanned page as the agent sees it: its alias, page, the line of the primary view, the size of the page image, and the
/// line that covers the same region in every other view with the differences between their words.</summary>
internal sealed record ScanLine(string Alias, int Page, OcrLine Primary, int PageWidth, int PageHeight,
    IReadOnlyDictionary<string, (OcrLine Line, IReadOnlyList<TextDifference> Differences)> OtherViews);

/// <summary>
/// The source of an agent run over a scanned document: the lines of its primary OCR view (<c>ocr-original</c>) in reading order, under aliases
/// of the form P003-L012 (page, line), with the other views aligned to them and the vision observations and transcriptions, if any, attached.
/// The document has no text of its own. The text shown for a line is its blind vision transcription (<see cref="VlmTranscribedReading"/>) when
/// one passed the program's checks, else the primary OCR reading; every reading stays available and none is edited. A segment is a window of
/// lines cut like a DOCX one, by size.
/// <para>
/// The facts are measurements of the page image (box, margins, gaps, line height against the page), of the OCR (confidences, whether another
/// line overlaps, whether the views read the line the same way), and of the typography: stroke width and ink measured on the page image against the
/// page median (MEASURED_ON_IMAGE), plus, only when a vision model was asked, its observation marked as such. A scan has no font data, so the
/// facts never give a font size or a weight; where the image could not be measured, the facts say so.
/// </para>
/// </summary>
internal sealed class ScanAgentSource : IAgentSource
{
    public const string Version = "SCAN_AGENT_SOURCE_V4", FactsVersion = "SCAN_LINE_FACTS_V4";
    public const string VlmProposedReading = "vlm-proposed", VlmTranscribedReading = "vlm-transcribed";
    public const int Overlap = 20, LowConfidence = 60, MaxLowConfidenceWords = 8;
    /// <summary>Lines shown as context before and after a unit's own lines: enough to reach the heading of the section a unit's lines belong to.</summary>
    public const int ContextLines = 40;
    /// <summary>A unit of scan lines holds at most this many lines.</summary>
    public const int CoreLines = 48;

    /// <summary>The units, by page as the PDF's LAYOUT_PAGE_BLOCK_48_V1 cuts them, with no layout block: a whole page when it has at most
    /// <see cref="CoreLines"/> lines, else slices of that many lines in reading order; consecutive pieces are packed while the unit stays within
    /// <see cref="CoreLines"/> lines. Only page numbers and line counts decide; no fact and no byte budget does.</summary>
    internal static IReadOnlyList<(int Start, int End)> PageCores(IReadOnlyList<int> pages)
    {
        var pieces = new List<(int Start, int End)>();
        for (var s = 0; s < pages.Count;)
        {
            var e = s; while (e < pages.Count && pages[e] == pages[s]) e++;
            for (var k = s; k < e; k += CoreLines) pieces.Add((k, Math.Min(e, k + CoreLines)));
            s = e;
        }
        var cores = new List<(int, int)>();
        for (var u = 0; u < pieces.Count;)
        {
            var start = pieces[u].Start; var end = pieces[u].End; u++;
            while (u < pieces.Count && pieces[u].End - start <= CoreLines) { end = pieces[u].End; u++; }
            cores.Add((start, end));
        }
        return cores;
    }

    /// <summary>The paragraph experiment's units. Inside each page a line starts a paragraph where the space above it is large for that page: the
    /// gap (the top of the line less the bottom of the line above) is capped at the page's 90th percentile, and the paragraph cut is the two-group
    /// split of the capped gaps that separates them best (Otsu). A page's first line always starts a paragraph. Paragraphs longer than
    /// <see cref="CoreLines"/> are sliced, and consecutive paragraphs are packed into units while a unit stays within <see cref="CoreLines"/> lines,
    /// across pages. Only the measured gaps and the line counts decide; no fixed threshold and no text.</summary>
    internal static IReadOnlyList<(int Start, int End)> ParagraphCores(IReadOnlyList<ScanLine> lines)
    {
        var starts = new bool[lines.Count];
        for (var s = 0; s < lines.Count;)
        {
            var e = s; while (e < lines.Count && lines[e].Page == lines[s].Page) e++;
            starts[s] = true;
            var gaps = Enumerable.Range(s + 1, e - s - 1)
                .Select(i => (double)Math.Max(0, lines[i].Primary.Box.Top - (lines[i - 1].Primary.Box.Top + lines[i - 1].Primary.Box.Height))).ToArray();
            if (gaps.Length > 0)
            {
                var sorted = gaps.OrderBy(g => g).ToArray();
                var top = sorted[Math.Min(sorted.Length - 1, (int)(0.9 * sorted.Length))];
                var cut = OtsuCut(gaps.Select(g => Math.Min(g, top)).ToArray());
                if (cut is { } c)
                    for (var i = s + 1; i < e; i++)
                        if (Math.Min(gaps[i - s - 1], top) > c) starts[i] = true;
            }
            s = e;
        }
        var pieces = new List<(int Start, int End)>();
        for (var k = 0; k < lines.Count;)
        {
            var e = k + 1; while (e < lines.Count && !starts[e]) e++;
            for (var q = k; q < e; q += CoreLines) pieces.Add((q, Math.Min(e, q + CoreLines)));
            k = e;
        }
        var cores = new List<(int, int)>();
        for (var u = 0; u < pieces.Count;)
        {
            var start = pieces[u].Start; var end = pieces[u].End; u++;
            while (u < pieces.Count && pieces[u].End - start <= CoreLines) { end = pieces[u].End; u++; }
            cores.Add((start, end));
        }
        return cores;
    }

    /// <summary>The split of values into two groups that separates them best, as the cut between the groups; null when there are fewer than three.</summary>
    static double? OtsuCut(IReadOnlyList<double> values)
    {
        if (values.Count < 3) return null;
        var v = values.OrderBy(x => x).ToArray();
        var total = v.Sum();
        double best = -1, cut = 0, left = 0;
        for (var i = 1; i < v.Length; i++)
        {
            left += v[i - 1];
            var a = (double)i; var b = v.Length - i;
            var meanA = left / a; var meanB = (total - left) / b;
            var score = a * b * (meanA - meanB) * (meanA - meanB);
            if (score > best) { best = score; cut = (v[i - 1] + v[i]) / 2; }
        }
        return cut;
    }

    public ScanOcrEvidence Evidence { get; }
    public string? EvidenceEntry { get; }
    public IReadOnlyList<ScanLine> ScanLines { get; }
    public IReadOnlyList<AgentSourceLine> Lines { get; }
    public IReadOnlyDictionary<string, JsonObject> Facts { get; }
    public IReadOnlyList<(int Start, int End)> Cores { get; }
    public JsonObject StyleTable { get; }
    public IReadOnlyList<VlmPageObservation> Vision { get; }
    public TranscriptionSet? Transcriptions { get; }
    public string SourceSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public string EvidenceStoreSha256 { get; }
    public IAgentFlavor Flavor => ScanAgentFlavor.Instance;

    readonly Dictionary<string, VlmLineObservation> seen = new(StringComparer.Ordinal);
    readonly Dictionary<string, VlmReadingChoice> chosen = new(StringComparer.Ordinal);
    readonly Dictionary<string, LineTranscription> transcribed = new(StringComparer.Ordinal);
    readonly string? visionModel;

    /// <summary>The type of each line measured on its page image (<see cref="ScanTypography"/>), by OCR line id; empty when not measured.</summary>
    public IReadOnlyDictionary<string, LineTypography> Typography { get; }

    /// <summary>The frame drawn on the page (<see cref="ScanFrames"/>) that encloses each line, by OCR line id; a line outside every frame has no entry.</summary>
    public IReadOnlyDictionary<string, DrawnFrame> Frames { get; }

    public ScanAgentSource(ScanOcrEvidence evidence, IReadOnlyList<VlmPageObservation>? vision = null, TranscriptionSet? transcriptions = null,
        IReadOnlyDictionary<string, LineTypography>? typography = null, string? evidenceEntry = null,
        IReadOnlyDictionary<string, DrawnFrame>? frames = null, bool paragraphUnits = false)
    {
        EvidenceEntry = evidenceEntry;
        Typography = typography ?? new Dictionary<string, LineTypography>();
        Frames = frames ?? new Dictionary<string, DrawnFrame>();
        Evidence = evidence;
        Vision = vision ?? [];
        Transcriptions = transcriptions;
        var primary = evidence.Views.FirstOrDefault(v => v.Spec.ViewId == ScanOcrPreflight.OriginalView)
            ?? throw new InvalidOperationException("SCAN_PRIMARY_VIEW_MISSING");
        var others = evidence.Views.Where(v => v != primary).ToArray();
        var alignments = others.ToDictionary(v => v.Spec.ViewId, v => OcrViewComparer.Compare(primary, v).ToDictionary(a => a.LineA, StringComparer.Ordinal));
        var otherLines = others.ToDictionary(v => v.Spec.ViewId, v => v.Pages.SelectMany(p => p.Lines).ToDictionary(l => l.Id, StringComparer.Ordinal));

        var lines = new List<ScanLine>();
        foreach (var page in primary.Pages)
            for (var n = 0; n < page.Lines.Count; n++)
            {
                var line = page.Lines[n];
                var views = new Dictionary<string, (OcrLine, IReadOnlyList<TextDifference>)>(StringComparer.Ordinal);
                foreach (var v in others)
                    if (alignments[v.Spec.ViewId].TryGetValue(line.Id, out var a) && a.LineB is { } b)
                        views[v.Spec.ViewId] = (otherLines[v.Spec.ViewId][b], a.Differences);
                lines.Add(new ScanLine($"P{page.Page:000}-L{n + 1:000}", page.Page, line, page.ImageWidth, page.ImageHeight, views));
            }
        if (lines.Count == 0) throw new InvalidOperationException("SCAN_EMPTY_UNIVERSE");
        ScanLines = lines;
        index = lines.Select((l, i) => (l.Alias, i)).ToDictionary(x => x.Alias, x => x.i, StringComparer.Ordinal);
        var byLineId = lines.ToDictionary(l => l.Primary.Id, l => l.Alias, StringComparer.Ordinal);
        foreach (var t in transcriptions?.Lines ?? []) if (byLineId.TryGetValue(t.LineId, out var alias)) transcribed[alias] = t;
        Lines = lines.Select((l, i) => new AgentSourceLine(l.Alias, i, l.Page, Text(l.Alias), null, null)).ToArray();

        foreach (var page in Vision.Where(p => p.Failure is null))
        {
            visionModel ??= page.Model;
            foreach (var o in page.Lines) if (byLineId.TryGetValue(o.LineId, out var alias)) seen[alias] = o;
            // A choice among OCR readings is evidence only where no blind transcription was made: the two are not shown side by side.
            if (transcriptions is null)
                foreach (var c in page.Readings) if (byLineId.TryGetValue(c.LineId, out var alias)) chosen[alias] = c;
        }

        Facts = ComputeFacts();
        // Units follow the pages, as the PDF's do, and nothing else: neither the facts nor the text decide where a unit ends.
        // Production cuts units at paragraph boundaries measured from the line gaps and packs them across pages; PageCores is the earlier cut (CLI --page-units).
        Cores = paragraphUnits ? ParagraphCores(lines) : PageCores(lines.Select(l => l.Page).ToArray());
        StyleTable = new JsonObject
        {
            ["readBy"] = "the program: a scanned document has no font styles to count; what is known of a line's typography is in its facts (typography): measured on the page image from the line's own box, and a vision model's observation too when one was asked",
            ["version"] = Version, ["documentLines"] = lines.Count, ["stylesTotal"] = 0, ["styles"] = new JsonArray(),
        };
        SourceSha256 = evidence.SourceSha256;
        SourceAliasUniverseSha256 = Sha(string.Join("\n", lines.Select(l => l.Alias + "\t" + Text(l.Alias))));
        EvidenceStoreSha256 = Sha(string.Join("\n", new[] { Version, FactsVersion, evidence.ParametersSha256 }
            .Concat(Vision.Select(v => v.ImageSha256 + ":" + v.PromptVersion + ":" + v.Model))
            .Concat(Typography.Count == 0 ? [] : [ScanTypography.Version])
            .Concat(transcriptions is null ? [] : [transcriptions.Model + ":" + transcriptions.PromptVersion + ":" + transcriptions.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)])
            .Concat(lines.Select(l => l.Alias + "\t" + Facts[l.Alias].ToJsonString()))));
    }

    /// <summary>The readings of one line by view id: every OCR view that covers it, the vision transcription when it passed the checks, and,
    /// when the vision model proposed one, its proposal.</summary>
    public IReadOnlyDictionary<string, string> Readings(string alias)
    {
        var line = ScanLines[Index(alias)];
        var r = new Dictionary<string, string>(StringComparer.Ordinal) { [ScanOcrPreflight.OriginalView] = line.Primary.Text };
        foreach (var (view, (other, _)) in line.OtherViews) r[view] = other.Text;
        if (transcribed.TryGetValue(alias, out var t) && t.Status == ScanTranscription.Accepted) r[VlmTranscribedReading] = ScanTranscription.Norm(t.Text!);
        if (chosen.TryGetValue(alias, out var c) && !string.IsNullOrWhiteSpace(c.ProposedReading)) r[VlmProposedReading] = c.ProposedReading!;
        return r;
    }

    /// <summary>The reading shown for a line, and taken when the agent names none: the accepted transcription, else the primary OCR view.</summary>
    public string DefaultReading(string alias) =>
        transcribed.TryGetValue(alias, out var t) && t.Status == ScanTranscription.Accepted ? VlmTranscribedReading : ScanOcrPreflight.OriginalView;

    /// <summary>The text shown for a line: its <see cref="DefaultReading"/>.</summary>
    public string Text(string alias) => Readings(alias)[DefaultReading(alias)];

    public LineTranscription? Transcription(string alias) => transcribed.GetValueOrDefault(alias);

    /// <summary>The OCR views whose reading of the line is the same text as the transcription (after NFC and white space).</summary>
    public IReadOnlyList<string> OcrViewsAgreeingWithTranscription(string alias)
    {
        var r = Readings(alias);
        if (!r.TryGetValue(VlmTranscribedReading, out var t)) return [];
        return r.Where(x => x.Key is not (VlmTranscribedReading or VlmProposedReading) && ScanTranscription.Norm(x.Value) == t).Select(x => x.Key).ToArray();
    }

    public VlmReadingChoice? VisionChoice(string alias) => chosen.GetValueOrDefault(alias);
    public VlmLineObservation? VisionLine(string alias) => seen.GetValueOrDefault(alias);
    public string? VisionModel => visionModel;

    readonly Dictionary<string, int> index;
    public int Index(string alias) => index[alias];

    public (string[] Decide, int[] Shown) Scope(int segment)
    {
        var (core, end) = Cores[segment];
        var from = Math.Max(0, core - ContextLines);
        return (Enumerable.Range(core, end - core).Select(i => Lines[i].Alias).ToArray(),
            Enumerable.Range(from, Math.Min(Lines.Count, end + ContextLines) - from).ToArray());
    }

    static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    Dictionary<string, JsonObject> ComputeFacts()
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var surface = ScanTextMeasurements.Build(Lines, DefaultReading);
        // How many other lines of the document carry the same text word for word: a count, not a decision about the line.
        var repeat = ScanLines.Select((l, i) => (k: Evidence_RepeatKey(Text(l.Alias)), i)).Where(x => x.k.Length > 0).ToLookup(x => x.k, x => x.i, StringComparer.Ordinal);
        foreach (var page in ScanLines.Select((l, i) => (l, i)).GroupBy(x => x.l.Page))
        {
            var items = page.ToArray();
            for (var k = 0; k < items.Length; k++)
            {
                var (l, _) = items[k];
                var box = l.Primary.Box;
                var right = l.PageWidth > 0 ? l.PageWidth - box.Right : (int?)null;
                var layout = new JsonObject
                {
                    ["box"] = new JsonObject { ["left"] = box.Left, ["top"] = box.Top, ["width"] = box.Width, ["height"] = box.Height },
                    ["page"] = new JsonObject { ["width"] = l.PageWidth, ["height"] = l.PageHeight },
                };
                if (right is { } r) { layout["leftMarginPx"] = box.Left; layout["rightMarginPx"] = r; layout["centredOffsetPx"] = (box.Left - r) / 2; }
                // The rectangle drawn on the page around the line, measured on the image (OpenCV); absent when no drawn frame encloses it.
                if (Frames.TryGetValue(l.Primary.Id, out var frame))
                    layout["drawnFrame"] = new JsonObject { ["origin"] = "MEASURED_ON_IMAGE", ["left"] = frame.Left, ["top"] = frame.Top, ["width"] = frame.Width, ["height"] = frame.Height };

                JsonObject typography;
                if (Typography.TryGetValue(l.Primary.Id, out var mt))
                    typography = new JsonObject
                    {
                        ["origin"] = "MEASURED_ON_IMAGE", ["measured"] = true,
                        ["strokeWidthPx"] = mt.StrokeWidthPx, ["strokePerHeight"] = mt.StrokePerHeight, ["inkShare"] = mt.InkShare, ["runs"] = mt.Runs,
                    };
                else if (seen.ContainsKey(l.Alias))
                    typography = new JsonObject { ["measured"] = false, ["reason"] = "the page image could not be read; see vision" };
                else
                    typography = new JsonObject { ["measured"] = false, ["reason"] = "a scan carries no font data, and the page image could not be read" };
                if (seen.TryGetValue(l.Alias, out var vo))
                    typography["vision"] = new JsonObject
                    {
                        ["origin"] = "VLM_OBSERVATION", ["observedBy"] = visionModel, ["appearsBold"] = vo.AppearsBold,
                        ["relativeSize"] = vo.RelativeSize, ["alignment"] = vo.Alignment,
                    };
                var node = new JsonObject
                {
                    ["alias"] = l.Alias, ["origin"] = "OCR_DERIVED", ["page"] = l.Page,
                    ["text"] = TextFacts(l),
                    ["surface"] = surface[l.Alias],
                    ["layout"] = layout, ["typography"] = typography,
                };
                if (chosen.TryGetValue(l.Alias, out var c))
                    node["visionReading"] = new JsonObject { ["origin"] = "VLM_OBSERVATION", ["choice"] = c.Choice, ["proposedReading"] = c.ProposedReading is { Length: > 0 } };
                var key = Evidence_RepeatKey(Text(l.Alias));
                node["repetition"] = new JsonObject { ["otherOccurrences"] = key.Length == 0 ? 0 : repeat[key].Count(j => j != index[l.Alias]) };
                result[l.Alias] = node;
            }
        }
        return result;
    }

    /// <summary>Which reading the text shown comes from, and how the transcription relates to the OCR views.</summary>
    JsonObject TextFacts(ScanLine l)
    {
        var shown = Text(l.Alias);
        var o = new JsonObject
        {
            ["shownReading"] = DefaultReading(l.Alias), ["chars"] = shown.Length,
            ["words"] = shown.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
        };
        if (transcribed.TryGetValue(l.Alias, out var t))
        {
            o["transcription"] = t.Status;
            if (t.Status == ScanTranscription.Accepted)
                o["ocrViewsAgreeingWithTranscription"] = new JsonArray(OcrViewsAgreeingWithTranscription(l.Alias).Select(v => (JsonNode)v).ToArray());
        }
        else o["transcription"] = Transcriptions is null ? "NOT_ASKED" : ScanTranscription.NotTranscribed;
        return o;
    }

    static string Evidence_RepeatKey(string text) => Formats.Evidence.RepeatKey(text);
}
