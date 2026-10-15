using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The system prompt and the measurement notes of the DOCX flavor. The method is the V6 one - the user's instruction decides what a heading is,
/// the first reply must read the facts through the facts tool, at most three tool rounds, then a strict answer - and most of the prompt is the
/// shared <see cref="AgentProtocol"/> text. What is DOCX's own is what the model is told it can read: paragraphs instead of lines on pages, and
/// the style, numbering and place in the XML instead of position and font.
/// </summary>
internal static class DocxAgentProtocol
{
    const string Fields = "The request gives stage, targetCount (the number of targets), targets (the lines you must decide: one decision for each) and context (lines shown only as evidence: never decide them). A line is one paragraph of the document, in reading order. Each line has alias and text only: this request shows no formatting, and the formatting of a line (its style, numbering and emphasis, and where it stands) comes from get_line_facts. Every alias appears once: in targets or in context, never in both.";

    const string ToolsFirst = "Evidence tools: the functions declared in this request read the program's record of the whole document: the lines before and after a line, the full record of a line (text, style, numbering, formatting), a text search, the other lines with the same text, two lines side by side, and the numbered sequence a line belongs to (lines whose first words share their form and differ in a number, with where the numbers skip, repeat or go down). They return observations only, never labels or decisions.";

    const string FactsToolLine = "Facts: this request carries no formatting facts. Your first reply must be tool calls to get_line_facts that read the core facts the program measured for the targets (from targetFirstAlias to targetLastAlias; one call covers at most 24 lines, and a result that was cut says hasMore true and nextOffset: call again with the same first and last aliases and that offset). An answer given before the core facts of every target have arrived is not accepted as complete. For each line the facts give: its style (style id and name, the outline level the style or the paragraph declares, font size, bold, italic, underline, all capitals, alignment), its numbering (the list, the level and the rendered label, when it has one), its text (characters, the share of characters set in bold, line breaks with their offsets; when its formatting changes inside the paragraph, the number of formatting runs and the offsets where it changes), where it stands (inside a table with its depth, row and cell, or inside a text box), the document-internal links it carries, how its style ranks among the styles of the document and how many lines share it, whether the lines before and after it share its style, how its font size compares with the median of the document, and, when its text also occurs on other lines, a repetition summary (how many other lines have the same normalized text and the aliases of the first two). The core facts do not carry everything: the pieces of text between line breaks and every formatting run with its exact start and end come from get_source_span, and the places of a repeated text and the lines around them from get_repeated_occurrences, compare_source_locations and get_source_context; ask for them where a decision needs them. They are measurements, not labels or decisions; what they mean for the instruction is for you to judge. All capitals is read from the paragraph's formatting or from its text. You may cite the aliases they show in evidenceRefs. You may also call the other tools for anything else.";

    const string DocumentStylesLine = "Document styles: documentStyles lists the distinct styles (style name, font size and weight) of the whole document, largest first, with how many lines use each and the first lines set in it. It describes the whole document, not only the lines shown here: use it to compare a target's style with the styles used in the rest of the document. It is a measurement, not a label.";

    const string UseFactsLine = "Use the facts: before you decide a target, read the facts get_line_facts returned for it and for the lines next to it - its style, numbering and emphasis, where it stands, how its style compares with the lines before and after it and with the whole document, and whether its text occurs elsewhere (then compare the places with the tools). Compare the target with its neighbours, not with its own text alone. Base each decision on the instruction and on this evidence together.";

    /// <summary>How a SELECT gives the part of its paragraph that is the heading. The model decides the extent; the program checks that the offsets
    /// lie in the target's text and that the text given is the text at those offsets, and refuses the answer otherwise (it never moves an extent).</summary>
    const string ExtentLine = "headingExtent: a paragraph can hold more than its heading, for example a heading that its body text follows in the same paragraph, on the same line or after a line break. On a SELECT whose target text holds text that is not part of the heading, give headingExtent: start and end are offsets in UTF-16 code units into the target's text exactly as the request shows it (a line break is one space there), end is exclusive, and text is exactly the target's text from start to end. The offsets get_line_facts gives (lineBreakOffsets, formatChangeOffsets) and the ones get_source_span gives (pieces, formatRuns) are in the same units; when a paragraph has line breaks or changes of formatting and you cannot place the end of the heading from the text alone, read its record with get_source_span. Omit headingExtent only when the whole target text is heading text. The program checks every extent against the text and refuses one that does not match it; it never moves an extent.";

    /// <summary>The answer format: the shared one under <see cref="SelectExtentVersion"/>, with headingExtent on a SELECT.</summary>
    internal static readonly string ExtentBody = BodyWithAnalysis(SelectExtentVersion)
        .Replace("\"reason\":\"<why this line is such a heading, at most 300 characters>\"}", "\"reason\":\"<why this line is such a heading, at most 300 characters>\",\"headingExtent\":{\"start\":0,\"end\":<offset where the heading ends>,\"text\":\"<the heading's exact text>\"}}", StringComparison.Ordinal)
        .Replace("Do not add headings, levels, parents or other fields.", "A SELECT may also carry headingExtent, as described above. Do not add headings, levels, parents or other fields.", StringComparison.Ordinal);

    public static readonly string SystemPrompt = string.Join("\n", Intro, Fields + RangeSentence, MembershipOnly, ToolsLineFor(ToolsFirst), FactsToolLine, DocumentStylesLine,
        UseFactsLine, ExtentLine, ObservedLine, ReplyLine, ExtentBody);

    /// <summary>The paragraph of the DOCX instruction on a heading that shares its paragraph with body text: the heading is decided by its function
    /// and only its own text is the heading. It goes with <see cref="ExtentLine"/>, the answer field that can say where the heading ends.</summary>
    public const string HeadingExtentParagraph =
        "A paragraph may contain both a structural heading and body text. Identify the heading by its structural function and determine its exact extent. Include only the text that belongs to the heading, even when body text continues in the same paragraph. Do not treat the entire paragraph as heading text merely because it begins with a heading.";

    /// <summary>The instruction a DOCX run sends: the production structural instruction, formatting as one kind of evidence among several, and
    /// the extent of a heading that shares its paragraph with body text.</summary>
    public const string Instruction = AgentProtocol.InstructionStructuralV1 + "\n" + AgentProtocol.FormattingEvidenceParagraph + "\n" + HeadingExtentParagraph;

    /// <summary>The grouping prompt: the shared one, saying what a line carries in this kind of document.</summary>
    public static readonly string GroupPrompt = AgentGrouping.GroupPrompt.Replace(
        "Each line has alias, page, text and layout;",
        "Each line has alias, text and its style (style name, font size, bold, numbering label and level, alignment, when it has them);", StringComparison.Ordinal)
        + "\n" + GroupingFactsLine;

    /// <summary>Two measurements of the document, given to every line of a grouping request. They describe the document; they say nothing about what
    /// a heading is, and no example is given.</summary>
    const string GroupingFactsLine = "Each line also carries shape: its first two words, with every number or roman numeral written as #. And shapeCount: how many lines of the whole document have that shape. Both are measurements of the document, not labels; what they mean for the division is for you to judge.";

    /// <summary>What the style objects of the placement pass hold for a DOCX (the default sentence describes a PDF's size, weight and rank).</summary>
    public const string PlacementNote = "Each heading may also carry \"style\" (the style name, the outline level it declares, sizePt, bold, italic, allCaps, alignment, the numbering label and level, rankInDocument: the place of its style among the distinct styles of the whole document, 1 being the largest, and linesWithThisStyleInDocument), and \"documentStyles\" lists those distinct styles with the first lines set in each. They are measurements of the document, not labels and not decisions: you decide each relation.";
}
