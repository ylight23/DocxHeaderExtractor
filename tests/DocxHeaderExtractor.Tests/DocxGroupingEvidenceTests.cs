using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;

namespace DocxHeaderExtractor.Tests;

public sealed class DocxGroupingEvidenceTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-group-extents-" + Guid.NewGuid().ToString("N"));
    public DocxGroupingEvidenceTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, recursive: true);
    DocxAgentSource Source() => DocxAgentSource.Load(DocxTestDocument.Create(Path.Combine(dir, Guid.NewGuid() + ".docx")), null, out _)!;

    [Fact]
    public void Production_passes_selected_extents_to_grouping_and_keeps_source_text_as_context()
    {
        var source = Source();
        var first = source.Lines[0];
        var second = source.Lines[1];
        var grouped = false;
        AgentUnit.Model model = (unit, turn, request) =>
        {
            var body = JsonNode.Parse(request.Body)!;
            var payload = JsonNode.Parse(body["messages"]![1]!["content"]!.Text())!;
            if (payload["stage"]!.GetValue<string>() == "GROUP")
            {
                grouped = true;
                var run = Assert.Single(payload["runs"]!.AsArray())!;
                var line = run["lines"]![0]!;
                Assert.Equal("Chapter", line["text"]!.GetValue<string>());
                Assert.Equal("Chapter", line["selectedText"]!.GetValue<string>());
                Assert.Equal(first.Text, line["sourceText"]!.GetValue<string>());
                Assert.Equal(7, line["headingExtent"]!["end"]!.GetValue<int>());
                Assert.Equal(second.Text, run["lines"]![1]!["selectedText"]!.GetValue<string>());
                return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                    runs = new[] { new { run = run["run"]!.GetValue<string>(), headings = new[] { new[] { first.Alias, second.Alias } } } } }), [], "stop");
            }
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = source.Flavor.AnswerVersion, status = "COMPLETE",
                decisions = payload["targets"]!.AsArray().Select(t =>
                {
                    var alias = t!["alias"]!.GetValue<string>();
                    return new { alias, decision = alias == first.Alias || alias == second.Alias ? "SELECT" : "NOT_SELECT",
                        evidenceRefs = new[] { alias }, reason = "Heading in the source context.",
                        headingExtent = alias == first.Alias ? new { start = 0, end = 7, text = "Chapter" } : null };
                }) }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), [], "stop");
        };
        var result = AgentSelector.Select(source, model, new AgentBudget(100, 100), 1, CancellationToken.None, 0, () => 0);
        Assert.Empty(result.UnitFailures);
        Assert.True(grouped);
        Assert.Equal([first.Alias, second.Alias], Assert.Single(result.Groups!));
        Assert.Equal(new TextOffsetSpan(0, 7), result.HeadingExtents![first.Alias]);
    }

    [Fact]
    public void Cache_reuse_requires_the_same_selected_extents_and_source_is_not_mutated()
    {
        var source = Source();
        var first = source.Lines[0];
        var second = source.Lines[1];
        var calls = 0;
        AgentUnit.Model model = (unit, turn, request) =>
        {
            calls++;
            var payload = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!.Text())!;
            var run = payload["runs"]![0]!;
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                runs = new[] { new { run = run["run"]!.GetValue<string>(), headings = new[] { new[] { first.Alias }, new[] { second.Alias } } } } }), [], "stop");
        };
        var cache = new GroupingCache(Path.Combine(dir, "cache"));
        var selected = new HashSet<string>([first.Alias, second.Alias]);
        void Group(TextOffsetSpan span) => AgentGrouping.Group(source, selected, model, new AgentBudget(100, 100), 1, CancellationToken.None,
            retryDelayMs: 0, cache: cache, headingExtents: new Dictionary<string, TextOffsetSpan> { [first.Alias] = span });
        Group(new(0, 7));
        Group(new(0, 7));
        Assert.Equal(1, calls);
        Group(new(8, 9));
        Assert.Equal(2, calls);
        Assert.Equal("Chapter I", source.Lines[0].Text);
        var item = JsonSerializer.SerializeToNode(source.Flavor.GroupLineItem(source, 0, new TextOffsetSpan(8, 9)))!;
        Assert.Equal("I", item["selectedText"]!.GetValue<string>());
        Assert.Equal(8, item["headingExtent"]!["start"]!.GetValue<int>());
    }

    [Fact]
    public void Invalid_extent_is_not_replaced_with_the_whole_paragraph()
    {
        var source = Source();
        Assert.Throws<InvalidOperationException>(() => source.Flavor.GroupLineItem(source, 0, new TextOffsetSpan(0, 999)));
    }

    [Fact]
    public void A_mixed_paragraph_exposes_only_the_selected_heading_as_the_boundary_target()
    {
        var path = DocxTestDocument.Create(Path.Combine(dir, "mixed.docx"));
        const string heading = "Article 7. Scope";
        const string paragraph = heading + " Body content follows in the same paragraph.";
        using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, true))
        {
            var first = document.MainDocumentPart!.Document!.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().First();
            first.RemoveAllChildren();
            first.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(paragraph)));
        }
        var source = DocxAgentSource.Load(path, null, out _)!;
        var line = JsonSerializer.SerializeToNode(source.Flavor.GroupLineItem(source, 0, new TextOffsetSpan(0, heading.Length)))!;
        Assert.Equal(heading, line["text"]!.GetValue<string>());
        Assert.Equal(heading, line["selectedText"]!.GetValue<string>());
        Assert.Equal(paragraph, line["sourceText"]!.GetValue<string>());
        var whole = JsonSerializer.SerializeToNode(source.Flavor.GroupLineItem(source, 0))!;
        Assert.Equal(paragraph, whole["text"]!.GetValue<string>());
    }
}
