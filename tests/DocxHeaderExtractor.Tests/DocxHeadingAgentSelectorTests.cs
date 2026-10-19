using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The production DOCX selector end to end over a fake OpenRouter: the same conversation as the PDF selector, with the DOCX prompt, tools and
/// facts. No network.
/// </summary>
public sealed class DocxHeadingAgentSelectorTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-docx-selector-" + Guid.NewGuid().ToString("N"));

    public DocxHeadingAgentSelectorTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private string Long() => DocxTestDocument.CreateLong(Path.Combine(dir, "long.docx"), 100);

    [Fact]
    public async Task Every_window_reads_its_facts_then_answers_and_the_request_is_the_docx_one()
    {
        var path = Long();
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget);
        var selector = new DocxHeadingAgentSelector(() => handler, parallelism: 4);

        var selection = await selector.SelectAsync(path, "sk-test", CancellationToken.None);

        var source = DocxAgentSource.Load(path, null, out _)!;
        Assert.Equal(3, source.Cores.Count);
        Assert.Equal(source.Cores.Count, selection.UnitsTotal);
        Assert.Equal(selection.UnitsTotal, selection.UnitsComplete);
        Assert.Empty(selection.UnitFailures);
        Assert.Equal(Enumerable.Range(0, source.Cores.Count).Select(n => source.Scope(n).Decide[0]).ToHashSet(), selection.SelectedAliases);
        // Facts and selection answer are the only per-segment calls. Semantic group verification
        // is deliberately absent from the production binding path.
        Assert.Empty(handler.Bodies.Where(FakeOpenRouter.IsVerification));
        Assert.Equal(2 * selection.UnitsTotal, selection.ProviderCalls);
        Assert.Equal(2 * selection.UnitsTotal, handler.Posts);
        Assert.All(handler.Keys, k => Assert.Equal("Bearer sk-test", k));

        // Every request carries the DOCX prompt and the six DOCX tools; the facts a unit read are the DOCX facts.
        Assert.All(handler.Bodies, b =>
        {
            Assert.Equal(DocxAgentProtocol.SystemPrompt, b["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
            Assert.Equal(DocxAgentTools.Definitions().ToJsonString(), b["tools"]!.ToJsonString());
        });
        var toolMessages = handler.Bodies.SelectMany(b => b["messages"]!.AsArray()).Where(m => m!["role"]!.GetValue<string>() == "tool").ToArray();
        Assert.NotEmpty(toolMessages);
        Assert.All(toolMessages, m => Assert.Contains(DocxAgentSource.FactsVersion, m!["content"]!.ToJsonString(), StringComparison.Ordinal));
        Assert.StartsWith("docx-v6:", selector.SelectorId, StringComparison.Ordinal);
        Assert.Contains(HeadingAgentSelector.BindingPolicyVersion, selector.SelectorId, StringComparison.Ordinal);
        Assert.NotNull(selection.Placement);
        Assert.Equal(DocxAgentProtocol.PlacementNote, selection.Placement!.MeasurementsNote);
    }

    [Fact]
    public async Task Adjacent_selected_paragraphs_are_divided_into_headings_by_a_group_request()
    {
        var path = Long();
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTwoTargetsThenGroup);
        var selector = new DocxHeadingAgentSelector(() => handler, parallelism: 4);

        var selection = await selector.SelectAsync(path, "sk-test", CancellationToken.None);

        var source = DocxAgentSource.Load(path, null, out _)!;
        Assert.Empty(selection.UnitFailures);
        Assert.Equal(source.Cores.Count, selection.Groups!.Count(g => g.Count == 2));
        Assert.Equal(selection.SelectedAliases.Order(), selection.Groups!.SelectMany(g => g).Order());
        Assert.Empty(handler.Bodies.Where(FakeOpenRouter.IsVerification));
        Assert.Equal(2 * selection.UnitsTotal + 1, selection.ProviderCalls);
        // The group request carries the DOCX grouping prompt, and its lines carry a style and no page.
        var group = handler.Bodies.Single(b => b["messages"]![1]!["content"] is JsonValue && !FakeOpenRouter.IsVerification(b));
        Assert.Equal(DocxAgentFlavor.Instance.GroupPrompt, group["messages"]![0]!["content"]!.ToString());
        Assert.Contains("\"style\"", group["messages"]![1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"page\"", group["messages"]![1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_the_reader_refuses_fails_the_selection_before_any_call()
    {
        var path = Path.Combine(dir, "not-a-docx.docx");
        File.WriteAllText(path, "plain text");
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget);
        var selector = new DocxHeadingAgentSelector(() => handler);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => selector.SelectAsync(path, "sk-test", CancellationToken.None));

        Assert.Contains("DOCX_AGENT_SOURCE_REFUSED", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task Every_model_reply_is_handed_to_the_diagnostics_hook_only_when_one_is_given()
    {
        var path = Long();
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget);
        var replies = new List<string>();
        var selector = new DocxHeadingAgentSelector(() => handler, parallelism: 1,
            onModelReply: r => replies.Add(r.Content ?? ""));

        await selector.SelectAsync(path, "sk-test", CancellationToken.None);

        Assert.NotEmpty(replies);
        Assert.Contains(replies, c => c.Contains(AgentProtocol.SelectExtentVersion, StringComparison.Ordinal));
    }

    [Fact]
    public void The_reasoning_the_model_streamed_survives_the_stream_and_the_reply()
    {
        var raw = "data: {\"id\":\"g\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"Thinking \"}}]}\n\n"
            + "data: {\"id\":\"g\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"more.\"}}]}\n\n"
            + "data: {\"id\":\"g\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n";

        var asm = Sse.Parse(raw);

        Assert.Equal("Thinking more.", asm.Reasoning);
        Assert.Equal("{}", asm.Content);
        var turn = AgentProtocol.ReadResponse(asm.View()).Turn;
        Assert.Equal("Thinking more.", turn!.Reasoning);
        Assert.Equal("{}", turn.Content);
    }

    [Fact]
    public async Task A_reply_carries_the_tool_results_the_request_before_it_added()
    {
        var path = Long();
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget);
        var replies = new List<ModelReply>();
        var selector = new DocxHeadingAgentSelector(() => handler, parallelism: 1, onModelReply: r => replies.Add(r));

        await selector.SelectAsync(path, "sk-test", CancellationToken.None);

        Assert.Contains(replies, r => r.ToolResults.Count >= 1
            && r.ToolResults[0].Content.Contains(DocxAgentSource.FactsVersion, StringComparison.Ordinal));
        Assert.All(replies.Where(r => r.Turn == 1), r => Assert.Empty(r.ToolResults));
    }
}
