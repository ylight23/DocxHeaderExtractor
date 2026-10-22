using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>
/// What differs between kinds of document in the method: the system prompt, the declared tools (and the object that runs them), and how a line
/// appears in a request. The conversation itself - facts supplied in the request, optional tool rounds, repairs, retries, budget - is the same for every kind.
/// </summary>
internal interface IAgentFormat
{
    string SystemPrompt { get; }
    /// <summary>The protocolVersion of this flavor's answer format.</summary>
    string AnswerVersion { get; }
    /// <summary>Whether a SELECT may carry headingExtent, the part of the line's text that is the heading (checked against the text, never moved).</summary>
    bool HeadingExtents { get; }
    /// <summary>Whether a COMPLETE answer needs the core facts of every target (DOCX). An answer that leaves targets without is sent back once, naming
    /// them, while another facts result still fits the unit; if they stay uncovered, or the unit has no room left to read them, the unit ends INCOMPLETE
    /// and none of its decisions is used. Off for the PDF: its conversation is the qualified one, held to the qualified request bytes, so there the
    /// coverage ledger is recorded only.</summary>
    bool RequiresCoreFacts { get; }
    /// <summary>The readings the evidence holds for one line, by view id (a scanned source: its OCR views and a reading a VLM proposed); null when a
    /// line has only its own text (PDF, DOCX). A SELECT may then name one with readingViewId, and its extent is measured on it.</summary>
    IReadOnlyDictionary<string, string>? Readings(IAgentSource source, string alias) => null;
    /// <summary>Failure codes (matched as parts of the call's failure code) for which a call is sent once more, the same bytes, beyond the transport
    /// retries every route has. The scan route only (owner's decision 2026-10-16): REASONING_EXHAUSTED (the model's reasoning used every output
    /// token; one unit lost in each of two live runs on 023) and TIMEOUT_OR_CANCELLED (the call did not end in time; one unit lost in the next).
    /// Empty for PDF and DOCX, whose runs stay as they were qualified.</summary>
    IReadOnlyList<string> RetryOnceCodes => [];
    /// <summary>The core facts of the targets, put into the first request (targetFacts) so that the model need not ask for them, in the form the
    /// facts tool returns them. All production routes supply them; a null result retains the facts-tool prerequisite.</summary>
    JsonArray? PayloadFacts(IAgentSource source, IReadOnlyList<string> targets) => null;
    /// <summary>References whose observations a facts entry actually shows, without claiming their full core facts were read.</summary>
    IEnumerable<string> FactReferences(JsonNode facts) =>
        facts["repetition"]?["sampleAliases"]?.AsArray().Select(a => a!.GetValue<string>()) ?? [];
    JsonArray ToolDefinitions();
    IAgentTools CreateTools(IAgentSource source);
    /// <summary>One line of the targets or the context in the unit payload.</summary>
    object PayloadItem(AgentSourceLine line);

    // The grouping step (which selected lines form one heading) differs only in how a line is shown and in the rule used for a run the model
    // could not divide.
    string GroupPrompt { get; }
    /// <summary>A line of a run in the grouping request.</summary>
    object GroupLineItem(IAgentSource source, int index);
    /// <summary>The line next to a run, shown only as context (selected false).</summary>
    object GroupContextItem(IAgentSource source, int index);
    /// <summary>The measurements the placement pass gets: the document's style table and the style of every line that has one. Facts only.</summary>
    HeadingPlacementContext PlacementContext(IAgentSource source);
    /// <summary>How a run divides into headings when the model's division was refused or could not be asked for: contiguous groups of line indices.</summary>
    List<int[]> FallbackDivision(IAgentSource source, int[] lines);
}
