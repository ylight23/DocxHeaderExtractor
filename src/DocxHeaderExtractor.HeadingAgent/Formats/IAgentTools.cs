using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

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
    /// <summary>Aliases actually returned by successful get_page_layout calls in this unit.</summary>
    IReadOnlySet<string> PageLayoutAliasesReturned { get; }
    /// <summary>Whether facts were supplied in the request or a facts-tool call succeeded.</summary>
    bool FactsWereRead { get; }
    /// <summary>Whether a facts-tool call succeeded: reading facts from the request does not count (the scan route requires the call).</summary>
    bool ToolFactsRead { get; }
    /// <summary>The images the tool calls since the last take returned (a scan crop), in call order; they travel in a user message after the tool results.</summary>
    IReadOnlyList<JsonObject> TakeImages();
    /// <summary>The ledger of evidence coverage: every line whose own facts were supplied in the request or returned by a successful facts call (not the lines a result only
    /// mentions). It says which targets the model has read the facts of; it decides nothing.</summary>
    IReadOnlySet<string> FactsReturnedFor { get; }
    /// <summary>Successful facts calls whose result was cut (hasMore true or truncated).</summary>
    int FactsResultsCut { get; }
    /// <summary>The detail evidence the model asked for: every alias that a successful call of a tool other than the facts tool showed. Not required
    /// of any target; it records what the model chose to read.</summary>
    IReadOnlySet<string> DetailEvidenceAliases { get; }
    /// <summary>Enters facts the request carried into the coverage ledger, with the aliases they show (repetition/context references) as observed: the model
    /// has them from the first turn, as if a facts call had returned them.</summary>
    void NoteRequestFacts(IEnumerable<string> aliases, IEnumerable<string> shown);
}
