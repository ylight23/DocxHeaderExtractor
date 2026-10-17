using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>A fake OpenRouter: answers each POST with a streamed reply the script chooses.</summary>
internal sealed class FakeOpenRouter(Func<JsonObject, int, (HttpStatusCode Status, string Sse)> script, string provider = "Alibaba") : HttpMessageHandler
{
    private int posts;
    public int Posts => posts;
    public List<string> Keys { get; } = new();
    /// <summary>The body of every POST, in arrival order.</summary>
    public List<JsonObject> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        lock (Keys) Keys.Add(request.Headers.Authorization!.ToString());
        lock (Bodies) Bodies.Add(body);
        var n = Interlocked.Increment(ref posts);
        var (status, sse) = VerificationResponse(body) is { } verification
            ? (HttpStatusCode.OK, Stream(new { content = verification }, "stop")) : script(body, n);
        return new HttpResponseMessage(status) { Content = new StringContent(sse.Replace("PROVIDER", provider), Encoding.UTF8, "text/event-stream") };
    }

    private static string Event(object o) => "data: " + JsonSerializer.Serialize(o) + "\n\n";

    internal static bool IsVerification(JsonObject body) => VerificationPayload(body) is not null;
    internal static JsonObject? VerificationPayload(JsonObject body)
    {
        if (body["messages"]?[1]?["content"] is not JsonValue content || !content.TryGetValue<string>(out var text)) return null;
        var payload = JsonNode.Parse(text) as JsonObject;
        return payload?["stage"]?.GetValue<string>() == "GROUP_VERIFICATION" ? payload : null;
    }
    internal static string? VerificationResponse(JsonObject body)
    {
        var payload = VerificationPayload(body);
        if (payload is null) return null;
        return JsonSerializer.Serialize(new
        {
            protocolVersion = DocxHeaderExtractor.HeadingAgent.AgentGroupVerification.Version,
            verdicts = payload["candidates"]!.AsArray().Select(c => new
            {
                run = c!["run"]!.GetValue<string>(), decision = "ACCEPT", reason = "Scripted independent verifier acceptance.",
                evidenceRefs = new[] { c["proposedGroups"]![0]![0]!.GetValue<string>() }
            })
        });
    }

    internal static string Stream(object delta, string finish) =>
        ": OPENROUTER PROCESSING\n\n"
        + Event(new { id = "gen-1", provider = "PROVIDER", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta } } })
        + Event(new { id = "gen-1", provider = "PROVIDER", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta = new { }, finish_reason = finish } },
            usage = new { prompt_tokens = 1000, completion_tokens = 200, completion_tokens_details = new { reasoning_tokens = 150 }, cost = 0.001 } })
        + "data: [DONE]\n\n";

    /// <summary>The most targets one get_line_facts call returns: the tool's cap per call (the compact facts of a unit fit it).</summary>
    public const int MaxFactsPerCall = 24;

    /// <summary>Facts calls for one turn: get_line_facts over the targets at offset 0, 24, ... (at most <paramref name="pages"/> calls).</summary>
    public static object[] FactsCalls(IReadOnlyList<string> targets, int pages = int.MaxValue) =>
        Enumerable.Range(0, Math.Min(pages, (targets.Count + 23) / 24)).Select(k => (object)new
        {
            index = k, id = "call_" + targets[0] + "_" + k, type = "function",
            function = new { name = "get_line_facts", arguments = JsonSerializer.Serialize(new { first = targets[0], last = targets[^1], offset = 24 * k }) },
        }).ToArray();

    /// <summary>The facts calls of a first turn as the route's model makes them: a DOCX unit needs the core facts of every target (every page of
    /// them), so all pages; a PDF unit keeps the qualified single call over its range.</summary>
    public static object[] FirstTurnFacts(JsonObject body, IReadOnlyList<string> targets) => AnswerVersion(body) != "P7_F1_V6_SELECT_OBJECTS_V1"
        ? FactsCalls(targets)
        : [new { index = 0, id = "call_" + targets[0], type = "function",
            function = new { name = "get_line_facts", arguments = JsonSerializer.Serialize(new { first = targets[0], last = targets[^1], offset = 0 }) } }];

    /// <summary>The answer format the request's system prompt names: the DOCX one carries heading extents.</summary>
    public static string AnswerVersion(JsonObject body)
    {
        var system = body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        return system.Contains("P7_F1_V6_SELECT_OBJECTS_SCAN_V1", StringComparison.Ordinal) ? "P7_F1_V6_SELECT_OBJECTS_SCAN_V1"
            : system.Contains("P7_F1_V6_SELECT_OBJECTS_EXTENT_V1", StringComparison.Ordinal) ? "P7_F1_V6_SELECT_OBJECTS_EXTENT_V1" : "P7_F1_V6_SELECT_OBJECTS_V1";
    }

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
            return (HttpStatusCode.OK, Stream(new { tool_calls = FirstTurnFacts(body, targets) }, "tool_calls"));
        var answer = JsonSerializer.Serialize(new
        {
            protocolVersion = AnswerVersion(body), status = "COMPLETE", analysis = "fake",
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
            return (HttpStatusCode.OK, Stream(new { tool_calls = FirstTurnFacts(body, targets) }, "tool_calls"));
        var answer = JsonSerializer.Serialize(new
        {
            protocolVersion = AnswerVersion(body), status = "COMPLETE", analysis = "fake",
            decisions = targets.Select((a, i) => i == 0 ? (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a }, reason = "first" } : new { alias = a, decision = "NOT_SELECT" }),
        });
        return (HttpStatusCode.OK, Stream(new { content = answer }, "stop"));
    }
}
