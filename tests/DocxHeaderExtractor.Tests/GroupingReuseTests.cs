using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A grouping batch answered before is read back instead of asked again, when its first request is byte for byte the same; the group
/// requests carry cache marks like the selection's. No network.
/// </summary>
[Trait("Category", "Slow")]
public sealed class GroupingReuseTests : IDisposable
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(Pdf), out _)!);

    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-groupcache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private static string A(int i) => Source.Value.Lines[i].Alias;

    /// <summary>Answers every run as one heading of all its lines; counts the calls.</summary>
    private static AgentUnit.Model Answering(Action onCall, string? reply = null) => (unit, turn, request) =>
    {
        onCall();
        if (reply is not null) return new AgentProtocol.ModelTurn(reply, [], "stop");
        var payload = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!.Text())!;
        var runs = payload["runs"]!.AsArray().Select(r => (JsonNode)new JsonObject
        {
            ["run"] = r!["run"]!.GetValue<string>(),
            ["headings"] = new JsonArray(new JsonArray(r["lines"]!.AsArray().Select(l => (JsonNode)JsonValue.Create(l!["alias"]!.GetValue<string>())!).ToArray())),
        }).ToArray();
        return new AgentProtocol.ModelTurn(new JsonObject { ["protocolVersion"] = AgentGrouping.GroupVersion, ["runs"] = new JsonArray(runs) }.ToJsonString(), [], "stop");
    };

    private static HashSet<string> Selected(params int[] lines) => lines.Select(A).ToHashSet(StringComparer.Ordinal);

    private static string[] Shape(AgentGrouping.Result result) => result.Groups.Select(g => string.Join("|", g)).ToArray();

    [Fact]
    public void A_batch_answered_before_is_read_back_and_not_asked_again()
    {
        var cache = new GroupingCache(dir);
        var selected = Selected(10, 11, 20, 21);
        var first = AgentGrouping.Group(Source.Value, selected, Answering(() => { }), new AgentBudget(1000, 100), 1, CancellationToken.None,
            retryDelayMs: 0, cache: cache);
        Assert.Empty(first.Failures);

        var calls = 0;
        var second = AgentGrouping.Group(Source.Value, selected, Answering(() => calls++), new AgentBudget(1000, 100), 1, CancellationToken.None,
            retryDelayMs: 0, cache: cache);

        Assert.Equal(0, calls);
        Assert.Equal(Shape(first), Shape(second));
        Assert.Empty(second.Failures);
        Assert.Equal(first.Observation.ModelDividedRuns, second.Observation.ModelDividedRuns);
    }

    [Fact]
    public void A_different_selection_is_a_different_request_and_is_asked_again()
    {
        var cache = new GroupingCache(dir);
        AgentGrouping.Group(Source.Value, Selected(10, 11), Answering(() => { }), new AgentBudget(1000, 100), 1, CancellationToken.None, retryDelayMs: 0, cache: cache);

        var calls = 0;
        AgentGrouping.Group(Source.Value, Selected(10, 11, 12), Answering(() => calls++), new AgentBudget(1000, 100), 1, CancellationToken.None,
            retryDelayMs: 0, cache: cache);

        Assert.True(calls > 0);
    }

    [Fact]
    public void A_batch_with_a_refused_answer_is_not_stored_and_is_asked_again()
    {
        var cache = new GroupingCache(dir);
        var selected = Selected(10, 11);
        AgentGrouping.Group(Source.Value, selected, Answering(() => { }, reply: "not json"), new AgentBudget(1000, 100), 1, CancellationToken.None,
            retryDelayMs: 0, cache: cache);

        var calls = 0;
        AgentGrouping.Group(Source.Value, selected, Answering(() => calls++), new AgentBudget(1000, 100), 1, CancellationToken.None,
            retryDelayMs: 0, cache: cache);

        Assert.True(calls > 0);
    }

    [Fact]
    public void Without_a_cache_every_run_asks_the_model()
    {
        var selected = Selected(10, 11);
        var calls = 0;
        AgentGrouping.Group(Source.Value, selected, Answering(() => calls++), new AgentBudget(1000, 100), 1, CancellationToken.None, retryDelayMs: 0);
        AgentGrouping.Group(Source.Value, selected, Answering(() => calls++), new AgentBudget(1000, 100), 1, CancellationToken.None, retryDelayMs: 0);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Group_requests_mark_the_group_prompt_and_the_payload_for_caching_like_the_selection_does()
    {
        var payload = JsonSerializer.Serialize(new { stage = "GROUP", runs = Array.Empty<object>() });
        var body = JsonNode.Parse(AgentGrouping.BuildRequest(payload, new JsonArray()).Body)!;

        foreach (var message in body["messages"]!.AsArray())
        {
            var block = message!["content"]!.AsArray().Single()!;
            Assert.Equal("text", block["type"]!.GetValue<string>());
            Assert.Equal("ephemeral", block["cache_control"]!["type"]!.GetValue<string>());
        }
        Assert.Equal(GroupingPrompts.Shared, body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(payload, body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>());
    }
}
