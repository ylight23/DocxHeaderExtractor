using System.Text.Json;
using DocxHeaderExtractor.HeadingAgent;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

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
    public void Large_grouping_sets_are_split_into_parallel_bounded_requests()
    {
        var selected = Enumerable.Range(0, 17)
            .SelectMany(i => new[] { A(i * 3), A(i * 3 + 1) })
            .ToHashSet(StringComparer.Ordinal);
        using var barrier = new Barrier(2);
        var active = 0;
        var maxActive = 0;
        var overlapped = 0;
        var (model, payloads, _) = Scripted((call, p) =>
        {
            var now = Interlocked.Increment(ref active);
            int seen;
            do { seen = maxActive; if (seen >= now) break; }
            while (Interlocked.CompareExchange(ref maxActive, now, seen) != seen);
            try
            {
                if (call <= 2)
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Grouping requests did not overlap.");
                    Interlocked.Exchange(ref overlapped, 1);
                }
                return Division(p, id => [RunAliases(p, id)]);
            }
            finally { Interlocked.Decrement(ref active); }
        });

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), parallelism: 2, CancellationToken.None, retryDelayMs: 0);

        Assert.Empty(result.Failures);
        Assert.Equal(3, result.Posts);
        Assert.Equal(new[] { 1, 8, 8 }, payloads.Select(p => p.GetProperty("runs").GetArrayLength()).Order());
        Assert.Equal(1, overlapped);
        Assert.Equal(2, maxActive);
        Assert.Equal(17, result.Groups.Count);
        Assert.Equal(selected.Order(), result.Groups.SelectMany(g => g).Order());
        Assert.Equal(3, result.Observation.Batches!.Count);
        Assert.All(result.Observation.Batches, batch => Assert.True(batch.CompletedAtSeconds >= batch.StartedAtSeconds));
    }

    [Fact]
    public void Budget_reserves_initial_grouping_and_possible_review_regroup_batches()
    {
        const int sourceLines = 1055;
        var chunks = (sourceLines + (2 * AgentGrouping.MaxRunsPerRequest) - 1) / (2 * AgentGrouping.MaxRunsPerRequest);
        var perPass = chunks * (1 + AgentGrouping.MaxRepairs) * (1 + AgentUnit.TransportRetries);

        Assert.Equal(2 * perPass, HeadingAgentSelector.GroupingPostsReserveForLines(sourceLines));
    }

    private static string Disagreement(JsonElement payload) => JsonSerializer.Serialize(new
    {
        protocolVersion = AgentGrouping.GroupVersion,
        runs = payload.GetProperty("runs").EnumerateArray().Select(r => new
        {
            run = r.GetProperty("run").GetString(),
            dispute = new { aliases = RunAliases(payload, r.GetProperty("run").GetString()!), reason = "These lines appear to continue body text; reconsider selection." }
        })
    });

    [Fact]
    public void Explicit_disagreement_is_retained_without_repair_or_layout_fallback()
    {
        var (model, _, _) = Scripted((_, p) => Disagreement(p));
        var result = AgentGrouping.Group(Source.Value, new HashSet<string> { A(10), A(11), A(20) }, model, Budget(), 1, CancellationToken.None, 0);
        Assert.Empty(result.Failures);
        Assert.Equal(1, result.Posts);
        Assert.Equal(new[] { A(10), A(11) }, Assert.Single(result.Disputes).Aliases);
        Assert.Equal(new[] { A(20) }, Assert.Single(result.Groups));
        Assert.Contains("dispute", Assert.Single(result.Evidence));
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("reaffirm")]
    [InlineData("partial")]
    [InlineData("failed")]
    [InlineData("redispute")]
    [InlineData("invalid")]
    [InlineData("budget")]
    public void Disagreement_goes_back_to_selection_once_and_only_disputed_decisions_can_change(string mode)
    {
        var source = Source.Value;
        var core = source.Cores.First(c => c.End - c.Start >= 6);
        var first = A(core.Start + 1); var second = A(core.Start + 2); var kept = A(core.Start + 4); var unrelated = A(core.Start + 5);
        var groupingCalls = 0; var reviewCalls = 0;
        var budget = Budget();
        AgentUnit.Model model = (unit, turn, request) =>
        {
            using var body = JsonDocument.Parse(request.Body);
            if (FakeOpenRouter.VerificationResponse(System.Text.Json.Nodes.JsonNode.Parse(request.Body)!.AsObject()) is { } verification)
                return new ModelTurn(verification, [], "stop");
            var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].GetProperty("text").GetString()!;
            using var doc = JsonDocument.Parse(text);
            var payload = doc.RootElement;
            if (payload.GetProperty("stage").GetString() == "GROUP")
            {
                groupingCalls++;
                if (mode == "budget") budget.Halt("TEST");
                return new ModelTurn(groupingCalls == 1 || mode == "redispute"
                    ? Disagreement(payload) : Division(payload, id => [RunAliases(payload, id)]), [], "stop");
            }
            var reviewing = payload.TryGetProperty("selectionReview", out var review);
            if (reviewing)
            {
                reviewCalls++;
                Assert.StartsWith("REVIEW-SEG-", unit);
                Assert.Equal(Instruction, payload.GetProperty("instruction").GetString());
                Assert.True(payload.GetProperty("targetFacts").GetArrayLength() > 0);
                Assert.Equal(new[] { first, second }, review.GetProperty("disputedAliases").EnumerateArray().Select(x => x.GetString()));
                Assert.True(review.TryGetProperty("previousSelection", out _));
                if (mode == "failed") throw new ReplyFailure("TEST_FAILURE");
                if (mode == "invalid") return new ModelTurn("not json", [], "stop");
            }
            return new ModelTurn(JsonSerializer.Serialize(new
            {
                protocolVersion = source.Flavor.AnswerVersion,
                status = "COMPLETE",
                decisions = payload.GetProperty("targets").EnumerateArray().Select(t =>
                {
                    var alias = t.GetProperty("alias").GetString()!;
                    var select = !reviewing ? alias == first || alias == second || alias == kept
                        : alias == unrelated || ((mode is "reaffirm" or "redispute") && (alias == first || alias == second)) || mode == "partial" && alias == first;
                    return new { alias, decision = select ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { alias } };
                })
            }), [], "stop");
        };
        var result = HeadingAgentSelector.Select(source, model, budget, 1, CancellationToken.None, 0, () => 0, verifyGroups: false);
        Assert.Contains(kept, result.SelectedAliases);
        Assert.DoesNotContain(unrelated, result.SelectedAliases);
        var expected = mode is "reaffirm" ? new[] { first, second, kept } : mode == "partial" ? new[] { first, kept } : new[] { kept };
        Assert.Equal(expected.Order(), result.SelectedAliases.Order());
        Assert.Equal(expected.Order(), result.Groups!.SelectMany(g => g).Order());
        Assert.Equal(mode == "budget" ? 0 : mode == "invalid" ? AgentSelectionReview.MaxPosts : 1, reviewCalls);
        Assert.Equal(mode is "reaffirm" or "redispute" ? 2 : 1, groupingCalls);
        Assert.Contains(result.ModelAnswers, a => a.Contains("selection-review") && a.Contains("before") && a.Contains("after"));
        Assert.Contains(result.ModelAnswers, a => a.Contains("ignoredDecisionChanges"));
        if (mode is "reject" or "reaffirm" or "partial") Assert.True(result.Grouping!.IgnoredReviewChanges > 0);
        if (mode is "failed" or "invalid" or "redispute" or "budget")
        {
            Assert.NotEmpty(result.UnitFailures);
            Assert.Equal(result.UnitsTotal - 1, result.UnitsComplete);
        }
        else { Assert.Empty(result.UnitFailures); Assert.Equal(result.UnitsTotal, result.UnitsComplete); }
    }

    [Theory]
    [InlineData("missing-alias")]
    [InlineData("unknown-alias")]
    [InlineData("empty-reason")]
    [InlineData("both-fields")]
    public void A_dispute_must_cover_its_exact_run_and_cannot_also_give_headings(string mode)
    {
        var source = Source.Value;
        var run = new AgentGrouping.Run("R1", [10, 11]);
        var aliases = mode == "missing-alias" ? new[] { A(10) } : mode == "unknown-alias" ? new[] { A(10), "unknown" } : new[] { A(10), A(11) };
        var item = new System.Text.Json.Nodes.JsonObject
        {
            ["run"] = "R1",
            ["dispute"] = JsonSerializer.SerializeToNode(new { aliases, reason = mode == "empty-reason" ? " " : "Review membership" })
        };
        if (mode == "both-fields") item["headings"] = JsonSerializer.SerializeToNode(new[] { new[] { A(10), A(11) } });
        var reply = new System.Text.Json.Nodes.JsonObject
        { ["protocolVersion"] = AgentGrouping.GroupVersion, ["runs"] = new System.Text.Json.Nodes.JsonArray(item) };
        Assert.Null(AgentGrouping.Parse(reply.ToJsonString(), [run], source, out var failure, out _));
        Assert.Equal("PROTOCOL:DISPUTE_INVALID", failure);
    }

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

    private static string Answer(params (string Run, string[][] Headings)[] runs) =>
        JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion, runs = runs.Select(r => new { run = r.Run, headings = r.Headings }) });

    [Fact]
    public void Runs_divided_correctly_are_kept_and_the_repair_asks_only_for_the_run_left_out()
    {
        // Live on 023 (26 runs in one request) the model left a run out on every turn, and the whole answer fell back to one heading per line.
        var selected = new HashSet<string> { A(10), A(11), A(12), A(40), A(41), A(60), A(61) };
        var (model, _, bodies) = Scripted((n, _) => n == 1
            ? Answer(("R1", [[A(10)], [A(11), A(12)]]), ("R3", [[A(60), A(61)]]))
            : Answer(("R2", [[A(40), A(41)]])));

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Posts);
        Assert.Equal(2, result.Answers.Count);
        Assert.Equal(new[] { new[] { A(10) }, new[] { A(11), A(12) }, new[] { A(40), A(41) }, new[] { A(60), A(61) } }, result.Groups.Select(g => g.ToArray()));
        using var second = JsonDocument.Parse(bodies[1]);
        var repair = second.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.Contains("run R2 is not in your answer", repair, StringComparison.Ordinal);
        Assert.Contains("were accepted and are kept", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("run R1", repair, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_never_answered_falls_back_alone_and_an_accepted_run_keeps_its_first_division()
    {
        var selected = new HashSet<string> { A(10), A(11), A(12), A(40), A(41) };
        // Every turn leaves R2 out; the repair turns also re-send R1 divided another way, which is ignored.
        var (model, _, _) = Scripted((n, _) => n == 1
            ? Answer(("R1", [[A(10)], [A(11), A(12)]]))
            : Answer(("R1", [[A(10), A(11), A(12)]])));

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Equal(1 + AgentGrouping.MaxRepairs, result.Posts);
        Assert.Equal("GROUP-001:PROTOCOL:RUN_MISSING:R2", result.Failures.Single());
        Assert.Equal(new[] { A(10) }, result.Groups[0]);
        Assert.Equal(new[] { A(11), A(12) }, result.Groups[1]);
        // R2 is divided by the fallback rule; nothing selected is lost.
        Assert.Equal(new[] { A(40), A(41) }, result.Groups.Skip(2).SelectMany(g => g));
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
