using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Selection;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;

namespace DocxHeaderExtractor.Tests;

[Trait("Category", "Slow")]
public sealed class PdfGroupingExtentTests
{
    const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(Pdf), out _)!);

    [Fact]
    public void Pdf_repairs_invalid_extent_then_groups_and_binds_the_exact_selected_substring()
    {
        var source = Source.Value;
        var firstIndex = Enumerable.Range(0, source.Cores[0].End - 1).First(i => source.Lines[i].Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3);
        var first = source.Lines[firstIndex];
        var second = source.Lines[firstIndex + 1];
        var start = first.Text.IndexOf(' ') + 1;
        while (first.Text[start] == ' ') start++;
        var end = first.Text.IndexOf(' ', start);
        if (end < 0) end = first.Text.Length;
        var text = first.Text[start..end];
        var selectionPosts = 0;
        var sawGroup = false;
        AgentUnit.Model model = (unit, turn, request) =>
        {
            var messages = JsonNode.Parse(request.Body)!["messages"]!;
            var payload = JsonNode.Parse(messages[1]!["content"]!.Text())!;
            if (payload["stage"]!.GetValue<string>() == "GROUP")
            {
                sawGroup = true;
                var run = payload["runs"]![0]!;
                var line = run["lines"]![0]!;
                Assert.Equal(text, line["selectedText"]!.GetValue<string>());
                Assert.Equal(text, line["text"]!.GetValue<string>());
                Assert.Equal(first.Text, line["sourceText"]!.GetValue<string>());
                Assert.Equal(start, line["headingExtent"]!["start"]!.GetValue<int>());
                Assert.NotNull(line["layout"]);
                Assert.Equal(second.Text, run["lines"]![1]!["selectedText"]!.GetValue<string>());
                return new(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                    runs = new[] { new { run = run["run"]!.GetValue<string>(), headings = new[] { new[] { first.Alias }, new[] { second.Alias } } } } }), [], "stop");
            }
            selectionPosts++;
            Assert.Contains(AgentProtocol.SelectExtentVersion, messages[0]!["content"]!.Text());
            if (selectionPosts > 1) Assert.Contains("HEADING_EXTENT_", messages[messages.AsArray().Count - 1]!["content"]!.GetValue<string>());
            return new(JsonSerializer.Serialize(new { protocolVersion = source.Flavor.AnswerVersion, status = "COMPLETE",
                decisions = payload["targets"]!.AsArray().Select(t =>
                {
                    var alias = t!["alias"]!.GetValue<string>();
                    return new { alias, decision = alias == first.Alias || alias == second.Alias ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { alias },
                        headingExtent = alias == first.Alias ? new { start, end = selectionPosts == 1 ? 999999 : end, text } : null };
                }) }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), [], "stop");
        };
        var result = AgentSelector.Select(source, model, new(1000, 100), 1, CancellationToken.None, 0, () => 0, firstSegments: 1);
        Assert.Empty(result.UnitFailures);
        Assert.True(sawGroup);
        Assert.Equal(2, selectionPosts);
        var built = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(Pdf));
        var decisions = SelectionAuthority.FromGroups(built.Snapshot.Atoms, result.SelectedAliases, result.Groups!, built.Details.LayoutBlockByAtom, result.HeadingExtents);
        var part = Assert.Single(decisions[0].Parts!);
        Assert.Equal(text, part.Text);
        Assert.Equal(start, part.Start);
        Assert.Equal(end, part.End);
        Assert.Equal(first.Text, source.Lines[firstIndex].Text);
    }

    [Fact]
    public void Pdf_extent_without_a_valid_reply_never_becomes_a_whole_line_heading()
    {
        var source = Source.Value;
        AgentUnit.Model model = (unit, turn, request) =>
        {
            var payload = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!.Text())!;
            return new(JsonSerializer.Serialize(new { protocolVersion = source.Flavor.AnswerVersion, status = "COMPLETE",
                decisions = payload["targets"]!.AsArray().Select(t => new { alias = t!["alias"]!.GetValue<string>(), decision = "SELECT",
                    evidenceRefs = new[] { t["alias"]!.GetValue<string>() }, headingExtent = new { start = 0, end = 999999, text = "invalid" } }) }), [], "stop");
        };
        var result = AgentSelector.Select(source, model, new(1000, 100), 1, CancellationToken.None, 0, () => 0, firstSegments: 1);
        Assert.Empty(result.SelectedAliases);
        Assert.Empty(result.Groups!);
        Assert.NotEmpty(result.UnitFailures);
        Assert.Throws<InvalidOperationException>(() => source.Flavor.GroupLineItem(source, 0, new TextOffsetSpan(0, 999999)));
    }
}
