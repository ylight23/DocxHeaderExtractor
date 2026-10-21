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
            var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").Text()!).RootElement.Clone();
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
    public void Production_budget_reserves_grouping_only_without_semantic_review_or_verifier()
    {
        const int sourceLines = 1055;
        var chunks = (sourceLines + (2 * AgentGrouping.MaxRunsPerRequest) - 1) / (2 * AgentGrouping.MaxRunsPerRequest);
        var perPass = chunks * (1 + AgentGrouping.MaxRepairs) * (1 + AgentUnit.TransportRetries);

        Assert.Equal(perPass, HeadingAgentSelector.GroupingPostsReserveForLines(sourceLines));
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
    public void Dispute_is_rejected_as_protocol_and_valid_partition_repairs_it()
    {
        var calls = 0;
        var (model, _, _) = Scripted((_, p) => ++calls == 1 ? Disagreement(p) : Division(p, id => [RunAliases(p, id)]));
        var result = AgentGrouping.Group(Source.Value, new HashSet<string> { A(10), A(11), A(20) }, model, Budget(), 1, CancellationToken.None, 0);
        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Posts);
        Assert.Empty(result.Disputes);
        Assert.Equal(new[] { new[] { A(10), A(11) }, new[] { A(20) } }, result.Groups.Select(g => g.ToArray()));
        Assert.Equal(1, result.Observation.ModelDividedRuns);
        Assert.Equal(0, result.Observation.DisputedRuns);
        Assert.Contains("PROTOCOL:RUN_KEYS_INVALID", string.Join(" ", result.Evidence));
    }

    [Fact]
    public void Dispute_shaped_reply_gets_protocol_repair_and_cannot_reopen_selection()
    {
        var source = Source.Value;
        var core = source.Cores.First(c => c.End - c.Start >= 6);
        var first = A(core.Start + 1); var second = A(core.Start + 2); var kept = A(core.Start + 4); var unrelated = A(core.Start + 5);
        var groupingCalls = 0; var reviewCalls = 0;
        var budget = Budget();
        AgentUnit.Model model = (unit, turn, request) =>
        {
            using var body = JsonDocument.Parse(request.Body);
            var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].GetProperty("text").GetString()!;
            using var doc = JsonDocument.Parse(text);
            var payload = doc.RootElement;
            if (payload.GetProperty("stage").GetString() == "GROUP")
            {
                groupingCalls++;
                return new ModelTurn(groupingCalls == 1
                    ? Disagreement(payload) : Division(payload, id => [RunAliases(payload, id)]), [], "stop");
            }
            var reviewing = payload.TryGetProperty("selectionReview", out var review);
            if (reviewing)
            {
                reviewCalls++;
                throw new InvalidOperationException("Production must not invoke selection review.");
            }
            return new ModelTurn(JsonSerializer.Serialize(new
            {
                protocolVersion = source.Flavor.AnswerVersion,
                status = "COMPLETE",
                decisions = payload.GetProperty("targets").EnumerateArray().Select(t =>
                {
                    var alias = t.GetProperty("alias").GetString()!;
                    var select = alias == first || alias == second || alias == kept;
                    return new { alias, decision = select ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { alias } };
                })
            }), [], "stop");
        };
        var result = HeadingAgentSelector.Select(source, model, budget, 1, CancellationToken.None, 0, () => 0);
        Assert.Contains(first, result.SelectedAliases);
        Assert.Contains(second, result.SelectedAliases);
        Assert.Contains(kept, result.SelectedAliases);
        Assert.DoesNotContain(unrelated, result.SelectedAliases);
        var expected = new[] { first, second, kept };
        Assert.Equal(expected.Order(), result.SelectedAliases.Order());
        Assert.Equal(expected.Order(), result.Groups!.SelectMany(g => g).Order());
        Assert.Equal(0, reviewCalls);
        Assert.Equal(2, groupingCalls);
        Assert.DoesNotContain(result.ModelAnswers, a => a.Contains("selection-review", StringComparison.Ordinal));
        Assert.Equal(0, result.Grouping!.VerificationChecks);
        Assert.Equal(result.UnitsTotal, result.UnitsComplete);
    }

    [Fact]
    public void Persistent_dispute_shaped_reply_is_protocol_failure_and_remains_fail_closed()
    {
        var source = Source.Value;
        var core = source.Cores.First(c => c.End - c.Start >= 6);
        var selected = new[] { A(core.Start + 1), A(core.Start + 2) };
        var groupingCalls = 0;
        AgentUnit.Model model = (unit, turn, request) =>
        {
            using var body = JsonDocument.Parse(request.Body);
            if (FakeOpenRouter.VerificationResponse(System.Text.Json.Nodes.JsonNode.Parse(request.Body)!.AsObject()) is { } verification)
                return new ModelTurn(verification, [], "stop");
            var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].GetProperty("text").GetString()!;
            using var payload = JsonDocument.Parse(text);
            if (payload.RootElement.GetProperty("stage").GetString() == "GROUP")
            {
                groupingCalls++;
                return new ModelTurn(Disagreement(payload.RootElement), [], "stop");
            }
            var targets = payload.RootElement.GetProperty("targets").EnumerateArray();
            return new ModelTurn(JsonSerializer.Serialize(new
            {
                protocolVersion = source.Flavor.AnswerVersion,
                status = "COMPLETE",
                decisions = targets.Select(t => new { alias = t.GetProperty("alias").GetString(), decision = selected.Contains(t.GetProperty("alias").GetString()!) ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { t.GetProperty("alias").GetString()! } })
            }), [], "stop");
        };
        var result = HeadingAgentSelector.Select(source, model, Budget(), 1, CancellationToken.None, 0, () => 0);
        Assert.True(groupingCalls > 0, string.Join("; ", result.UnitFailures) + " | " + string.Join(" | ", result.ModelAnswers));
        // A refused reply is repaired up to the limit; a repeated one is not a reason to stop.
        Assert.Equal(1 + AgentGrouping.MaxRepairs, groupingCalls);
        Assert.All(selected, alias => Assert.DoesNotContain(alias, result.SelectedAliases));
        Assert.DoesNotContain(result.Groups!.SelectMany(g => g), alias => selected.Contains(alias));
        Assert.Contains(result.UnitFailures, f => f.Contains("PROTOCOL:RUN_KEYS_INVALID", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispute_is_not_a_valid_grouping_contract_field()
    {
        var source = Source.Value;
        var run = new AgentGrouping.Run("R1", [10, 11]);
        var item = new System.Text.Json.Nodes.JsonObject
        {
            ["run"] = "R1",
            ["dispute"] = JsonSerializer.SerializeToNode(new { aliases = new[] { A(10), A(11) }, reason = "Review membership" })
        };
        var reply = new System.Text.Json.Nodes.JsonObject
        { ["protocolVersion"] = AgentGrouping.GroupVersion, ["runs"] = new System.Text.Json.Nodes.JsonArray(item) };
        Assert.Null(AgentGrouping.Parse(reply.ToJsonString(), [run], source, out var failure, out _));
        Assert.Equal("PROTOCOL:RUN_KEYS_INVALID", failure);
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
        // Each turn's refusal differs (here only in trailing spaces), so both repair turns are used; a reply repeated word for word stops
        // earlier (A_refused_reply_repeated_word_for_word_stops_after_one_repair).
        var (model, payloads, bodies) = Scripted((n, p) => Bad(p) + new string(' ', n));

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Equal(1 + AgentGrouping.MaxRepairs, result.Posts);
        Assert.Single(result.Failures);
        Assert.StartsWith("GROUP-001:PROTOCOL:", result.Failures[0]);
        Assert.Empty(result.Answers);
        // The repair turns carry the refused reply and a message naming the refusal.
        using var last = JsonDocument.Parse(bodies[^1]);
        Assert.Contains("was refused", last.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").Text());
        // Fallback: consecutive lines of one page and one layout block stay together, and Group keeps every selected line. (The production
        // selector then withholds these FALLBACK aliases as UNRESOLVED: Persistent_dispute_shaped_reply_is_protocol_failure_and_remains_fail_closed.)
        Assert.Equal(new[] { A(10), A(11), A(12) }.Order(), result.Groups.SelectMany(g => g).Order());
        var source = Source.Value;
        foreach (var g in result.Groups.Where(g => g.Count > 1))
            Assert.All(g.Skip(1), (alias, k) =>
            {
                var i = source.Lines.ToList().FindIndex(l => l.Alias == alias);
                Assert.Equal(source.Blocks[i - 1], source.Blocks[i]);
            });
    }

    // 023, GROUP-003: the model divided every run right but wrote one "runs" key per run. JsonDocument keeps only the last, so the old parser
    // called the other runs missing and the model repeated the same answer until they fell back.
    [Fact]
    public void A_runs_key_written_once_per_run_is_read_as_one_list_and_no_run_is_lost()
    {
        var selected = new HashSet<string> { A(10), A(11), A(20), A(21) };
        var (model, payloads, bodies) = Scripted((n, p) =>
        {
            var ids = p.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("run").GetString()!).ToArray();
            var repeated = string.Join(",", ids.Select(id => "\"runs\":[" + JsonSerializer.Serialize(new { run = id, headings = new[] { RunAliases(p, id) } }) + "]"));
            return "{\"protocolVersion\":\"" + AgentGrouping.GroupVersion + "\"," + repeated + "}";
        });

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Equal(1, result.Posts);
        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Observation.ModelDividedRuns);
        Assert.Equal(0, result.Observation.FallbackRuns);
        var groups = result.Groups.Select(g => string.Join("|", g)).ToArray();
        Assert.Contains(A(10) + "|" + A(11), groups);
        Assert.Contains(A(20) + "|" + A(21), groups);
    }

    [Fact]
    public void A_run_listed_twice_across_repeated_runs_keys_is_still_refused()
    {
        var selected = new HashSet<string> { A(10), A(11) };
        var (model, payloads, bodies) = Scripted((n, p) =>
        {
            if (n > 1) return Division(p, id => [RunAliases(p, id)]);
            var run = JsonSerializer.Serialize(new { run = "R1", headings = new[] { RunAliases(p, "R1") } });
            return "{\"protocolVersion\":\"" + AgentGrouping.GroupVersion + "\",\"runs\":[" + run + "],\"runs\":[" + run + "]}";
        });

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Equal(2, result.Posts);
        using var repair = JsonDocument.Parse(bodies[1]);
        var message = repair.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").Text()!;
        Assert.Contains("PROTOCOL:RUN_LISTED_TWICE", message);
    }

    [Fact]
    public void A_repeated_key_inside_a_run_is_found_too()
    {
        using var doc = JsonDocument.Parse("{\"runs\":[{\"run\":\"R1\",\"headings\":[[\"a\"]],\"headings\":[[\"b\"]]}]}");
        Assert.Equal(("headings", 2), AgentProtocol.RepeatedKey(doc.RootElement));
        using var clean = JsonDocument.Parse("{\"runs\":[{\"run\":\"R1\",\"headings\":[[\"a\"]]}]}");
        Assert.Null(AgentProtocol.RepeatedKey(clean.RootElement));
    }

    [Fact]
    public void A_selection_answer_with_a_repeated_decisions_key_is_refused_by_name_not_as_missing_targets()
    {
        var shown = new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 0, ["b"] = 1 };
        var reply = "{\"protocolVersion\":\"" + SelectObjectsVersion + "\",\"status\":\"COMPLETE\","
            + "\"decisions\":[{\"alias\":\"a\",\"decision\":\"NOT_SELECT\"}],\"decisions\":[{\"alias\":\"b\",\"decision\":\"NOT_SELECT\"}]}";

        var parsed = ParseSelect(reply, shown, ["a", "b"]);

        Assert.False(parsed.Ok);
        Assert.Equal("KEY_REPEATED", parsed.Failure);
        var message = RepairMessage("PROTOCOL:" + parsed.Failure, parsed.Missing, parsed.Detail);
        Assert.Contains("\"decisions\" appears 2 times", message);
        Assert.Contains("one \"decisions\" array", message);
    }

    [Fact]
    public void A_refused_reply_repeated_word_for_word_is_still_repaired_to_the_limit()
    {
        var selected = new HashSet<string> { A(10), A(11), A(12) };
        var (model, _, _) = Scripted((_, p) => Division(p, id => [[A(10), A(11)]]));   // A(12) left out, every time the same

        var result = AgentGrouping.Group(Source.Value, selected, model, Budget(), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Equal(1 + AgentGrouping.MaxRepairs, result.Posts);
        Assert.Equal("GROUP-001:PROTOCOL:RUN_NOT_COVERED:R1", result.Failures.Single());
        Assert.Equal(1, result.Observation.FallbackRuns);
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
        var repair = second.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").Text()!;
        Assert.Contains("run R1 is missing " + A(12), repair, StringComparison.Ordinal);
        Assert.Contains("run R2 is missing " + A(42), repair, StringComparison.Ordinal);
        Assert.Contains("Selection has already decided that every alias in each run is a heading", repair, StringComparison.Ordinal);
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
        var repair = second.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").Text()!;
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
        Assert.Equal(GroupingPrompts.Shared, root.GetProperty("messages")[0].GetProperty("content").Text());
        Assert.Contains("JSON", GroupingPrompts.Shared, StringComparison.Ordinal);
    }
}
