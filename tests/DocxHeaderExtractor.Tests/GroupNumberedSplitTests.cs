using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

public sealed class GroupNumberedSplitTests
{
    [Theory]
    [InlineData("decimal", "1.", true)]
    [InlineData("upperRoman", "I.", true)]
    [InlineData("bullet", "•", false)]
    [InlineData("none", "1.", false)]
    [InlineData(null, "•", false)]
    [InlineData("", "1.", false)]
    public void Missing_or_non_numeric_list_format_never_qualifies(string? format, string label, bool expected)
    {
        var numbering = new JsonObject { ["label"] = label, ["format"] = format };
        Assert.Equal(expected, DocxAgentSource.IsNumberedList(numbering));
    }

    [Fact]
    public void Provenance_marks_only_the_partition_that_was_changed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dhx-split-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = DocxAgentSource.Load(DocxTestDocument.Create(Path.Combine(dir, "test.docx")), null, out _)!;
            var ids = source.Lines.Take(3).Select(x => x.Alias).ToArray();
            AgentUnit.Model model = (_, _, request) =>
            {
                var body = JsonNode.Parse(request.Body)!;
                var payload = JsonNode.Parse(body["messages"]![1]!["content"]!.Text())!;
                var run = payload["runs"]![0]!["run"]!.GetValue<string>();
                return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                    runs = new[] { new { run, headings = new[] { new[] { ids[0], ids[1] }, new[] { ids[2] } } } } }), [], "stop");
            };
            var result = AgentGrouping.Group(source, ids.ToHashSet(), model, new AgentBudget(100, 100), 1, CancellationToken.None,
                retryDelayMs: 0, ownListNumber: a => a == ids[0] || a == ids[1]);
            Assert.Empty(result.Failures);
            Assert.Equal(3, result.Groups.Count);
            Assert.All(result.Provenance.Where(p => p.Alias == ids[0] || p.Alias == ids[1]), p => Assert.Contains(AgentGrouping.SplitNumberedCode, p.FailureCodes));
            Assert.DoesNotContain(AgentGrouping.SplitNumberedCode, Assert.Single(result.Provenance, p => p.Alias == ids[2]).FailureCodes);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void A_group_of_two_separately_numbered_lines_is_split_into_single_lines()
    {
        // "I. CÁC HOẠT ĐỘNG CHÍNH" followed by "A. Truyền thông:" proposed as one heading: both lines show their own list number.
        var numbered = new HashSet<int> { 5, 6 };
        var parts = AgentGrouping.SplitSeparatelyNumbered([[5, 6]], numbered.Contains);
        Assert.Equal([[5], [6]], parts);
    }

    [Fact]
    public void A_heading_continued_on_unnumbered_lines_is_kept()
    {
        // A numbered first line with an unnumbered continuation (for example "1." then the rest of its title) stays one heading.
        var numbered = new HashSet<int> { 1, 4 };
        var parts = AgentGrouping.SplitSeparatelyNumbered([[1, 2, 3], [4]], numbered.Contains);
        Assert.Equal([[1, 2, 3], [4]], parts);
    }

    [Fact]
    public void An_unnumbered_continuation_stays_with_the_numbered_line_before_it()
    {
        // [A numbered, B continuation, C numbered] is two items: [A, B] and [C]; an unnumbered line before the first number stays on its own.
        var numbered = new HashSet<int> { 1, 3, 6 };
        Assert.Equal([[1, 2], [3]], AgentGrouping.SplitSeparatelyNumbered([[1, 2, 3]], numbered.Contains));
        Assert.Equal([[4], [5], [6, 7]], AgentGrouping.SplitSeparatelyNumbered([[4, 5, 6, 7]], i => i is 5 or 6));
    }

    [Fact]
    public void A_group_with_one_numbered_line_is_never_cut()
    {
        Assert.Equal([[8, 9]], AgentGrouping.SplitSeparatelyNumbered([[8, 9]], i => i == 9));
    }

    [Fact]
    public void Only_the_offending_group_of_a_run_is_split_and_order_is_kept()
    {
        var numbered = new HashSet<int> { 10, 11, 13 };
        var parts = AgentGrouping.SplitSeparatelyNumbered([[10, 11], [12], [13, 14]], numbered.Contains);
        Assert.Equal([[10], [11], [12], [13, 14]], parts);
    }

    [Fact]
    public void The_split_is_opt_in_for_docx_and_keys_the_selector_and_stored_outlines()
    {
        Assert.False(new PipelineOptions().DocxSplitNumberedGroups);
        Assert.DoesNotContain("group-split-numbered", new DocxSelector().SelectorId);
        Assert.Contains(":group-split-numbered-v1", new DocxSelector(splitNumberedGroups: true).SelectorId);
        // Within one build the key is unchanged when the flag is off. The key also holds assembly MVIDs, so any new build changes it either way.
        Assert.Equal(OutlineReuse.ConfigKey(new PipelineOptions()), OutlineReuse.ConfigKey(new PipelineOptions { DocxSplitNumberedGroups = false }));
        Assert.NotEqual(OutlineReuse.ConfigKey(new PipelineOptions()), OutlineReuse.ConfigKey(new PipelineOptions { DocxSplitNumberedGroups = true }));
        var routes = HeadingAgentSetup.CreateRoutes(new PipelineOptions { DocxSplitNumberedGroups = true }, new Provider());
        Assert.Contains(":group-split-numbered-v1", ((DocxSelector)routes.Shared.Docx!).SelectorId);
    }

    sealed class Provider : IModelProvider
    {
        public T? Create<T>(string role) where T : class => null;
    }
}
