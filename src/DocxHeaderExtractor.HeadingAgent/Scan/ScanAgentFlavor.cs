using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>
/// The system prompt of the scan flavor. The method is the V6 one (the instruction decides what a heading is; the first reply reads the facts;
/// at most three tool rounds; a strict answer), and the model is the one that decides: which lines are headings, where each heading ends, and
/// which of the recorded readings of a line it takes. What is the scan's own is what the model is told it can read: OCR lines of page images,
/// their geometry and confidences, the readings of several OCR views and, when one was asked, a vision model's observations, each marked with
/// its origin, and the vision model's blind transcription of each line, shown as the line's text where it passed the program's checks. No
/// reading was edited by the program.
/// </summary>
internal static class ScanAgentProtocol
{
    const string Fields = "The request gives stage, targetCount (the number of targets), targets (the lines you must decide: one decision for each) and context (lines shown only as evidence: never decide them). The document is a scan: it has no text of its own, and a line is one line that OCR found on a page image, in reading order. Each line has alias (page and line, for example P003-L012), page and text. The text is the reading the program shows: the vision model's blind transcription of the line's crop (vlm-transcribed: it saw only the image, no OCR reading) when that transcription passed the program's checks, else the reading of the primary OCR view (ocr-original); the facts say which (text.shownReading) and which OCR views read the same. No reading was edited by the program. Every alias appears once: in targets or in context, never in both.";

    const string ToolsFirst = "Evidence tools: the functions declared in this request read the evidence the program stored for the scan before this conversation: the lines before and after a line, a text search over every OCR view, every reading of a line (each OCR view, where they differ, the vision transcription and whether it passed the checks, and the vision model's choice and proposed reading when one was asked), the numbered sequence a line belongs to, and how the document and its readings were made. They return observations only, never labels or decisions.";

    const string FactsToolLine = "Facts: the request carries targetFacts: the facts the program measured for every target, one entry per target in the order of targets, in the form get_line_facts returns them. Read them before you decide; you need not ask for them. get_line_facts gives the same facts for any other line, a context line for example (one call covers at most 24 lines; a result that was cut says hasMore true and nextOffset). For each line the facts give: its page; its layout, measured on the page image (box in pixels, page size, its left edge against the edge most lines of the page share and how many lines share it, its margins and how far it is from centred, the gaps above and below it, its height against the median line of the page); which reading its text is and how the vision transcription relates to the OCR views; its OCR (mean and lowest word confidence, low-confidence words, whether another line overlaps it, how many views cover it and whether they read it differently); its typography, which a scan does not carry as fonts, measured on the page image (MEASURED_ON_IMAGE: the width of its strokes in pixels and against the median line of the page, the same per unit of line height, and how much of its box is ink against the page; thick strokes are what bold type looks like) and, only when a vision model was asked, its observation (bold or not, larger or smaller than its neighbours, alignment; marked VLM_OBSERVATION: a model's judgement, not a measurement); the vision model's reading choice when one was asked; and a repetition summary. They are measurements and observations, not labels or decisions; what they mean for the instruction is for you to judge. You may cite the aliases they show in evidenceRefs. You may also call the other tools for anything else.";

    const string DocumentStylesLine = "Document styles: a scan has no font styles, so documentStyles lists none; what is known of a line's typography is in its facts.";

    const string UseFactsLine = "Use the evidence: before you decide a target, read its facts and those of the lines next to it - its place and spacing on the page, its OCR confidence, whether the views agree, the vision observation if there is one, and the numbered sequence it belongs to. A misread label (for example a wrong diacritic, or a comma for a full stop) does not by itself change what a line is: read the other views and the sequence. Base each decision on the instruction and on this evidence together.";

    const string ReadingLine = "readingViewId: get_text_views lists every reading of a line by viewId: each OCR view, vlm-transcribed (the vision model's blind transcription, when it passed the checks) and, when the vision model proposed one, vlm-proposed (a correction candidate nobody has confirmed). On a SELECT you may give readingViewId to take the reading you judge right; leave it out to take the line as the request shows it. Take only a listed reading: never write a reading of your own. The output records which reading you took, whether other readings agree with it, and keeps the ocr-original reading beside it.";

    const string ExtentLine = "headingExtent: a line can hold more than its heading. On a SELECT whose reading holds text that is not part of the heading, give headingExtent: start and end are offsets in UTF-16 code units into the text of the reading you take (the readingViewId you give, else the text the request shows), end is exclusive, and text is exactly that part. Omit it only when the whole reading is heading text. The program checks every extent against the reading and refuses one that does not match it; it never moves an extent.";

    internal static readonly string AnswerBody = BodyWithAnalysis(SelectScanVersion)
        .Replace("\"reason\":\"<why this line is such a heading, at most 300 characters>\"}", "\"reason\":\"<why this line is such a heading, at most 300 characters>\",\"readingViewId\":\"<a viewId from get_text_views>\",\"headingExtent\":{\"start\":0,\"end\":<offset where the heading ends>,\"text\":\"<the heading's exact text in that reading>\"}}", StringComparison.Ordinal)
        .Replace("Do not add headings, levels, parents or other fields.", "A SELECT may also carry readingViewId and headingExtent, as described above. Do not add headings, levels, parents or other fields.", StringComparison.Ordinal);

    public static readonly string SystemPrompt = string.Join("\n", Intro, Fields + RangeSentence, MembershipOnly, ToolsLineFor(ToolsFirst), FactsToolLine, DocumentStylesLine,
        UseFactsLine, ReadingLine, ExtentLine, ObservedLine, ReplyLine, AnswerBody);

    /// <summary>The instruction a scan run sends: the DOCX one (structural headings, formatting as one kind of evidence, the exact extent).</summary>
    public const string Instruction = DocxAgentProtocol.Instruction;

    public const string PlacementNote = "A scanned document carries no style objects: its headings have no style here.";
}

/// <summary>The scan flavor: lines of page images, the scan tools, the scan answer format with readings, and the readings of each line.</summary>
internal sealed class ScanAgentFlavor : IAgentFlavor
{
    public static readonly ScanAgentFlavor Instance = new();

    public string SystemPrompt => ScanAgentProtocol.SystemPrompt;
    public string AnswerVersion => SelectScanVersion;
    public bool HeadingExtents => true;
    public bool RequiresCoreFacts => true;
    public IReadOnlyList<string> RetryOnceCodes { get; } = ["REASONING_EXHAUSTED", "TIMEOUT_OR_CANCELLED"];

    /// <summary>The targets' facts in the first request: the same objects get_line_facts returns (about 700 bytes a line; a unit's targets are
    /// cut so that theirs stay under 28 KB).</summary>
    public JsonArray? PayloadFacts(IAgentSource source, IReadOnlyList<string> targets) =>
        new(targets.Select(a => (JsonNode)((ScanAgentSource)source).Facts[a].DeepClone()).ToArray());
    public IEnumerable<string> FactReferences(JsonNode facts) =>
        (facts["repetition"]?["sampleAliases"]?.AsArray().Select(a => a!.GetValue<string>()) ?? [])
            .Concat(ScanTextMeasurements.ReferencedAliases(facts));
    public JsonArray ToolDefinitions() => ScanAgentTools.Definitions();
    public IAgentTools CreateTools(IAgentSource source) => new ScanAgentTools((ScanAgentSource)source);
    public object PayloadItem(AgentSourceLine line) => new { alias = line.Alias, page = line.Page ?? 0, text = line.Text };
    public IReadOnlyDictionary<string, string>? Readings(IAgentSource source, string alias) => ((ScanAgentSource)source).Readings(alias);

    public string GroupPrompt => AgentGrouping.GroupPrompt;

    public object GroupLineItem(IAgentSource source, int index)
    {
        var l = ((ScanAgentSource)source).ScanLines[index];
        return new
        {
            alias = l.Alias, page = l.Page, text = source.Lines[index].Text,
            layout = new { left = l.Primary.Box.Left, top = l.Primary.Box.Top, width = l.Primary.Box.Width, height = l.Primary.Box.Height, unit = "page image pixels" },
        };
    }

    public object GroupContextItem(IAgentSource source, int index) =>
        new { alias = source.Lines[index].Alias, page = source.Lines[index].Page ?? 0, text = AgentGrouping.Clip(source.Lines[index].Text), selected = false };

    public HeadingPlacementContext PlacementContext(IAgentSource source)
    {
        var scan = (ScanAgentSource)source;
        return new HeadingPlacementContext(new JsonObject { ["kind"] = "scan", ["documentLines"] = scan.Lines.Count }.ToJsonString(),
            scan.Facts.ToDictionary(p => p.Key, p => new JsonObject
            {
                ["layout"] = p.Value["layout"]?.DeepClone(), ["typography"] = p.Value["typography"]?.DeepClone(),
            }.ToJsonString(), StringComparer.Ordinal), ScanAgentProtocol.PlacementNote);
    }

    /// <summary>A run the model could not divide stays as separate headings: geometry alone does not say two lines are one heading.</summary>
    public List<int[]> FallbackDivision(IAgentSource source, int[] lines) => lines.Select(i => new[] { i }).ToList();
}
