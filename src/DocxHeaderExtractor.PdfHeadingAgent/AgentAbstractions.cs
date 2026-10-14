using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.PdfHeadingAgent;

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
    JsonArray ToolDefinitions();
    IAgentTools CreateTools(IAgentSource source);
    /// <summary>One line of the targets or the context in the unit payload.</summary>
    object PayloadItem(AgentSourceLine line);
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
}
