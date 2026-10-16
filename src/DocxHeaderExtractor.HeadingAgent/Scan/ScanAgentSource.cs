using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>One line of a scanned page as the agent sees it: its alias, page, the line of the primary view, the size of the page image, and the
/// line that covers the same region in every other view with the differences between their words.</summary>
internal sealed record ScanLine(string Alias, int Page, OcrLine Primary, int PageWidth, int PageHeight,
    IReadOnlyDictionary<string, (OcrLine Line, IReadOnlyList<TextDifference> Differences)> OtherViews);

/// <summary>
/// The source of an agent run over a scanned document: the lines of its primary OCR view (<c>ocr-original</c>) in reading order, under aliases
/// of the form P003-L012 (page, line), with the other views aligned to them and the vision observations, if any, attached. The document has no
/// text of its own: every line is OCR_DERIVED, and no reading has been corrected. A segment is a window of lines cut like a DOCX one, by size.
/// <para>
/// The facts are measurements of the page image (box, margins, gaps, line height against the page), of the OCR (confidences, whether another
/// line overlaps, whether the views read the line the same way) and, only when a vision model was asked, its observation of the typography,
/// marked as such. A scan has no font data: without a vision observation the facts say that typography was not measured.
/// </para>
/// </summary>
internal sealed class ScanAgentSource : IAgentSource
{
    public const string Version = "SCAN_AGENT_SOURCE_V1", FactsVersion = "SCAN_LINE_FACTS_V1";
    public const string VlmProposedReading = "vlm-proposed";
    public const int Overlap = 20, LeftEdgeTolerancePx = 8, LowConfidence = 60, MaxLowConfidenceWords = 8;

    public ScanOcrEvidence Evidence { get; }
    public IReadOnlyList<ScanLine> ScanLines { get; }
    public IReadOnlyList<AgentSourceLine> Lines { get; }
    public IReadOnlyDictionary<string, JsonObject> Facts { get; }
    public IReadOnlyList<(int Start, int End)> Cores { get; }
    public JsonObject StyleTable { get; }
    public IReadOnlyList<VlmPageObservation> Vision { get; }
    public string SourceSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public string EvidenceStoreSha256 { get; }
    public IAgentFlavor Flavor => ScanAgentFlavor.Instance;

    readonly Dictionary<string, VlmLineObservation> seen = new(StringComparer.Ordinal);
    readonly Dictionary<string, VlmReadingChoice> chosen = new(StringComparer.Ordinal);
    readonly string? visionModel;

    public ScanAgentSource(ScanOcrEvidence evidence, IReadOnlyList<VlmPageObservation>? vision = null)
    {
        Evidence = evidence;
        Vision = vision ?? [];
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
        Lines = lines.Select((l, i) => new AgentSourceLine(l.Alias, i, l.Page, l.Primary.Text, null, null)).ToArray();

        var byLineId = lines.ToDictionary(l => l.Primary.Id, l => l.Alias, StringComparer.Ordinal);
        foreach (var page in Vision.Where(p => p.Failure is null))
        {
            visionModel ??= page.Model;
            foreach (var o in page.Lines) if (byLineId.TryGetValue(o.LineId, out var alias)) seen[alias] = o;
            foreach (var c in page.Readings) if (byLineId.TryGetValue(c.LineId, out var alias)) chosen[alias] = c;
        }

        Facts = ComputeFacts();
        Cores = DocxAgentSource.CoresOf(lines.Select(l => Facts[l.Alias].ToJsonString().Length + l.Alias.Length + 12).ToArray());
        StyleTable = new JsonObject
        {
            ["readBy"] = "the program: a scanned document has no font styles to count; what is known of a line's typography is in its facts (typography), from a vision model when one was asked",
            ["version"] = Version, ["documentLines"] = lines.Count, ["stylesTotal"] = 0, ["styles"] = new JsonArray(),
        };
        SourceSha256 = evidence.SourceSha256;
        SourceAliasUniverseSha256 = Sha(string.Join("\n", lines.Select(l => l.Alias + "\t" + l.Primary.Text)));
        EvidenceStoreSha256 = Sha(string.Join("\n", new[] { Version, FactsVersion, evidence.ParametersSha256 }
            .Concat(Vision.Select(v => v.ImageSha256 + ":" + v.PromptVersion + ":" + v.Model))
            .Concat(lines.Select(l => l.Alias + "\t" + Facts[l.Alias].ToJsonString()))));
    }

    /// <summary>The readings of one line by view id: every OCR view that covers it and, when the vision model proposed one, its reading.</summary>
    public IReadOnlyDictionary<string, string> Readings(string alias)
    {
        var line = ScanLines[Index(alias)];
        var r = new Dictionary<string, string>(StringComparer.Ordinal) { [ScanOcrPreflight.OriginalView] = line.Primary.Text };
        foreach (var (view, (other, _)) in line.OtherViews) r[view] = other.Text;
        if (chosen.TryGetValue(alias, out var c) && !string.IsNullOrWhiteSpace(c.ProposedReading)) r[VlmProposedReading] = c.ProposedReading!;
        return r;
    }

    public VlmReadingChoice? VisionChoice(string alias) => chosen.GetValueOrDefault(alias);
    public VlmLineObservation? VisionLine(string alias) => seen.GetValueOrDefault(alias);
    public string? VisionModel => visionModel;

    Dictionary<string, int>? index;
    public int Index(string alias) => (index ??= Lines.Select((l, i) => (l.Alias, i)).ToDictionary(x => x.Alias, x => x.i, StringComparer.Ordinal))[alias];

    public (string[] Decide, int[] Shown) Scope(int segment)
    {
        var (core, end) = Cores[segment];
        var from = Math.Max(0, core - Overlap);
        return (Enumerable.Range(core, end - core).Select(i => Lines[i].Alias).ToArray(),
            Enumerable.Range(from, Math.Min(Lines.Count, end + Overlap) - from).ToArray());
    }

    static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    Dictionary<string, JsonObject> ComputeFacts()
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var repeat = ScanLines.Select((l, i) => (k: Evidence_RepeatKey(l.Primary.Text), i)).Where(x => x.k.Length > 0).ToLookup(x => x.k, x => x.i, StringComparer.Ordinal);
        foreach (var page in ScanLines.Select((l, i) => (l, i)).GroupBy(x => x.l.Page))
        {
            var items = page.ToArray();
            var heights = items.Select(x => (double)x.l.Primary.Box.Height).Order().ToArray();
            var medianHeight = heights[heights.Length / 2];
            var lefts = items.Select(x => x.l.Primary.Box.Left).ToArray();
            var common = lefts.Select(e => (Edge: e, Count: lefts.Count(o => Math.Abs(o - e) <= LeftEdgeTolerancePx)))
                .OrderByDescending(x => x.Count).ThenBy(x => x.Edge).First();
            for (var k = 0; k < items.Length; k++)
            {
                var (l, i) = items[k];
                var box = l.Primary.Box;
                var right = l.PageWidth > 0 ? l.PageWidth - box.Right : (int?)null;
                var layout = new JsonObject
                {
                    ["box"] = new JsonObject { ["left"] = box.Left, ["top"] = box.Top, ["width"] = box.Width, ["height"] = box.Height },
                    ["page"] = new JsonObject { ["width"] = l.PageWidth, ["height"] = l.PageHeight },
                    ["leftEdgeOffsetFromPageCommonPx"] = box.Left - common.Edge,
                    ["leftEdgeSharedBy"] = lefts.Count(o => Math.Abs(o - box.Left) <= LeftEdgeTolerancePx) - 1,
                    ["lineHeightVsPageMedian"] = Math.Round(box.Height / medianHeight, 2),
                };
                if (right is { } r) { layout["leftMarginPx"] = box.Left; layout["rightMarginPx"] = r; layout["centredOffsetPx"] = (box.Left - r) / 2; }
                if (k > 0) layout["gapAbovePx"] = box.Top - items[k - 1].l.Primary.Box.Bottom;
                if (k + 1 < items.Length) layout["gapBelowPx"] = items[k + 1].l.Primary.Box.Top - box.Bottom;

                var low = l.Primary.Words.Where(w => w.Confidence < LowConfidence).ToArray();
                var ocr = new JsonObject
                {
                    ["meanConfidence"] = l.Primary.MeanConfidence, ["minConfidence"] = l.Primary.MinConfidence,
                    ["lowConfidenceWords"] = low.Length, ["overlapsOtherLine"] = l.Primary.OverlapsOtherLine,
                    ["viewsCoveringLine"] = 1 + l.OtherViews.Count,
                    ["viewsReadItDifferently"] = l.OtherViews.Values.Any(v => v.Differences.Count > 0),
                    ["differencesWithOtherViews"] = l.OtherViews.Values.Sum(v => v.Differences.Count),
                };
                JsonObject typography;
                if (seen.TryGetValue(l.Alias, out var vo))
                    typography = new JsonObject
                    {
                        ["origin"] = "VLM_OBSERVATION", ["observedBy"] = visionModel, ["appearsBold"] = vo.AppearsBold,
                        ["relativeSize"] = vo.RelativeSize, ["alignment"] = vo.Alignment,
                    };
                else
                    typography = new JsonObject { ["measured"] = false, ["reason"] = "a scan carries no font data, and no vision observation was made for this line" };
                var node = new JsonObject
                {
                    ["alias"] = l.Alias, ["origin"] = "OCR_DERIVED", ["page"] = l.Page,
                    ["text"] = new JsonObject { ["chars"] = l.Primary.Text.Length, ["words"] = l.Primary.Words.Count },
                    ["layout"] = layout, ["ocr"] = ocr, ["typography"] = typography,
                };
                if (chosen.TryGetValue(l.Alias, out var c))
                    node["visionReading"] = new JsonObject { ["origin"] = "VLM_OBSERVATION", ["choice"] = c.Choice, ["proposedReading"] = c.ProposedReading is { Length: > 0 } };
                var key = Evidence_RepeatKey(l.Primary.Text);
                if (key.Length > 0 && repeat[key].Where(j => j != i).ToArray() is { Length: > 0 } others)
                    node["repetition"] = new JsonObject
                    {
                        ["otherOccurrences"] = others.Length,
                        ["sampleAliases"] = new JsonArray(others.Take(2).Select(j => (JsonNode)ScanLines[j].Alias).ToArray()),
                    };
                result[l.Alias] = node;
            }
        }
        return result;
    }

    static string Evidence_RepeatKey(string text) => HeadingAgent.Evidence.RepeatKey(text);
}
