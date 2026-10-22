using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Routing;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>The scan flavor: lines of page images, the scan tools, the scan answer format with readings, and the readings of each line.</summary>
internal sealed class ScanAgentFlavor : IAgentFlavor
{
    public static readonly ScanAgentFlavor Instance = new();

    public string SystemPrompt => ScanAgentProtocol.SystemPrompt;
    public string AnswerVersion => SelectScanVersion;
    public bool HeadingExtents => true;
    public bool RequiresCoreFacts => true;
    public IReadOnlyList<string> RetryOnceCodes { get; } = ["REASONING_EXHAUSTED", "TIMEOUT_OR_CANCELLED"];

    /// <summary>The targets' facts in the first request: the same objects get_line_facts returns (about 700 bytes a line; a unit's targets are
    /// cut so that theirs stay under 28 KB).</summary>
    public JsonArray? PayloadFacts(IAgentSource source, IReadOnlyList<string> targets) =>
        new(targets.Select(a => (JsonNode)((ScanAgentSource)source).Facts[a].DeepClone()).ToArray());
    public IEnumerable<string> FactReferences(JsonNode facts) => [];
    public JsonArray ToolDefinitions() => ScanAgentTools.Definitions();
    public IAgentTools CreateTools(IAgentSource source) => new ScanAgentTools((ScanAgentSource)source);
    public object PayloadItem(AgentSourceLine line) => new { alias = line.Alias, page = line.Page ?? 0, text = line.Text };
    public IReadOnlyDictionary<string, string>? Readings(IAgentSource source, string alias) => ((ScanAgentSource)source).Readings(alias);

    public string GroupPrompt => GroupingPrompts.Scan;

    public object GroupLineItem(IAgentSource source, int index)
    {
        var scan = (ScanAgentSource)source;
        var l = scan.ScanLines[index];
        // The space above is taken from the line above on the same page; the first line of a page has none.
        int? gap = index > 0 && scan.ScanLines[index - 1].Page == l.Page ? l.Primary.Box.Top - scan.ScanLines[index - 1].Primary.Box.Bottom : null;
        // The same position measures the selection gets: its margins and offset from centre, from its own box and the page width.
        int? right = l.PageWidth > 0 ? l.PageWidth - l.Primary.Box.Right : null;
        return new
        {
            alias = l.Alias, page = l.Page, text = source.Lines[index].Text,
            layout = new
            {
                left = l.Primary.Box.Left, top = l.Primary.Box.Top, width = l.Primary.Box.Width, height = l.Primary.Box.Height, unit = "page image pixels",
                gapAbovePx = gap, leftMarginPx = right is null ? null : (int?)l.Primary.Box.Left, rightMarginPx = right,
                centredOffsetPx = right is { } r ? (int?)((l.Primary.Box.Left - r) / 2) : null,
                drawnFrame = scan.Frames.TryGetValue(l.Primary.Id, out var f) ? new { left = f.Left, top = f.Top, width = f.Width, height = f.Height } : null,
            },
        };
    }

    public object GroupContextItem(IAgentSource source, int index) =>
        new { alias = source.Lines[index].Alias, page = source.Lines[index].Page ?? 0, text = AgentGrouping.Clip(source.Lines[index].Text), selected = false };

    public HeadingPlacementContext PlacementContext(IAgentSource source)
    {
        var scan = (ScanAgentSource)source;
        return new HeadingPlacementContext(new JsonObject { ["kind"] = "scan", ["documentLines"] = scan.Lines.Count }.ToJsonString(),
            scan.Facts.ToDictionary(p => p.Key, p => new JsonObject
            {
                ["layout"] = p.Value["layout"]?.DeepClone(), ["typography"] = p.Value["typography"]?.DeepClone(),
            }.ToJsonString(), StringComparer.Ordinal), ScanAgentProtocol.PlacementNote);
    }

    /// <summary>A run the model could not divide stays as separate headings: geometry alone does not say two lines are one heading.</summary>
    public List<int[]> FallbackDivision(IAgentSource source, int[] lines) => lines.Select(i => new[] { i }).ToList();
}
