using static DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol;
using DocxHeaderExtractor.HeadingAgent.Protocol;

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

    const string ToolsFirst = "Evidence tools: the functions declared in this request read the evidence the program stored for the scan before this conversation: the lines before and after a line, a text search over every OCR view, every reading of a line (each OCR view, where they differ, the vision transcription and whether it passed the checks, and the vision model's choice and proposed reading when one was asked), how the document and its readings were made, and an image of lines cut from their page image (get_scan_crop). They return observations only, never labels or decisions.";

    const string FactsToolLine = "Facts: the program measured facts for every line, each from the line itself. The request carries targetFacts: the facts of every target, one entry per target in the order of targets. Read them before you decide; you need not ask for them. get_line_facts gives the facts of any lines, a context line for example (one call covers at most 24 lines; a result that was cut says hasMore true and nextOffset). For each line the facts give: its page; its box and the page size, with its margins; drawnFrame, the rectangle drawn on the page that encloses the line (a frame or a table border, measured on the page image), absent when no drawn frame encloses it; its typography, measured on the page image from its own box (the width of its strokes in pixels, the same per pixel of its height, the share of its box that is dark, and the number of runs); the reading its text is and how the vision transcription relates to the OCR views; its leading token, its numbered prefix and its ending punctuation; and, only when a vision model was asked, its observation (marked VLM_OBSERVATION: a model's judgement, not a measurement). The vision model's reading choice when one was asked. They are measurements and observations; what they mean for the instruction is for you to judge. You may cite the aliases they show in evidenceRefs. You may also call the other tools for anything else.";

    const string DocumentStylesLine = "Document styles: a scan has no font styles, so documentStyles lists none; what is known of a line's typography is in its facts.";

    const string UseFactsLine = "Use the evidence: before you decide a target, read its facts and the facts of the lines next to it. Compare the target with the lines next to it, not with its own text alone. A misread character does not by itself change what a line is: read the other views. Base each decision on the instruction and on this evidence together.";

    const string ReadingLine = "readingViewId: get_text_views lists every reading of a line by viewId: each OCR view, vlm-transcribed (the vision model's blind transcription, when it passed the checks) and, when the vision model proposed one, vlm-proposed (a correction candidate nobody has confirmed). On a SELECT you may give readingViewId to take the reading you judge right; leave it out to take the line as the request shows it. Take only a listed reading: never write a reading of your own. The output records which reading you took, whether other readings agree with it, and keeps the ocr-original reading beside it.";

    const string ExtentLine = "headingExtent: a line can hold more than its heading. On a SELECT whose reading holds text that is not part of the heading, give headingExtent: start and end are offsets in UTF-16 code units into the text of the reading you take (the readingViewId you give, else the text the request shows), end is exclusive, and text is exactly that part. Omit it only when the whole reading is heading text. The program checks every extent against the reading and refuses one that does not match it; it never moves an extent.";

    internal static readonly string AnswerBody = BodyWithAnalysis(SelectScanVersion)
        .Replace("\"reason\":\"<why this line is such a heading, at most 300 characters>\"}", "\"reason\":\"<why this line is such a heading, at most 300 characters>\",\"readingViewId\":\"<a viewId from get_text_views>\",\"headingExtent\":{\"start\":0,\"end\":<offset where the heading ends>,\"text\":\"<the heading's exact text in that reading>\"}}", StringComparison.Ordinal)
        .Replace("Do not add headings, levels, parents or other fields.", "A SELECT may also carry readingViewId and headingExtent, as described above. Do not add headings, levels, parents or other fields.", StringComparison.Ordinal);

    public static readonly string SystemPrompt = string.Join("\n", Intro, Fields + RangeSentence, MembershipOnly, ToolsLineFor(ToolsFirst), FactsToolLine, DocumentStylesLine,
        UseFactsLine, ReadingLine, ExtentLine, ObservedLine, ReplyLine, AnswerBody);

    /// <summary>The instruction a scan run sends: the DOCX one (structural headings, formatting as one kind of evidence, the exact extent).</summary>
    public const string Instruction = DocxAgentProtocol.Instruction + "\n" + BoilerplateParagraph;

    /// <summary>A line that repeats word for word elsewhere in the document and names no unit of its own is not a heading, even where it stands
    /// directly above one: the heading is the line that names the unit. The repetition count in its facts is a measurement, not a decision.</summary>
    const string BoilerplateParagraph = "A line that carries the same text word for word in other places of the document (its repetition count in the facts) and names no unit of its own is not a heading, even when it stands directly above one: the heading is the line that names the unit. The repetition count is a measurement; it decides nothing by itself.";

    public const string PlacementNote = "Each heading may carry \"layout\" (its box and the page size, and its margins) and \"typography\" (stroke width, stroke per height and ink share, measured on the page image from its own box). These are measurements from the image. A scanned document declares no font size, no bold and no outline level, so none of those is given here.";
}
