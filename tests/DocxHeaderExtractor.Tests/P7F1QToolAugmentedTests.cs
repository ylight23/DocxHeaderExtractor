using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Deterministic, non-provider tests for the P7-F1Q tool-augmented harness (Issue #5).</summary>
public sealed class P7F1QToolAugmentedTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "p7f1q-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static JsonElement Box(double l, double r, double b, double t) => JsonSerializer.SerializeToElement(new { left = l, right = r, bottom = b, top = t });
    private static JsonElement Font(double size, double bold) => JsonSerializer.SerializeToElement(new { fontSize = size, fontName = "times", boldRatio = bold, italicRatio = 0.0 });

    private static readonly F1QSourceLine[] Lines =
    [
        new("L0000:S0", 0, 1, "CONTENTS", Box(50, 120, 700, 712), Font(14, 1)),
        new("L0001:S0", 1, 1, "1. Introduction .......... 4", Box(50, 500, 680, 690), Font(11, 0)),
        new("L0002:S0", 2, 1, "Page 1 of 9", Box(280, 330, 20, 30), Font(9, 0)),
        new("L0003:S0", 3, 4, "1. Introduction", Box(50, 160, 700, 712), Font(14, 1)),
        new("L0004:S0", 4, 4, "Body text follows here.", Box(50, 520, 680, 690), Font(11, 0)),
        new("L0005:S0", 5, 4, "Page 4 of 9", Box(280, 330, 20, 30), null),
    ];
    private static readonly Dictionary<string, string> IssuedMap = new() { ["O1"] = "L0000:S0", ["O2"] = "L0001:S0", ["O3"] = "L0002:S0" };
    private static P7F1QEvidenceTools Tools() => new("src", "universe", "store", Lines, IssuedMap);
    private static readonly F1QIssuedOccurrence[] Issued =
        [new("O1", "L0000:S0", 1, "CONTENTS"), new("O2", "L0001:S0", 1, "1. Introduction .......... 4"), new("O3", "L0002:S0", 1, "Page 1 of 9")];
    private static readonly HashSet<string> Citable = ["L0000:S0", "L0001:S0", "L0002:S0"];

    private static JsonElement Result(F1QToolResult r) => JsonDocument.Parse(r.Content).RootElement;

    [Fact] public void Context_tool_resolves_issued_ids_and_aliases_in_reading_order()
    {
        var r = Tools().Execute("E1.1", "get_occurrence_context", """{"target":"O2","before":1,"after":2}""");
        Assert.Equal("OK", r.Status);
        var lines = Result(r).GetProperty("result").GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("sourceAlias").GetString()!).ToArray();
        Assert.Equal(["L0000:S0", "L0001:S0", "L0002:S0", "L0003:S0"], lines);
        Assert.Equal(lines, r.ReturnedAliases);
        var byAlias = Tools().Execute("E1.1", "get_occurrence_context", """{"target":"L0001:S0","before":1,"after":2}""");
        Assert.Equal(r.ContentSha256, byAlias.ContentSha256); // arguments are normalized to the resolved alias
        Assert.Equal("L0001:S0", Result(byAlias).GetProperty("result").GetProperty("targetAlias").GetString());
    }

    [Fact] public void Tool_results_are_deterministic_and_carry_provenance_without_labels()
    {
        var a = Tools().Execute("E2.1", "get_source_span", """{"targets":["O1","L0003:S0"]}""");
        var b = Tools().Execute("E2.1", "get_source_span", """{"targets":["O1","L0003:S0"]}""");
        Assert.Equal(a.ContentSha256, b.ContentSha256);
        var json = Result(a);
        Assert.Equal("NONE", json.GetProperty("provenance").GetProperty("semanticLabels").GetString());
        Assert.True(json.GetProperty("provenance").GetProperty("readOnly").GetBoolean());
        Assert.DoesNotContain("isHeading", Encoding.UTF8.GetString(a.Content));
        Assert.Equal(2, json.GetProperty("result").GetProperty("records").GetArrayLength());
    }

    [Theory]
    [InlineData("get_occurrence_context", """{"target":"O9","before":1,"after":1}""", "TARGET_NOT_ISSUED_OR_NOT_IN_DOCUMENT")]
    [InlineData("get_occurrence_context", """{"target":"O1","before":7,"after":1}""", "ARGUMENT_OUT_OF_RANGE")]
    [InlineData("get_occurrence_context", """{"target":"O1","before":1}""", "ARGUMENT_REQUIRED_MISSING")]
    [InlineData("get_occurrence_context", """{"target":"O1","before":1,"after":1,"x":1}""", "ARGUMENT_KEYS_UNKNOWN_OR_DUPLICATE")]
    [InlineData("get_occurrence_context", """{"target":1,"before":1,"after":1}""", "ARGUMENT_TYPE_INVALID")]
    [InlineData("get_occurrence_context", "not json", "ARGUMENTS_NOT_JSON")]
    [InlineData("get_occurrence_context", "[1]", "ARGUMENTS_NOT_OBJECT")]
    [InlineData("get_occurrence_context", "", "ARGUMENTS_EMPTY_OR_TOO_LARGE")]
    [InlineData("get_page_geometry", """{"page":99,"offset":0,"maxLines":5}""", "PAGE_NOT_IN_DOCUMENT")]
    [InlineData("get_page_geometry", """{"page":1,"offset":0,"maxLines":41}""", "ARGUMENT_OUT_OF_RANGE")]
    [InlineData("get_source_span", """{"targets":[]}""", "TARGETS_INVALID")]
    [InlineData("get_source_span", """{"targets":["O1","O2","O3","L0003:S0","L0004:S0","L0005:S0","O1","O2","O3"]}""", "TARGETS_INVALID")]
    [InlineData("get_repeated_occurrences", """{"target":"O1","maxResults":0}""", "ARGUMENT_OUT_OF_RANGE")]
    [InlineData("delete_everything", """{}""", "UNKNOWN_TOOL")]
    public void Invalid_arguments_fail_closed_with_a_typed_error_and_no_returned_evidence(string tool, string args, string code)
    {
        var r = Tools().Execute("E1.1", tool, args);
        Assert.Equal("INVALID_ARGUMENT", r.Status);
        Assert.Equal(code, r.ErrorCode);
        Assert.Empty(r.ReturnedAliases);
        Assert.Equal(JsonValueKind.Null, Result(r).GetProperty("result").ValueKind);
    }

    [Fact] public void Geometry_reports_observations_and_page_extent()
    {
        var r = Tools().Execute("E1.1", "get_page_geometry", """{"page":4,"offset":0,"maxLines":40}""");
        var result = Result(r).GetProperty("result");
        Assert.Equal(3, result.GetProperty("pageLineCount").GetInt32());
        Assert.Equal(50, result.GetProperty("textExtent").GetProperty("minLeft").GetDouble());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("lines")[2].GetProperty("font").ValueKind);
        Assert.Equal(["L0003:S0", "L0004:S0", "L0005:S0"], r.ReturnedAliases);
    }

    [Fact] public void Geometry_truncates_deterministically_under_the_byte_cap()
    {
        var many = Enumerable.Range(0, 40).Select(i => new F1QSourceLine($"L{i:D4}:S0", i, 1, new string('ữ', 160), Box(1, 2, 3, 4), Font(10, 0))).ToArray();
        var tools = new P7F1QEvidenceTools("s", "u", "st", many, new Dictionary<string, string> { ["O1"] = "L0000:S0" });
        var r = tools.Execute("E1.1", "get_page_geometry", """{"page":1,"offset":0,"maxLines":40}""");
        Assert.True(r.Content.Length <= P7F1QEvidenceTools.ResultByteCap);
        var returned = Result(r).GetProperty("result").GetProperty("returnedLines").GetInt32();
        Assert.InRange(returned, 1, 39);
        Assert.Equal(returned, r.ReturnedAliases.Count);
    }

    [Fact] public void Repeated_occurrences_match_contents_entries_and_page_furniture_by_fixed_normalization()
    {
        Assert.Equal(P7F1QEvidenceTools.RepeatKey("1. Introduction .......... 4"), P7F1QEvidenceTools.RepeatKey("1. Introduction"));
        var toc = Result(Tools().Execute("E1.1", "get_repeated_occurrences", """{"target":"O2","maxResults":5}""")).GetProperty("result");
        Assert.Equal("L0003:S0", toc.GetProperty("matches")[0].GetProperty("sourceAlias").GetString());
        Assert.False(toc.GetProperty("matches")[0].GetProperty("exactTextMatch").GetBoolean());
        var footer = Result(Tools().Execute("E1.1", "get_repeated_occurrences", """{"target":"O3","maxResults":5}""")).GetProperty("result");
        Assert.Equal(1, footer.GetProperty("totalMatches").GetInt32());
        Assert.Equal("L0005:S0", footer.GetProperty("matches")[0].GetProperty("sourceAlias").GetString());
    }

    [Fact] public void Tool_universe_rejects_issued_aliases_outside_the_document()
    {
        Assert.Throws<InvalidOperationException>(() => new P7F1QEvidenceTools("s", "u", "st", Lines, new Dictionary<string, string> { ["O1"] = "L9999:S0" }));
        Assert.Throws<InvalidOperationException>(() => new P7F1QEvidenceTools("s", "u", "st", Lines, new Dictionary<string, string> { ["O1"] = "L0000:S0", ["O2"] = "L0000:S0" }));
    }

    private static string Decision(string id, string assessment = "SUPPORTED", string? function = "OTHER", string refs = "\"L0000:S0\"", string missing = "") =>
        $$"""{"occurrence":"{{id}}","assessment":"{{assessment}}","function":{{(function is null ? "null" : $"\"{function}\"")}},"observedRole":"role","evidenceRefs":[{{refs}}],"interpretation":"short","missingEvidence":[{{missing}}]}""";
    private static string Response(params string[] decisions) => $$"""{"protocolVersion":"P7_F1Q_V1","decisions":[{{string.Join(",", decisions)}}]}""";

    [Fact] public void Valid_response_with_supported_and_abstained_rows_is_strictly_accepted()
    {
        var v = P7F1QProtocol.Validate(Response(Decision("O1", function: "REPRESENTS_STRUCTURE"),
            Decision("O2", "INSUFFICIENT_EVIDENCE", null, missing: "\"PAGE_IMAGE\""), Decision("O3")), Issued, Citable);
        Assert.True(v.StrictAccepted, v.FailureCode);
        Assert.Null(v.Rows[1].Decision!.Function);
        Assert.Equal("INSUFFICIENT_EVIDENCE", v.Rows[1].Decision!.Assessment);
    }

    [Fact] public void All_abstention_response_is_a_valid_contract_not_silent_OTHER()
    {
        var v = P7F1QProtocol.Validate(Response(Issued.Select(i => Decision(i.Occurrence, "INSUFFICIENT_EVIDENCE", null, missing: "\"NEIGHBOR_TEXT\"")).ToArray()), Issued, Citable);
        Assert.True(v.StrictAccepted);
        Assert.All(v.Rows, r => Assert.Null(r.Decision!.Function));
    }

    [Theory]
    [InlineData("ABSTENTION_WITH_FUNCTION")]
    [InlineData("SUPPORTED_WITH_MISSING_EVIDENCE")]
    [InlineData("EVIDENCE_REF_UNRESOLVED")]
    [InlineData("SUPPORTED_FUNCTION_INVALID")]
    [InlineData("OCCURRENCE_DUPLICATE")]
    [InlineData("DECISION_CARDINALITY_INVALID")]
    [InlineData("OCCURRENCE_NOT_ISSUED")]
    [InlineData("ABSTENTION_MISSING_EVIDENCE_INVALID")]
    public void Contract_violations_are_rejected_and_never_repaired(string expected)
    {
        var first = expected switch
        {
            "ABSTENTION_WITH_FUNCTION" => Decision("O1", "INSUFFICIENT_EVIDENCE", "OTHER", missing: "\"X\""),
            "SUPPORTED_WITH_MISSING_EVIDENCE" => Decision("O1", missing: "\"X\""),
            "EVIDENCE_REF_UNRESOLVED" => Decision("O1", refs: "\"L0099:S0\""),
            "SUPPORTED_FUNCTION_INVALID" => Decision("O1", function: "HEADING"),
            "OCCURRENCE_NOT_ISSUED" => Decision("O7"),
            "ABSTENTION_MISSING_EVIDENCE_INVALID" => Decision("O1", "INSUFFICIENT_EVIDENCE", null),
            _ => Decision("O1"),
        };
        var rest = expected switch
        {
            "OCCURRENCE_DUPLICATE" => new[] { Decision("O1"), Decision("O3") },
            "DECISION_CARDINALITY_INVALID" => new[] { Decision("O2") },
            _ => new[] { Decision("O2"), Decision("O3") },
        };
        var v = P7F1QProtocol.Validate(Response([first, .. rest]), Issued, Citable);
        Assert.False(v.StrictAccepted);
        Assert.Equal(expected, v.FailureCode);
        if (expected == "EVIDENCE_REF_UNRESOLVED") Assert.Equal(["L0099:S0"], v.UnresolvedEvidenceRefs);
        Assert.Contains(v.Rows, r => r.Valid); // diagnostic rows are reported, never adopted as strict success
    }

    [Theory]
    [InlineData("```json\n{\"protocolVersion\":\"P7_F1Q_V1\",\"decisions\":[]}\n```", "RESPONSE_NOT_JSON")]
    [InlineData("[]", "ROOT_NOT_OBJECT")]
    [InlineData("{\"protocolVersion\":\"P7_F1Q_V1\"}", "DECISIONS_MISSING")]
    [InlineData("{\"protocolVersion\":\"V0\",\"decisions\":[]}", "PROTOCOL_VERSION_INVALID")]
    [InlineData("{\"protocolVersion\":\"P7_F1Q_V1\",\"decisions\":[],\"x\":1}", "ROOT_KEYS_INVALID")]
    public void Malformed_envelopes_fail_closed(string response, string code) =>
        Assert.Equal(code, P7F1QProtocol.Validate(response, Issued, Citable).FailureCode);

    [Fact] public void Sse_parser_assembles_streamed_tool_call_arguments_usage_and_reasoning()
    {
        var sse = string.Join("\n\n",
            """data: {"id":"gen-1","provider":"Alibaba","model":"qwen/qwen3.7-flash","choices":[{"delta":{"role":"assistant","content":"","reasoning":"Need "}}]}""",
            """data: {"id":"gen-1","choices":[{"delta":{"reasoning":"context","tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"get_occurrence_context","arguments":"{\"target\":"}}]}}]}""",
            """data: {"id":"gen-1","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"O1\",\"before\":1,\"after\":1}"}},{"index":1,"id":"call_b","function":{"name":"get_source_span","arguments":"{\"targets\":[\"O2\"]}"}}]}}]}""",
            ": OPENROUTER PROCESSING",
            """data: {not json""",
            """data: {"id":"gen-1","choices":[{"delta":{},"finish_reason":"tool_calls","native_finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"cost":0.001,"completion_tokens_details":{"reasoning_tokens":3}}}""",
            "data: [DONE]") + "\n\n";
        var a = P7F1QSse.Parse(sse);
        Assert.True(a.Done);
        Assert.Equal("tool_calls", a.FinishReason);
        Assert.Equal("Need context", a.Reasoning);
        Assert.Equal(2, a.ToolCalls.Count);
        Assert.Equal("""{"target":"O1","before":1,"after":1}""", a.ToolCalls[0].Arguments);
        Assert.Equal("call_b", a.ToolCalls[1].Id);
        Assert.Equal(0.001m, P7F1QSse.Cost(a.Usage));
        Assert.Equal(3, P7F1QSse.ReasoningTokens(a.Usage));
        Assert.Equal("Alibaba", a.Provider);
        Assert.Single(a.MalformedEvents);
    }

    // ---- Conversation loop with a scripted fake transport (no network) ----

    private sealed class ScriptedTransport(params Func<byte[], F1QHttpObservation>[] script) : IF1QTransport
    {
        public List<byte[]> Bodies { get; } = [];
        public Task<F1QHttpObservation> SendAsync(byte[] body, CancellationToken ct)
        {
            Bodies.Add(body);
            return Task.FromResult(script[Math.Min(Bodies.Count - 1, script.Length - 1)](body));
        }
    }

    private static F1QHttpObservation Sse(string deltaJson, string finish, decimal cost = 0.001m) => new(200, Encoding.UTF8.GetBytes(
        $"data: {{\"id\":\"gen-x\",\"provider\":\"Alibaba\",\"choices\":[{{\"delta\":{deltaJson}}}]}}\n\n" +
        $"data: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"{finish}\"}}],\"usage\":{{\"prompt_tokens\":100,\"completion_tokens\":20,\"cost\":{cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}}\n\ndata: [DONE]\n\n"), null, 1, "text/event-stream");
    private static F1QHttpObservation ToolCall(string name, string args) =>
        Sse(JsonSerializer.Serialize(new { tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name, arguments = args } } } }), "tool_calls");
    private static F1QHttpObservation Final(string content) => Sse(JsonSerializer.Serialize(new { content }), "stop");

    private F1QRequestSpec Spec(bool tools = true) => new("case|F1QTools", "case", F1QArm.F1QTools, "sys", "user", Issued, Citable,
        tools ? Tools() : null, null, null);
    private static readonly F1QBodyPolicy Policy = new("qwen/qwen3.7-flash", "alibaba", 32768, false);

    [Fact] public async Task Tool_round_trip_appends_assistant_tool_calls_and_tool_results_then_accepts_cited_evidence()
    {
        var final = Response(Decision("O1", refs: "\"E1.1\",\"L0003:S0\""), Decision("O2"), Decision("O3"));
        var transport = new ScriptedTransport(_ => ToolCall("get_occurrence_context", """{"target":"O1","before":0,"after":3}"""), _ => Final(final));
        var outcome = await new P7F1QConversationRunner(transport, Policy, new()).RunAsync(Spec(), Path.Combine(root, "r1"), () => 0, default);
        Assert.Equal("ACCEPTED", outcome.Status);
        Assert.Equal(2, outcome.Turns.Count);
        Assert.Equal(1, outcome.ToolCallsByName["get_occurrence_context"]);
        var second = JsonNode.Parse(transport.Bodies[1])!;
        var messages = second["messages"]!.AsArray();
        Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[3]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("auto", second["tool_choice"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(root, "r1", "turn-1", "tool-01-E1.1.json")));
        Assert.True(File.Exists(Path.Combine(root, "r1", "raw-freeze.json")));
        Assert.Equal(0.002m, outcome.CostUsd);
    }

    [Fact] public async Task Citing_evidence_not_returned_by_any_tool_is_rejected()
    {
        var transport = new ScriptedTransport(_ => Final(Response(Decision("O1", refs: "\"E1.1\""), Decision("O2"), Decision("O3"))));
        var outcome = await new P7F1QConversationRunner(transport, Policy, new()).RunAsync(Spec(), Path.Combine(root, "r2"), () => 0, default);
        Assert.Equal("CONTRACT_FAILED", outcome.Status);
        Assert.Equal("EVIDENCE_REF_UNRESOLVED", outcome.FailureCode);
        Assert.True(File.Exists(Path.Combine(root, "r2", "response.txt")));
    }

    [Fact] public async Task Tool_cap_forces_tool_choice_none_and_rejects_further_calls()
    {
        var transport = new ScriptedTransport(_ => ToolCall("get_source_span", """{"targets":["O1"]}"""));
        var outcome = await new P7F1QConversationRunner(transport, Policy, new()).RunAsync(Spec(), Path.Combine(root, "r3"), () => 0, default);
        Assert.Equal(4, transport.Bodies.Count);
        Assert.Equal("none", JsonNode.Parse(transport.Bodies[3])!["tool_choice"]!.GetValue<string>());
        Assert.Equal("CONTRACT_FAILED", outcome.Status);
        Assert.Equal("TOOL_CALL_AFTER_TOOL_CAP", outcome.FailureCode);
    }

    [Fact] public async Task Invalid_tool_arguments_are_returned_to_the_model_and_counted()
    {
        var transport = new ScriptedTransport(_ => ToolCall("get_occurrence_context", """{"target":"O99","before":0,"after":0}"""),
            _ => Final(Response(Decision("O1"), Decision("O2"), Decision("O3"))));
        var outcome = await new P7F1QConversationRunner(transport, Policy, new()).RunAsync(Spec(), Path.Combine(root, "r4"), () => 0, default);
        Assert.Equal("ACCEPTED", outcome.Status);
        Assert.Equal(1, outcome.InvalidToolCalls);
        var tool = JsonNode.Parse(transport.Bodies[1])!["messages"]![3]!["content"]!.GetValue<string>();
        Assert.Contains("TARGET_NOT_ISSUED_OR_NOT_IN_DOCUMENT", tool);
    }

    [Fact] public async Task Transport_failure_retries_once_with_identical_body_then_succeeds()
    {
        var transport = new ScriptedTransport(_ => new(502, Encoding.UTF8.GetBytes("bad gateway"), null, 1, "text/plain"),
            _ => Final(Response(Decision("O1"), Decision("O2"), Decision("O3"))));
        var outcome = await new P7F1QConversationRunner(transport, Policy, new()).RunAsync(Spec(false), Path.Combine(root, "r5"), () => 0, default);
        Assert.Equal("ACCEPTED", outcome.Status);
        Assert.Equal(2, outcome.Turns[0].Attempts);
        Assert.Equal(transport.Bodies[0], transport.Bodies[1]);
        Assert.True(File.Exists(Path.Combine(root, "r5", "turn-1", "attempt-1", "response.sse")));
        Assert.False(outcome.AllCostsReported); // the failed 502 carried no usage receipt
    }

    [Fact] public async Task Repeated_transport_failure_stops_without_a_third_attempt()
    {
        var transport = new ScriptedTransport(_ => new(null, [], "TRANSPORT_FAILED:HttpRequestException", 1, null));
        var outcome = await new P7F1QConversationRunner(transport, Policy, new()).RunAsync(Spec(false), Path.Combine(root, "r6"), () => 0, default);
        Assert.Equal("TRANSPORT_FAILED", outcome.Status);
        Assert.Equal(2, transport.Bodies.Count);
        Assert.False(File.Exists(Path.Combine(root, "r6", "response.txt")));
    }

    [Fact] public async Task Incomplete_stream_without_done_is_a_transport_failure_not_a_parse()
    {
        var truncated = new F1QHttpObservation(200, Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"{\"}}]}\n\n"), null, 1, "text/event-stream");
        var outcome = await new P7F1QConversationRunner(new ScriptedTransport(_ => truncated), Policy, new()).RunAsync(Spec(false), Path.Combine(root, "r7"), () => 0, default);
        Assert.Equal("TRANSPORT_FAILED", outcome.Status);
        Assert.Equal("STREAM_INCOMPLETE", outcome.FailureCode);
    }

    [Fact] public async Task Length_finish_is_a_contract_failure_with_raw_preserved()
    {
        var outcome = await new P7F1QConversationRunner(new ScriptedTransport(_ => Sse(JsonSerializer.Serialize(new { content = "{\"protocolVersion\"" }), "length")), Policy, new())
            .RunAsync(Spec(false), Path.Combine(root, "r8"), () => 0, default);
        Assert.Equal("CONTRACT_FAILED", outcome.Status);
        Assert.Equal("FINISH_REASON_LENGTH", outcome.FailureCode);
        Assert.Equal("{\"protocolVersion\"", File.ReadAllText(Path.Combine(root, "r8", "response.txt")));
    }

    [Fact] public async Task Budget_guards_stop_runaway_and_anomalous_requests()
    {
        var runaway = await new P7F1QConversationRunner(new ScriptedTransport(_ => Final("{}")), Policy, new(RunawayUsd: 1m))
            .RunAsync(Spec(false), Path.Combine(root, "r9"), () => 0.9995m, default);
        Assert.Equal(("BUDGET_STOP", "RUNAWAY_COST_GUARD"), (runaway.Status, runaway.FailureCode));
        var anomalous = await new P7F1QConversationRunner(new ScriptedTransport(_ => Sse(JsonSerializer.Serialize(new { content = "{}" }), "stop", 0.9m)), Policy, new())
            .RunAsync(Spec(false), Path.Combine(root, "r10"), () => 0, default);
        Assert.Equal(("BUDGET_STOP", "ANOMALOUS_REQUEST_COST"), (anomalous.Status, anomalous.FailureCode));
    }

    [Fact] public async Task Existing_request_directory_is_never_replayed_or_overwritten()
    {
        Directory.CreateDirectory(Path.Combine(root, "r11"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new P7F1QConversationRunner(new ScriptedTransport(_ => Final("{}")), Policy, new())
            .RunAsync(Spec(false), Path.Combine(root, "r11"), () => 0, default));
    }

    [Fact] public void Tool_arm_body_declares_tools_and_route_without_response_format()
    {
        var runner = new P7F1QConversationRunner(new ScriptedTransport(), Policy, new());
        var body = JsonNode.Parse(runner.Body(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "x" }), true, "auto"))!;
        Assert.Equal(4, body["tools"]!.AsArray().Count);
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
        Assert.Null(body["response_format"]);
        Assert.False(body["provider"]!["allow_fallbacks"]!.GetValue<bool>());
        Assert.Equal("alibaba", body["provider"]!["order"]![0]!.GetValue<string>());
        var noTools = JsonNode.Parse(runner.Body(new JsonArray(), false, "absent"))!;
        Assert.Null(noTools["tools"]); Assert.Null(noTools["tool_choice"]);
    }

    [Fact] public void Frozen_plan_binds_every_initial_body_prompt_and_tool_declaration()
    {
        var dir = TestRepository.Path("artifacts/web-pdf-semantic-diagnostic");
        using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "p7.f1q.execution-plan.v1.json")));
        var r = plan.RootElement;
        Assert.False(r.GetProperty("goldRead").GetBoolean());
        Assert.Equal(0, r.GetProperty("providerCalls").GetInt32());
        Assert.Equal(SpatialCanonical.Hash(Encoding.UTF8.GetBytes(P7F1QEvidenceTools.Definitions().ToJsonString())), r.GetProperty("toolDefinitionsSha256").GetString());
        var requests = r.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(23, requests.Length);
        Assert.Equal(10, r.GetProperty("cases").GetArrayLength());
        foreach (var q in requests)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, "p7.f1q.tool-augmented-raw.v1", "initial-bodies", q.GetProperty("bodyFile").GetString()!));
            Assert.Equal(q.GetProperty("bodySha256").GetString(), SpatialCanonical.Hash(bytes));
            var body = JsonNode.Parse(bytes)!;
            Assert.Equal("qwen/qwen3.7-flash", body["model"]!.GetValue<string>());
            Assert.False(body["provider"]!["allow_fallbacks"]!.GetValue<bool>());
            var arm = Enum.Parse<F1QArm>(q.GetProperty("arm").GetString()!);
            if (arm != F1QArm.Control)
                Assert.Equal(P7F1QProtocol.SystemPrompt(arm), body["messages"]![0]!["content"]!.GetValue<string>());
            Assert.Equal(arm is F1QArm.F1QTools or F1QArm.F1QToolsMandatory, body["tools"] is not null);
            Assert.DoesNotContain("semanticFunction", body["messages"]![1]!["content"]!.GetValue<string>());
        }
    }

    [Fact] public void Frozen_v2_plan_binds_the_mandatory_evidence_arm_to_the_same_ten_cases()
    {
        var dir = TestRepository.Path("artifacts/web-pdf-semantic-diagnostic");
        using var v1 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "p7.f1q.execution-plan.v1.json")));
        using var v2 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "p7.f1q.execution-plan.v2.json")));
        Assert.Equal("P7_F1Q_TOOL_CHAIN_QUALIFICATION_PLAN_V2", v2.RootElement.GetProperty("version").GetString());
        Assert.False(v2.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.True(JsonElement.DeepEquals(v1.RootElement.GetProperty("cases"), v2.RootElement.GetProperty("cases")));
        var requests = v2.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(10, requests.Length);
        foreach (var q in requests)
        {
            Assert.Equal("F1QToolsMandatory", q.GetProperty("arm").GetString());
            var bytes = File.ReadAllBytes(Path.Combine(dir, "p7.f1q.tool-augmented-raw.v1", "initial-bodies-v2", q.GetProperty("bodyFile").GetString()!));
            Assert.Equal(q.GetProperty("bodySha256").GetString(), SpatialCanonical.Hash(bytes));
            var body = JsonNode.Parse(bytes)!;
            Assert.Equal(P7F1QProtocol.SystemPrompt(F1QArm.F1QToolsMandatory), body["messages"]![0]!["content"]!.GetValue<string>());
            Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
            // Same user payload as the V1 tool arm: only the question's evidence requirement differs.
            var v1Body = JsonNode.Parse(File.ReadAllBytes(Path.Combine(dir, "p7.f1q.tool-augmented-raw.v1", "initial-bodies",
                q.GetProperty("bodyFile").GetString()!.Replace("F1QToolsMandatory", "F1QTools"))))!;
            Assert.Equal(v1Body["messages"]![1]!["content"]!.GetValue<string>(), body["messages"]![1]!["content"]!.GetValue<string>());
        }
    }

    [Fact] public void Published_raw_capture_matches_its_freeze_manifest_and_proves_the_tool_chain()
    {
        var dir = TestRepository.Path("artifacts/web-pdf-semantic-diagnostic");
        var root = Path.Combine(dir, "p7.f1q.tool-augmented-raw.v1");
        var manifestBytes = File.ReadAllBytes(Path.Combine(root, "manifest.json"));
        Assert.Equal(manifestBytes, File.ReadAllBytes(Path.Combine(dir, "p7.f1q.raw-capture-freeze.v1.json")));
        using var manifest = JsonDocument.Parse(manifestBytes);
        var m = manifest.RootElement;
        foreach (var f in m.GetProperty("files").EnumerateArray())
            Assert.Equal(f.GetProperty("sha256").GetString(), SpatialCanonical.Hash(File.ReadAllBytes(Path.Combine(root, f.GetProperty("path").GetString()!))));
        Assert.Equal(33, m.GetProperty("requests").GetInt32());
        Assert.Equal(44, m.GetProperty("httpAttempts").GetInt32());
        Assert.Equal(0.072387504m, m.GetProperty("reportedCostUsd").GetDecimal());
        Assert.True(m.GetProperty("allCostsReported").GetBoolean());

        // Canary transcript: Qwen tool_calls -> C# evidence tool results (role=tool, matching ids) -> Qwen final F1.
        var canary = Path.Combine(root, "requests", "D05-PACK_001.F1QToolsMandatory");
        var turn1 = P7F1QSse.Parse(File.ReadAllText(Path.Combine(canary, "turn-1", "attempt-1", "response.sse")));
        Assert.Equal("tool_calls", turn1.FinishReason);
        Assert.Equal("Alibaba", turn1.Provider);
        var turn2Body = JsonNode.Parse(File.ReadAllBytes(Path.Combine(canary, "turn-2", "request.json")))!;
        var toolMessages = turn2Body["messages"]!.AsArray().Where(x => x!["role"]!.GetValue<string>() == "tool").ToArray();
        Assert.Equal(turn1.ToolCalls.Count, toolMessages.Length);
        Assert.Equal(turn1.ToolCalls.Select(c => c.Id), toolMessages.Select(x => x!["tool_call_id"]!.GetValue<string>()));
        foreach (var (call, n) in turn1.ToolCalls.Select((c, i) => (c, i + 1)))
        {
            var record = JsonNode.Parse(File.ReadAllBytes(Directory.GetFiles(Path.Combine(canary, "turn-1"), $"tool-{n:D2}-*.json").Single()))!;
            Assert.Equal(call.Arguments, record["rawArguments"]!.GetValue<string>());
            Assert.Equal("OK", record["status"]!.GetValue<string>());
            var content = record["content"]!.GetValue<string>();
            Assert.Equal(record["contentSha256"]!.GetValue<string>(), SpatialCanonical.Hash(Encoding.UTF8.GetBytes(content)));
            Assert.Equal(content, toolMessages[n - 1]!["content"]!.GetValue<string>());
            Assert.Contains("\"sourceSha256\":\"f427233dcdcd8fc9724c4133c6ef5082bc5105474d4f074442b2c63f76922318\"", content);
        }
        var turn2 = P7F1QSse.Parse(File.ReadAllText(Path.Combine(canary, "turn-2", "attempt-1", "response.sse")));
        Assert.Equal("stop", turn2.FinishReason);
        Assert.Equal(turn2.Content, File.ReadAllText(Path.Combine(canary, "response.txt")));
        Assert.Equal("ACCEPTED", JsonNode.Parse(File.ReadAllBytes(Path.Combine(canary, "request-receipt.json")))!["status"]!.GetValue<string>());
    }

    private F1QRequestSpec MandatorySpec() => new("case|F1QToolsMandatory", "case", F1QArm.F1QToolsMandatory, "sys", "user", Issued, Citable, Tools(), null, null);

    [Fact] public async Task Mandatory_arm_rejects_a_valid_final_answer_given_without_any_tool_evidence()
    {
        var transport = new ScriptedTransport(_ => Final(Response(Decision("O1"), Decision("O2"), Decision("O3"))));
        var outcome = await new P7F1QConversationRunner(transport, Policy, new()).RunAsync(MandatorySpec(), Path.Combine(root, "m1"), () => 0, default);
        Assert.Equal(("CONTRACT_FAILED", "TOOL_EVIDENCE_REQUIRED_NOT_REQUESTED"), (outcome.Status, outcome.FailureCode));
        Assert.True(File.Exists(Path.Combine(root, "m1", "response.txt")));
    }

    [Fact] public async Task Mandatory_arm_accepts_tool_call_then_real_tool_result_then_final_citing_it()
    {
        var transport = new ScriptedTransport(_ => ToolCall("get_repeated_occurrences", """{"target":"O2","maxResults":5}"""),
            _ => Final(Response(Decision("O1"), Decision("O2", function: "REPRESENTS_STRUCTURE", refs: "\"E1.1\",\"L0003:S0\""), Decision("O3"))));
        var outcome = await new P7F1QConversationRunner(transport, Policy, new()).RunAsync(MandatorySpec(), Path.Combine(root, "m2"), () => 0, default);
        Assert.Equal("ACCEPTED", outcome.Status);
        var toolMessage = JsonNode.Parse(transport.Bodies[1])!["messages"]![3]!["content"]!.GetValue<string>();
        Assert.Contains("L0003:S0", toolMessage); // the body heading the contents entry repeats, from the evidence store
    }

    [Fact] public async Task Mandatory_arm_with_only_invalid_tool_calls_does_not_meet_the_requirement()
    {
        var transport = new ScriptedTransport(_ => ToolCall("get_source_span", """{"targets":["O77"]}"""),
            _ => Final(Response(Decision("O1"), Decision("O2"), Decision("O3"))));
        var outcome = await new P7F1QConversationRunner(transport, Policy, new()).RunAsync(MandatorySpec(), Path.Combine(root, "m3"), () => 0, default);
        Assert.Equal("TOOL_EVIDENCE_REQUIRED_NOT_REQUESTED", outcome.FailureCode);
        Assert.Equal(1, outcome.InvalidToolCalls);
    }
    [Fact] public void Prompts_separate_arms_and_never_project_labels()
    {
        var tools = P7F1QProtocol.SystemPrompt(F1QArm.F1QTools); var none = P7F1QProtocol.SystemPrompt(F1QArm.F1QNoTools);
        Assert.Contains("EVIDENCE TOOLS", tools); Assert.DoesNotContain("EVIDENCE TOOLS", none);
        Assert.Contains("must obtain source evidence before deciding", P7F1QProtocol.SystemPrompt(F1QArm.F1QToolsMandatory));
        Assert.DoesNotContain("must obtain source evidence", tools);
        Assert.Contains("INSUFFICIENT_EVIDENCE", none);
        Assert.DoesNotContain("\r", tools);
        Assert.Throws<InvalidOperationException>(() => P7F1QProtocol.SystemPrompt(F1QArm.Control));
    }
}
