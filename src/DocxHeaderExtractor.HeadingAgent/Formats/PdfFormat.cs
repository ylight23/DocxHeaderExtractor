using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>
/// The PDF flavor: V6 tools and measurements, with the targets' existing facts supplied in the first request.
/// </summary>
internal sealed class PdfFormat(bool inlineGrouping = false, bool sourceBound = false, bool requirePageLayoutEvidence = false, bool preloadPageLayoutEvidence = false, bool includeLayoutBlockEvidence = false, bool unitSelection = false, bool rasterGeometry = false, bool adaptiveVisualEvidence = false) : IAgentFormat
{
    public static readonly PdfFormat Instance = new();
    public static readonly PdfFormat WithInlineGrouping = new(true);
    public static readonly PdfFormat WithSourceBinding = new(sourceBound: true);
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string SystemPrompt
    {
        get
        {
            var prompt = unitSelection ? PdfUnitSelectionProtocol.SystemPrompt : sourceBound ? SourceBoundSelection.SystemPrompt : inlineGrouping ? PdfAgentProtocol.SystemPrompt.Replace(AgentProtocol.SelectExtentVersion,
                InlineGrouping.Version, StringComparison.Ordinal) + "\n" + InlineGrouping.Prompt : PdfAgentProtocol.SystemPrompt;
            if (rasterGeometry) prompt += DocxHeaderExtractor.HeadingAgent.Runtime.PdfRasterEvidence.Prompt;
            if (preloadPageLayoutEvidence)
                return prompt.Replace("If they are enough, answer directly; do not call a tool for its own sake.",
                    "The program already executed the source tools and completed pagination before this classification request. sourceEvidence contains their actual results and provenance, covering every target with neighboring source lines. Read them before classifying. You may answer directly from this complete evidence; use additional tools only for a specific unresolved source relationship. Cite source aliases, never tool evidenceId values.", StringComparison.Ordinal);
            return requirePageLayoutEvidence
                ? prompt.Replace("If they are enough, answer directly; do not call a tool for its own sake.",
                    "For this candidate, a successful get_page_layout result showing every target alias is required before any final JSON, even when the request appears sufficient. Call get_page_layout for each target's page and continue with nextOffset until that target alias appears. Read the returned text and layout with the surrounding lines before classifying; the tool call is mandatory source evidence, not an optional check.", StringComparison.Ordinal)
                : prompt;
        }
    }
    public string AnswerVersion => sourceBound ? SourceBoundSelection.Version : inlineGrouping ? InlineGrouping.Version : AgentProtocol.SelectExtentVersion;
    public bool HeadingExtents => true;
    public bool RequiresCoreFacts => false;
    public bool RequiresPageLayoutEvidence => requirePageLayoutEvidence;
    public bool PreloadPageLayoutEvidence => preloadPageLayoutEvidence;
    public bool UnitSelection => unitSelection;
    public JsonArray? PayloadFacts(IAgentSource source, IReadOnlyList<string> targets)
    {
        var facts = AgentRequestFacts.Read(source, targets);
        if (includeLayoutBlockEvidence) PdfLayoutBlockEvidence.Attach(facts, (AgentSource)source);
        return adaptiveVisualEvidence ? DocxHeaderExtractor.HeadingAgent.Runtime.PdfAdaptiveEvidence.Compact(facts) : facts;
    }
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

    public string GroupPrompt => GroupingPrompts.Pdf;
    public object GroupLineItem(IAgentSource source, int index) => GroupLineItem(source, index, null);
    public object GroupLineItem(IAgentSource source, int index, TextOffsetSpan? headingExtent)
    {
        var line = source.Lines[index];
        var (span, selectedText) = SelectedHeadingText.Resolve(line.Alias, line.Text, headingExtent);
        return new { alias = line.Alias, page = line.Page ?? 0, text = selectedText, selectedText,
            headingExtent = new { start = span.Start, end = span.End }, sourceText = line.Text,
            layout = Evidence.CompactLayout(line.Bbox, line.Typography) };
    }
    public object GroupContextItem(IAgentSource source, int index) =>
        new { alias = source.Lines[index].Alias, page = source.Lines[index].Page ?? 0, text = AgentGrouping.Clip(source.Lines[index].Text), selected = false };
    public List<int[]> FallbackDivision(IAgentSource source, int[] lines) => AgentGrouping.LayoutRule((AgentSource)source, lines);
}
