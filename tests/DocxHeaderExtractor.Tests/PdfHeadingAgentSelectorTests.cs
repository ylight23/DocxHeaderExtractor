using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.PdfHeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The production selector end to end over a fake OpenRouter: streamed replies, the audit, the budget and the parallel units. No network.
/// </summary>
public sealed class PdfHeadingAgentSelectorTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    [Fact]
    public async Task Every_segment_reads_its_facts_then_answers_and_the_selection_collects_the_selected_lines()
    {
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget);
        var selector = new PdfHeadingAgentSelector(() => handler, parallelism: 4);

        var selection = await selector.SelectAsync(TestRepository.Path(Pdf), "sk-test", CancellationToken.None);

        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;
        Assert.Equal(source.Cores.Count, selection.UnitsTotal);
        Assert.Equal(selection.UnitsTotal, selection.UnitsComplete);
        Assert.Empty(selection.UnitFailures);
        Assert.Equal(Enumerable.Range(0, source.Cores.Count).Select(n => source.Scope(n).Decide[0]).ToHashSet(), selection.SelectedAliases);
        Assert.Equal(2 * selection.UnitsTotal, selection.ProviderCalls);
        Assert.Equal(2 * selection.UnitsTotal, handler.Posts);
        Assert.Equal(0.001 * handler.Posts, selection.ProviderCostUsd, 6);
        Assert.All(handler.Keys, k => Assert.Equal("Bearer sk-test", k));
        Assert.Equal(selection.UnitsTotal, selection.ModelAnswers.Count);
    }

    [Fact]
    public async Task Adjacent_selected_lines_are_divided_into_headings_by_a_group_request_and_the_division_reaches_the_selection()
    {
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTwoTargetsThenGroup);
        var selector = new PdfHeadingAgentSelector(() => handler, parallelism: 4);

        var selection = await selector.SelectAsync(TestRepository.Path(Pdf), "sk-test", CancellationToken.None);

        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;
        Assert.Equal(selection.UnitsTotal, selection.UnitsComplete);
        Assert.Empty(selection.UnitFailures);
        // Every segment selected its first two targets; the fake group answer puts each pair together.
        Assert.Equal(source.Cores.Count, selection.Groups!.Count(g => g.Count == 2));
        Assert.Equal(selection.SelectedAliases.Order(), selection.Groups!.SelectMany(g => g).Order());
        Assert.Equal(2 * selection.UnitsTotal + (int)Math.Ceiling(source.Cores.Count / 40.0), selection.ProviderCalls);
        Assert.Contains(selection.ModelAnswers, a => a.StartsWith("{\"grouping\"", StringComparison.Ordinal));
        Assert.NotNull(selection.Placement);
    }

    [Fact]
    public async Task A_provider_that_keeps_failing_is_retried_three_times_per_unit_and_the_units_fail()
    {
        var handler = new FakeOpenRouter((_, _) => (HttpStatusCode.TooManyRequests, ""));
        var selector = new PdfHeadingAgentSelector(() => handler, parallelism: 1);
        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;

        // retryDelayMs is fixed at 5 s in production; one unit shows the rule.
        var budget = PdfHeadingAgentSelector.BudgetFor(source);
        using var model = new AgentModel(handler, "sk-test", budget);
        var outcome = AgentUnit.Run(source, 0, model.Turn, budget, retryDelayMs: 0);

        Assert.Equal("FAILED", outcome.Status);
        Assert.Equal("CALL:TRANSPORT:RATE_LIMITED_NO_RETRY", outcome.Failure);
        Assert.Equal(3, outcome.TransportRetries);
        Assert.Equal(4, handler.Posts);
        Assert.Empty(outcome.Decisions.Where(d => d.Value == "SELECT"));
        await Task.CompletedTask;
    }

    [Fact]
    public void A_reply_from_another_endpoint_halts_the_document()
    {
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget, provider: "SomeoneElse");
        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;
        var budget = PdfHeadingAgentSelector.BudgetFor(source);
        using var model = new AgentModel(handler, "sk-test", budget);
        var outcome = AgentUnit.Run(source, 0, model.Turn, budget, retryDelayMs: 0);
        Assert.Equal("CALL:ENDPOINT_OR_MODEL_UNEXPECTED", outcome.Failure);
        Assert.Equal("ENDPOINT_OR_MODEL_UNEXPECTED", budget.HaltReason);
    }

    /// <summary>A fake OpenRouter: answers each POST with a streamed reply the script chooses.</summary>
    private sealed class FakeOpenRouter(Func<JsonObject, int, (HttpStatusCode Status, string Sse)> script, string provider = "Alibaba") : HttpMessageHandler
    {
        private int posts;
        public int Posts => posts;
        public List<string> Keys { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            lock (Keys) Keys.Add(request.Headers.Authorization!.ToString());
            var n = Interlocked.Increment(ref posts);
            var (status, sse) = script(body, n);
            return new HttpResponseMessage(status) { Content = new StringContent(sse.Replace("PROVIDER", provider), Encoding.UTF8, "text/event-stream") };
        }

        private static string Event(object o) => "data: " + JsonSerializer.Serialize(o) + "\n\n";

        private static string Stream(object delta, string finish) =>
            ": OPENROUTER PROCESSING\n\n"
            + Event(new { id = "gen-1", provider = "PROVIDER", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta } } })
            + Event(new { id = "gen-1", provider = "PROVIDER", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta = new { }, finish_reason = finish } },
                usage = new { prompt_tokens = 1000, completion_tokens = 200, completion_tokens_details = new { reasoning_tokens = 150 }, cost = 0.001 } })
            + "data: [DONE]\n\n";

        /// <summary>As above, but each segment selects its first TWO targets; a GROUP request (the system prompt is the group prompt, the user
        /// message a plain string) is answered with one heading per run.</summary>
        public static (HttpStatusCode, string) FactsThenFirstTwoTargetsThenGroup(JsonObject body, int post)
        {
            var messages = body["messages"]!.AsArray();
            if (messages[1]!["content"] is JsonValue)
            {
                var payload = JsonNode.Parse(messages[1]!["content"]!.GetValue<string>())!;
                var runs = payload["runs"]!.AsArray().Select(r => new
                {
                    run = r!["run"]!.GetValue<string>(),
                    headings = new[] { r["lines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>()).ToArray() },
                });
                return (HttpStatusCode.OK, Stream(new { content = JsonSerializer.Serialize(new { protocolVersion = "PDF_HEADING_GROUPS_V1", runs }) }, "stop"));
            }
            var unit = JsonNode.Parse(messages[1]!["content"]![0]!["text"]!.GetValue<string>())!;
            var targets = unit["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToArray();
            if (!messages.Any(m => m!["role"]!.GetValue<string>() == "tool"))
                return (HttpStatusCode.OK, Stream(new
                {
                    tool_calls = new[] { new { index = 0, id = "call_" + targets[0], type = "function",
                        function = new { name = "get_line_facts", arguments = JsonSerializer.Serialize(new { first = targets[0], last = targets[^1], offset = 0 }) } } },
                }, "tool_calls"));
            var answer = JsonSerializer.Serialize(new
            {
                protocolVersion = "P7_F1_V6_SELECT_OBJECTS_V1", status = "COMPLETE", analysis = "fake",
                decisions = targets.Select((a, i) => i < 2 ? (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a }, reason = "head" } : new { alias = a, decision = "NOT_SELECT" }),
            });
            return (HttpStatusCode.OK, Stream(new { content = answer }, "stop"));
        }

        /// <summary>First turn: one get_line_facts call over the targets. After a tool result: select the first target, nothing else.</summary>
        public static (HttpStatusCode, string) FactsThenFirstTarget(JsonObject body, int post)
        {
            var messages = body["messages"]!.AsArray();
            var payload = JsonNode.Parse(messages[1]!["content"]![0]!["text"]!.GetValue<string>())!;
            var targets = payload["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToArray();
            if (!messages.Any(m => m!["role"]!.GetValue<string>() == "tool"))
                return (HttpStatusCode.OK, Stream(new
                {
                    tool_calls = new[] { new { index = 0, id = "call_" + targets[0], type = "function",
                        function = new { name = "get_line_facts", arguments = JsonSerializer.Serialize(new { first = targets[0], last = targets[^1], offset = 0 }) } } },
                }, "tool_calls"));
            var answer = JsonSerializer.Serialize(new
            {
                protocolVersion = "P7_F1_V6_SELECT_OBJECTS_V1", status = "COMPLETE", analysis = "fake",
                decisions = targets.Select((a, i) => i == 0 ? (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a }, reason = "first" } : new { alias = a, decision = "NOT_SELECT" }),
            });
            return (HttpStatusCode.OK, Stream(new { content = answer }, "stop"));
        }
    }
}
