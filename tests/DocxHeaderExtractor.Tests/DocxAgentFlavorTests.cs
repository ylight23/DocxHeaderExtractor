using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The DOCX flavor: a prompt that tells the model what a DOCX gives it (and nothing of a PDF's pages or boxes), the shape of a payload line, the
/// grouping and placement hooks, and that the PDF flavor is untouched. No network.
/// </summary>
public sealed class DocxAgentFlavorTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-docx-flavor-" + Guid.NewGuid().ToString("N"));

    public DocxAgentFlavorTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private DocxAgentSource Source() => DocxAgentSource.Load(DocxTestDocument.Create(Path.Combine(dir, "sample.docx")), null, out _)!;

    [Fact]
    public void The_prompt_tells_the_method_in_paragraphs_and_names_no_page_box_or_layout_block()
    {
        var prompt = DocxAgentProtocol.SystemPrompt;

        // The method is the shared one.
        Assert.Contains(AgentProtocol.Intro, prompt);
        Assert.Contains(AgentProtocol.MembershipOnly, prompt);
        Assert.Contains(AgentProtocol.ObservedLine, prompt);
        Assert.EndsWith(DocxAgentProtocol.ExtentBody, prompt, StringComparison.Ordinal);
        Assert.StartsWith(AgentProtocol.BodyWithAnalysis(AgentProtocol.SelectExtentVersion)[..200], DocxAgentProtocol.ExtentBody, StringComparison.Ordinal);
        Assert.Contains("You have at most " + AgentProtocol.MaxToolTurns + " rounds of tool calls", prompt);
        // What is DOCX's own.
        Assert.Contains("A line is one paragraph of the document", prompt);
        Assert.Contains("the request carries targetFacts", prompt);
        Assert.DoesNotContain("Your first reply must be tool calls", prompt);
        Assert.Contains("the outline level the style or the paragraph declares", prompt);
        Assert.Contains("how its style ranks among the styles of the document", prompt);
        Assert.Contains("documentStyles lists the distinct styles (style name, font size and weight)", prompt);
        // Nothing of a PDF.
        foreach (var pdfWord in new[] { "bbox", "layout block", "left edge", "visual line", "text segments", "PDF", "page" })
            Assert.DoesNotContain(pdfWord, prompt, StringComparison.Ordinal);
        Assert.NotEqual(AgentProtocol.SystemPrompt, prompt);
    }

    [Fact]
    public void A_payload_line_is_its_alias_and_text_only_and_the_request_carries_the_docx_tools()
    {
        var source = Source();
        var flavor = source.Flavor;
        Assert.Same(DocxAgentFlavor.Instance, flavor);
        Assert.Equal("{\"alias\":\"" + source.Lines[2].Alias + "\",\"text\":\"Article 1. Scope\"}", JsonSerializer.Serialize(flavor.PayloadItem(source.Lines[2])));

        var (decide, shown) = source.Scope(0);
        var styles = flavor.CreateTools(source).DocumentStyles();
        var payload = AgentProtocol.Payload(source.SourceSha256, AgentSource.SegmentId(0), decide, shown.Select(i => source.Lines[i]).ToArray(), styles, AgentProtocol.Instruction, flavor);
        var p = JsonNode.Parse(payload)!;
        Assert.Equal(decide.Length, p["targetCount"]!.GetValue<int>());
        Assert.All(p["targets"]!.AsArray(), t => Assert.Equal(new[] { "alias", "text" }, t!.AsObject().Select(kv => kv.Key)));
        Assert.NotNull(p["documentStyles"]);

        var request = AgentProtocol.Build(payload, new JsonArray(), "auto", flavor);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal(DocxAgentProtocol.SystemPrompt, body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(7, body["tools"]!.AsArray().Count);
        Assert.Equal(DocxAgentTools.Definitions().ToJsonString(), body["tools"]!.ToJsonString());
        // The PDF request is what it was: its own prompt and its eight tools.
        var pdf = JsonNode.Parse(AgentProtocol.Build(payload, new JsonArray(), "auto").Body)!;
        Assert.Equal(AgentProtocol.SystemPrompt, pdf["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(8, pdf["tools"]!.AsArray().Count);
    }

    [Fact]
    public void The_grouping_step_shows_a_paragraph_with_its_style_and_leaves_an_undivided_run_as_separate_headings()
    {
        var source = Source();
        var flavor = source.Flavor;

        Assert.Contains("Each line has alias, text and its style (style name, font size, bold, numbering label and level, alignment, when it has them);", flavor.GroupPrompt);
        Assert.DoesNotContain("layout", flavor.GroupPrompt.Split('\n')[1], StringComparison.Ordinal);

        var line = JsonNode.Parse(JsonSerializer.Serialize(flavor.GroupLineItem(source, 0)))!;
        Assert.Equal("heading 1", line["style"]!["styleName"]!.GetValue<string>());
        Assert.Equal(16.0, line["style"]!["sizePt"]!.GetValue<double>());
        Assert.Null(line["page"]);
        var near = JsonNode.Parse(JsonSerializer.Serialize(flavor.GroupContextItem(source, 3)))!;
        Assert.False(near["selected"]!.GetValue<bool>());

        // Without a page or a layout block, nothing safely merges two selected paragraphs the model did not divide.
        var fallback = flavor.FallbackDivision(source, [0, 1, 2]);
        Assert.Equal(new[] { new[] { 0 }, new[] { 1 }, new[] { 2 } }, fallback);
    }

    [Fact]
    public void The_placement_context_describes_its_own_measurements_and_joins_style_numbering_and_rank_per_paragraph()
    {
        var source = Source();
        var context = source.Flavor.PlacementContext(source);

        Assert.Equal(DocxAgentProtocol.PlacementNote, context.MeasurementsNote);
        Assert.Contains("numbering label and level", context.MeasurementsNote);
        Assert.Equal(source.Lines.Count, context.StyleJsonByAlias.Count);
        var chapter = JsonNode.Parse(context.StyleJsonByAlias[source.Lines[0].Alias])!;
        Assert.Equal("heading 1", chapter["name"]!.GetValue<string>());
        Assert.Equal(0, chapter["outlineLevel"]!.GetValue<int>());
        Assert.Equal(1, chapter["rankInDocument"]!.GetValue<int>());
        Assert.Equal(2, chapter["linesWithThisStyleInDocument"]!.GetValue<int>());
        Assert.Equal(source.Lines.Count, JsonNode.Parse(context.DocumentStylesJson)!["documentLines"]!.GetValue<int>());

        // A PDF's context keeps no note: the default sentence stays, and the placement prompt is byte for byte as before.
        Assert.Null(new DocxHeaderExtractor.Contracts.Routing.HeadingPlacementContext("{}", new Dictionary<string, string>()).MeasurementsNote);
    }
}
