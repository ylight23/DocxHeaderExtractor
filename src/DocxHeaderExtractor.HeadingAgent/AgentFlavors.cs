using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The PDF flavor: the qualified V6 prompt and tools, byte for byte (the parity tests hold them to the qualified SHA-256s). A line of the
/// payload is its alias, page and text; everything about its layout comes from the facts tool.
/// </summary>
internal sealed class PdfAgentFlavor : IAgentFlavor
{
    public static readonly PdfAgentFlavor Instance = new();
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string SystemPrompt => AgentProtocol.SystemPrompt;
    public JsonArray ToolDefinitions() => AgentTools.Definitions();
    public IAgentTools CreateTools(IAgentSource source) => new AgentTools((AgentSource)source);
    public object PayloadItem(AgentSourceLine line) => new { alias = line.Alias, page = line.Page ?? 0, text = line.Text };

    public HeadingPlacementContext PlacementContext(IAgentSource source)
    {
        var pdf = (AgentSource)source;
        return new(new AgentTools(pdf).DocumentStyles().ToJsonString(Compact),
            pdf.LayoutFacts.Where(kv => kv.Value["style"] is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value["style"]!.ToJsonString(Compact), StringComparer.Ordinal));
    }

    public string GroupPrompt => AgentGrouping.GroupPrompt;
    public object GroupLineItem(IAgentSource source, int index) =>
        new { alias = source.Lines[index].Alias, page = source.Lines[index].Page ?? 0, text = source.Lines[index].Text, layout = Evidence.CompactLayout(source.Lines[index].Bbox, source.Lines[index].Typography) };
    public object GroupContextItem(IAgentSource source, int index) =>
        new { alias = source.Lines[index].Alias, page = source.Lines[index].Page ?? 0, text = AgentGrouping.Clip(source.Lines[index].Text), selected = false };
    public List<int[]> FallbackDivision(IAgentSource source, int[] lines) => AgentGrouping.LayoutRule((AgentSource)source, lines);
}
