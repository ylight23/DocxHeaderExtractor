using System.Text.Encodings.Web;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The DOCX flavor: the same conversation as a PDF's, presented as paragraphs. A line of the payload is its alias and text; its style, numbering
/// and place are supplied as targetFacts in the first request, using the existing facts tool. The grouping step shows a paragraph with its style in brief, and a run the model could not divide stays as
/// separate headings (without a page or a layout block there is no layout rule that could merge them safely).
/// </summary>
internal sealed class DocxAgentFlavor : IAgentFlavor
{
    public static readonly DocxAgentFlavor Instance = new();
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string SystemPrompt => DocxAgentProtocol.SystemPrompt;
    public string AnswerVersion => AgentProtocol.SelectExtentVersion;
    public bool HeadingExtents => true;
    public bool RequiresCoreFacts => true;
    public JsonArray? PayloadFacts(IAgentSource source, IReadOnlyList<string> targets) => AgentRequestFacts.Read(source, targets);
    public JsonArray ToolDefinitions() => DocxAgentTools.Definitions();
    public IAgentTools CreateTools(IAgentSource source) => new DocxAgentTools((DocxAgentSource)source);
    public object PayloadItem(AgentSourceLine line) => new { alias = line.Alias, text = line.Text };

    /// <summary>The document style table and, per paragraph that has style facts, one object that joins its style, numbering and the rank of its
    /// style in the document (see <see cref="DocxAgentProtocol.PlacementNote"/>).</summary>
    public HeadingPlacementContext PlacementContext(IAgentSource source)
    {
        var docx = (DocxAgentSource)source;
        JsonObject Style(JsonObject facts)
        {
            var o = new JsonObject();
            var style = facts["style"]!.AsObject();
            if (style["name"] is { } name) o["name"] = name.DeepClone();
            if (style["outlineLevel"] is { } outline) o["outlineLevel"] = outline.DeepClone();
            if (style["sizePt"] is { } size) o["sizePt"] = size.DeepClone();
            foreach (var key in new[] { "bold", "italic", "allCaps" }) o[key] = style[key]!.DeepClone();
            if (style["alignment"] is { } alignment) o["alignment"] = alignment.DeepClone();
            if (facts["numbering"] is JsonObject numbering)
            {
                if (numbering["label"] is { } label) o["numberLabel"] = label.DeepClone();
                if (numbering["level"] is { } level) o["numberLevel"] = level.DeepClone();
            }
            if (facts["styleInDocument"] is JsonObject rank)
            {
                o["rankInDocument"] = rank["rank"]!.DeepClone();
                o["linesWithThisStyleInDocument"] = rank["linesWithThisStyleInDocument"]!.DeepClone();
            }
            return o;
        }
        return new(docx.StyleTable.ToJsonString(Compact),
            docx.Facts.ToDictionary(kv => kv.Key, kv => Style(kv.Value).ToJsonString(Compact), StringComparer.Ordinal),
            DocxAgentProtocol.PlacementNote);
    }

    public string GroupPrompt => DocxAgentProtocol.GroupPrompt;

    public object GroupLineItem(IAgentSource source, int index)
    {
        var docx = (DocxAgentSource)source;
        var f = docx.Facts[docx.Lines[index].Alias];
        var style = f["style"]!.AsObject();
        var brief = new JsonObject();
        if (style["name"] is { } name) brief["styleName"] = name.DeepClone();
        if (style["sizePt"] is { } size) brief["sizePt"] = size.DeepClone();
        brief["bold"] = style["bold"]!.DeepClone();
        if (style["alignment"] is { } alignment) brief["alignment"] = alignment.DeepClone();
        if (f["numbering"]?["label"] is { } label) brief["numberLabel"] = label.DeepClone();
        if (f["numbering"]?["level"] is { } level) brief["numberLevel"] = level.DeepClone();
        var shape = Shape(docx.Lines[index].Text);
        return new { alias = docx.Lines[index].Alias, text = docx.Lines[index].Text, style = brief, shape, shapeCount = ShapeCountsOf(docx)[shape] };
    }

    public object GroupContextItem(IAgentSource source, int index) =>
        new { alias = source.Lines[index].Alias, text = AgentGrouping.Clip(source.Lines[index].Text), selected = false };

    public List<int[]> FallbackDivision(IAgentSource source, int[] lines) => lines.Select(i => new[] { i }).ToList();

    static readonly Regex NumberToken = new(@"^(\d+(\.\d+)*|[IVXLCDM]+)$", RegexOptions.CultureInvariant);
    static readonly ConditionalWeakTable<IAgentSource, Dictionary<string, int>> shapeCounts = new();

    /// <summary>The shape of a line: its first two words, with every number or roman numeral written as #. Only the form of the text is read.</summary>
    internal static string Shape(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word =>
        {
            var core = word.TrimEnd('.', ':', ';', ',', ')');
            return core.Length > 0 && NumberToken.IsMatch(core) ? "#" : word;
        }));

    /// <summary>How many lines of the document have each shape, counted once per source.</summary>
    static Dictionary<string, int> ShapeCountsOf(IAgentSource source) => shapeCounts.GetValue(source, s =>
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in s.Lines)
        {
            var shape = Shape(line.Text);
            counts[shape] = counts.GetValueOrDefault(shape) + 1;
        }
        return counts;
    });
}
