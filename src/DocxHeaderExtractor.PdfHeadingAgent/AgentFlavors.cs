using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.PdfHeadingAgent;

/// <summary>
/// The PDF flavor: the qualified V6 prompt and tools, byte for byte (the parity tests hold them to the qualified SHA-256s). A line of the
/// payload is its alias, page and text; everything about its layout comes from the facts tool.
/// </summary>
internal sealed class PdfAgentFlavor : IAgentFlavor
{
    public static readonly PdfAgentFlavor Instance = new();

    public string SystemPrompt => AgentProtocol.SystemPrompt;
    public JsonArray ToolDefinitions() => AgentTools.Definitions();
    public IAgentTools CreateTools(IAgentSource source) => new AgentTools((AgentSource)source);
    public object PayloadItem(AgentSourceLine line) => new { alias = line.Alias, page = line.Page ?? 0, text = line.Text };
}
