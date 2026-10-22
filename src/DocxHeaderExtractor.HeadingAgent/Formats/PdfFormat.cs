using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>
/// The PDF flavor: V6 tools and measurements, with the targets' existing facts supplied in the first request.
/// </summary>
internal sealed class PdfFormat : IAgentFormat
{
    public static readonly PdfFormat Instance = new();
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string SystemPrompt => AgentProtocol.SystemPrompt;
    public string AnswerVersion => AgentProtocol.SelectObjectsVersion;
    public bool HeadingExtents => false;
    public bool RequiresCoreFacts => false;
    public JsonArray? PayloadFacts(IAgentSource source, IReadOnlyList<string> targets) => AgentRequestFacts.Read(source, targets);
    public IEnumerable<string> FactReferences(JsonNode facts) =>
        facts["repeatedText"]?["shown"]?.AsArray().SelectMany(x =>
            new[] { x!["alias"]!.GetValue<string>() }.Concat(x["followingLine"] is JsonObject following
                ? new[] { following["alias"]!.GetValue<string>() } : [])) ?? [];
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

    public string GroupPrompt => GroupingPrompts.Shared;
    public object GroupLineItem(IAgentSource source, int index) =>
        new { alias = source.Lines[index].Alias, page = source.Lines[index].Page ?? 0, text = source.Lines[index].Text, layout = Evidence.CompactLayout(source.Lines[index].Bbox, source.Lines[index].Typography) };
    public object GroupContextItem(IAgentSource source, int index) =>
        new { alias = source.Lines[index].Alias, page = source.Lines[index].Page ?? 0, text = AgentGrouping.Clip(source.Lines[index].Text), selected = false };
    public List<int[]> FallbackDivision(IAgentSource source, int[] lines) => AgentGrouping.LayoutRule((AgentSource)source, lines);
}
