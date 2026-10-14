using System.Text.Json;
using DocxHeaderExtractor.PdfHeadingAgent;
using static DocxHeaderExtractor.PdfHeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The grouping step: C# builds runs of selected lines that are adjacent in reading order, the model divides each run into headings, C# checks the
/// division and falls back to the layout rule for a run it cannot accept. No network.
/// </summary>
public sealed class PdfHeadingAgentGroupingTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(Pdf), out _)!);

    private static string A(int i) => Source.Value.Lines[i].Alias;

    private static AgentBudget Budget() => new(1000, 100);

    /// <summary>A scripted model: hands every request payload to the script and returns its content as a stop reply.</summary>
    private static (AgentUnit.Model Model, List<JsonElement> Payloads, List<string> Bodies) Scripted(Func<int, JsonElement, string?> script, Func<int, string?>? throwOn = null)
    {
        var payloads = new List<JsonElement>(); var bodies = new List<string>();
        var post = 0;
        return ((unit, turn, req) =>
        {
            var n = Interlocked.Increment(ref post);
            if (throwOn?.Invoke(n) is { } code) throw new ReplyFailure(code);
            using var body = JsonDocument.Parse(req.Body);
            var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!).RootElement.Clone();
            lock (payloads) { payloads.Add(payload); bodies.Add(req.Body); }
            return new ModelTurn(script(n, payload), [], "stop");
        }, payloads, bodies);
    }

    private static string Division(JsonElement payload, Func<string, string[][]> headings) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = AgentGrouping.GroupVersion,
            runs = payload.GetProperty("runs").EnumerateArray().Select(r => new { run = r.GetProperty("run").GetString(), headings = headings(r.GetProperty("run").GetString()!) }),
        });

    private static string[] RunAliases(JsonElement payload, string id) =>
        payload.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("run").GetString() == id).GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("alias").GetString()!).ToArray();

    [Fact]
    public void Only_runs_of_two_or_more_adjacent_selected_lines_are_asked_about()
    {
        var selected = new HashSet<string> { A(10), A(11), A(12), A(20), A(40), A(41) };
        var (model, payloads, _) = Scripted((_, p) => Division(p, id => [RunAliases(p, id)]));

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);

        var asked = payloads.Single().GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("run").GetString()).ToArray();
        Assert.Equal(new[] { "R1", "R2" }, asked);
        Assert.Equal(new[] { A(10), A(11), A(12) }, RunAliases(payloads.Single(), "R1"));
        Assert.Equal(new[] { A(40), A(41) }, RunAliases(payloads.Single(), "R2"));
        // The lines next to a run are shown as context, not as part of it.
        var r1 = payloads.Single().GetProperty("runs")[0];
        Assert.Equal(A(9), r1.GetProperty("before").GetProperty("alias").GetString());
        Assert.Equal(A(13), r1.GetProperty("after").GetProperty("alias").GetString());
        // Headings come out in document order; a lone selected line is a heading of its own and was never asked about.
        Assert.Equal(new[] { new[] { A(10), A(11), A(12) }, new[] { A(20) }, new[] { A(40), A(41) } }, result.Groups.Select(g => g.ToArray()));
        Assert.Empty(result.Failures);
        Assert.Equal(1, result.Posts);
    }

    [Fact]
    public void No_request_is_sent_when_no_two_selected_lines_are_adjacent()
    {
        var (model, payloads, _) = Scripted((_, _) => throw new InvalidOperationException("must not be called"));
        var result = AgentGrouping.Group(Source.Value, new HashSet<string> { A(5), A(7), A(9) }, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);
        Assert.Empty(payloads);
        Assert.Equal(0, result.Posts);
        Assert.Equal(new[] { new[] { A(5) }, new[] { A(7) }, new[] { A(9) } }, result.Groups.Select(g => g.ToArray()));
    }

    [Fact]
    public void The_model_may_split_a_run_into_several_headings()
    {
        var selected = new HashSet<string> { A(10), A(11), A(12) };
        var (model, _, _) = Scripted((_, p) => Division(p, id => [[A(10), A(11)], [A(12)]]));
        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);
        Assert.Equal(new[] { new[] { A(10), A(11) }, new[] { A(12) } }, result.Groups.Select(g => g.ToArray()));
    }

    public static IEnumerable<object[]> RefusedDivisions() => new[]
    {
        "missing-line", "out-of-order", "unknown-alias", "line-in-two-headings", "over-eight", "extra-key", "run-missing", "not-json",
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(RefusedDivisions))]
    public void A_division_that_is_not_a_clean_partition_is_refused_repaired_twice_and_then_the_layout_rule_decides(string kind)
    {
        var selected = new HashSet<string> { A(10), A(11), A(12) };
        string Bad(JsonElement p) => kind switch
        {
            "missing-line" => Division(p, id => [[A(10), A(11)]]),
            "out-of-order" => Division(p, id => [[A(11), A(10), A(12)]]),
            "unknown-alias" => Division(p, id => [[A(10), A(11), "L9999:S0"]]),
            "line-in-two-headings" => Division(p, id => [[A(10), A(11)], [A(11), A(12)]]),
            "over-eight" => Division(p, id => [Enumerable.Repeat(A(10), 9).ToArray()]),
            "extra-key" => Division(p, id => [[A(10), A(11), A(12)]]).Replace("\"runs\"", "\"level\":1,\"runs\""),
            "run-missing" => JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion, runs = Array.Empty<object>() }),
            _ => "here are the headings",
        };
        var (model, payloads, bodies) = Scripted((_, p) => Bad(p));

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Equal(1 + AgentGrouping.MaxRepairs, result.Posts);
        Assert.Single(result.Failures);
        Assert.StartsWith("GROUP-001:PROTOCOL:", result.Failures[0]);
        Assert.Empty(result.Answers);
        // The repair turns carry the refused reply and a message naming the refusal.
        using var last = JsonDocument.Parse(bodies[^1]);
        Assert.Contains("was refused", last.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString());
        // Fallback: consecutive lines of one page and one layout block stay together; nothing selected is lost.
        Assert.Equal(new[] { A(10), A(11), A(12) }.Order(), result.Groups.SelectMany(g => g).Order());
        var source = Source.Value;
        foreach (var g in result.Groups.Where(g => g.Count > 1))
            Assert.All(g.Skip(1), (alias, k) =>
            {
                var i = source.Lines.ToList().FindIndex(l => l.Alias == alias);
                Assert.Equal(source.Blocks[i - 1], source.Blocks[i]);
            });
    }

    [Fact]
    public void The_repair_message_names_every_line_left_out_of_every_run_so_one_repair_is_enough()
    {
        // Two runs; the model leaves out the last line of both, as the live model did for the 3-line runs.
        var selected = new HashSet<string> { A(10), A(11), A(12), A(40), A(41), A(42) };
        var (model, payloads, bodies) = Scripted((n, p) => n == 1
            ? Division(p, id => [RunAliases(p, id)[..^1]])
            : Division(p, id => [RunAliases(p, id)]));

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Posts);
        using var second = JsonDocument.Parse(bodies[1]);
        var repair = second.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.Contains("run R1 is missing " + A(12), repair, StringComparison.Ordinal);
        Assert.Contains("run R2 is missing " + A(42), repair, StringComparison.Ordinal);
        Assert.Contains("a line that is a heading of its own goes in a group of its own", repair, StringComparison.Ordinal);
        Assert.Equal(new[] { new[] { A(10), A(11), A(12) }, new[] { A(40), A(41), A(42) } }, result.Groups.Select(g => g.ToArray()));
    }

    [Fact]
    public void A_repaired_answer_is_accepted_and_a_transport_failure_is_retried()
    {
        var selected = new HashSet<string> { A(10), A(11) };
        var (model, payloads, _) = Scripted(
            (n, p) => n == 2 ? "not json" : Division(p, id => [[A(10), A(11)]]),
            throwOn: n => n == 1 ? "TRANSPORT:STREAM_ERROR" : null);
        // Post 1 fails in transport and is sent again (the same request), post 2 is refused, post 3 is accepted after one repair.
        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);
        Assert.Empty(result.Failures);
        Assert.Equal(new[] { new[] { A(10), A(11) } }, result.Groups.Select(g => g.ToArray()));
        Assert.Single(result.Answers);
    }

    [Fact]
    public void A_fenced_answer_is_read_and_a_refused_budget_falls_back()
    {
        var selected = new HashSet<string> { A(10), A(11) };
        var (model, _, _) = Scripted((_, p) => "```json\n" + Division(p, id => [[A(10), A(11)]]) + "\n```");
        Assert.Equal(new[] { new[] { A(10), A(11) } }, AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, 0).Groups.Select(g => g.ToArray()));

        var halted = Budget(); halted.Halt("TEST");
        var (never, payloads, _) = Scripted((_, _) => "unused");
        var result = AgentGrouping.Group(Source.Value, selected, never, halted, 1, CancellationToken.None, 0);
        Assert.Empty(payloads);
        Assert.StartsWith("GROUP-001:BUDGET:HALTED:TEST", result.Failures.Single());
        Assert.Equal(selected.Order(), result.Groups.SelectMany(g => g).Order());
    }

    [Fact]
    public void The_group_request_is_json_object_with_no_tools_and_the_instruction_verbatim()
    {
        var (model, payloads, bodies) = Scripted((_, p) => Division(p, id => [RunAliases(p, id)]));
        AgentGrouping.Group(Source.Value, new HashSet<string> { A(10), A(11) }, model, Budget(), 1, CancellationToken.None, 0);
        using var body = JsonDocument.Parse(bodies.Single());
        var root = body.RootElement;
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("tool_choice", out _));
        Assert.Equal(ChatModel, root.GetProperty("model").GetString());
        Assert.Equal("alibaba", root.GetProperty("provider").GetProperty("only")[0].GetString());
        Assert.Equal(Instruction, payloads.Single().GetProperty("instruction").GetString());
        Assert.Equal(AgentGrouping.GroupPrompt, root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("JSON", AgentGrouping.GroupPrompt, StringComparison.Ordinal);
    }
}
