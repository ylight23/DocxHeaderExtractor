using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The document an agent run reads: its lines in reading order under the aliases of the route's source snapshot, the identity hashes, the
/// segments (the targets decided per conversation and the context shown with them) and the flavor that says how this kind of document is
/// presented to the model. A PDF source and a DOCX source implement it; the runner, the grouping step and the selector depend only on this.
/// </summary>
internal interface IAgentSource
{
    string SourceSha256 { get; }
    string SourceAliasUniverseSha256 { get; }
    string EvidenceStoreSha256 { get; }
    /// <summary>The lines (PDF) or paragraphs (DOCX) in reading order. A DOCX paragraph has no page, bbox or typography.</summary>
    IReadOnlyList<AgentSourceLine> Lines { get; }
    /// <summary>The decided range of each segment, [Start, End) into <see cref="Lines"/>, covering every line once.</summary>
    IReadOnlyList<(int Start, int End)> Cores { get; }
    /// <summary>The targets of one segment and the lines shown with them, as line indices.</summary>
    (string[] Decide, int[] Shown) Scope(int segment);
    IAgentFlavor Flavor { get; }
}

/// <summary>
/// What differs between kinds of document in the method: the system prompt, the declared tools (and the object that runs them), and how a line
/// appears in a request. The conversation itself - forced facts call, tool rounds, repairs, retries, budget - is the same for every kind.
/// </summary>
internal interface IAgentFlavor
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

/// <summary>
/// The read-only evidence tools of one unit's conversation. Every call is recorded; every result is an exact JSON body that says when it was
/// cut; no result labels a line.
/// </summary>
internal interface IAgentTools
{
    int PositionOf(string alias);
    /// <summary>The style table of the whole document, which goes into the request.</summary>
    JsonObject DocumentStyles();
    /// <summary>The names the loop reserves rejection room for in the tool rounds that are still to come.</summary>
    IReadOnlyList<string> ReservedNames { get; }
    int MaxResultBytes { get; }
    int RejectionBytes(string evidenceId, string? name);
    AgentToolHost.Result Reject(string evidenceId, string? name, string? rawArguments, string code);
    AgentToolHost.Result Execute(string evidenceId, string? name, string? rawArguments);
    /// <summary>Every alias a successful tool result showed.</summary>
    IReadOnlySet<string> ObservedAliases { get; }
    /// <summary>Whether a call to the facts tool has succeeded: the first answer is refused before that.</summary>
    bool FactsWereRead { get; }
    /// <summary>The ledger of evidence coverage: every line whose own facts a successful facts call returned (not the lines a result only
    /// mentions). It says which targets the model has read the facts of; it decides nothing.</summary>
    IReadOnlySet<string> FactsReturnedFor { get; }
    /// <summary>Successful facts calls whose result was cut (hasMore true or truncated).</summary>
    int FactsResultsCut { get; }
    /// <summary>The detail evidence the model asked for: every alias that a successful call of a tool other than the facts tool showed. Not required
    /// of any target; it records what the model chose to read.</summary>
    IReadOnlySet<string> DetailEvidenceAliases { get; }
}
