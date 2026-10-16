using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.V6Qualification.P7;
using A = DocxHeaderExtractor.V6Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V6Qualification.P7.P7F1V6DirectRepeatCanary;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The production PDF heading agent (DocxHeaderExtractor.HeadingAgent) is a port of the qualified V6 direct agent with the D4.12 method. These
/// tests hold it to the qualified code byte for byte on SRC-089: the source and its hashes, the layout facts, the system prompt, the tool
/// declarations, the first request of every segment (only the prompt and targetFacts differ from the pinned D4.12 reference), the result of every kind
/// of tool call, and whole conversations driven by the same scripted model, with repairs, a fenced answer and a transport retry.
/// </summary>
public sealed class PdfHeadingAgentParityTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    // Pinned in artifacts/v5-p7-f1-v6-d4-contractfix/d412/…trial-plan…cache.v1.json (reference plan of the D4.12 method, code 7763b91).
    private const string PlanPromptSha256 = "3c557778bcb2dd5fa127195d62e31ec9fad4c83bacecc19881cf61f16bbe2845";
    private const string PlanToolDefinitionsSha256 = "17b846fe198c451ea1cc4eee6097ad16a6f82d91107a1a44793cc07537ea9142";
    private const string PlanFirstRequestsSha256 = "3dfeaf6e3c103c2bdaeeca017af13b7977881c64873f3a61e9217de4e5fca20a";
    // Production's system prompt (2026-10-16 facts-in-request revision): pinned so a further change is seen; the request checks below swap it in for the qualified one.
    private const string ProductionPromptSha256 = "7afd4abfdd10de122606477c6f816c57cf791a13500e2bba543aca634b1e6a1d";

    /// <summary>The production request differs only by its system prompt and the existing tool facts added as targetFacts.
    /// Model, tools, provider settings and conversation history must still match byte for byte.</summary>
    private static string WithProductionRequest(string qualifiedBody)
    {
        static string Escaped(string text) =>
            JsonSerializer.Serialize(text, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[1..^1];
        var from = Escaped(A.ToolFactsAnalysisV3PromptRange);
        Assert.Contains(from, qualifiedBody, StringComparison.Ordinal);
        var withPrompt = qualifiedBody.Replace(from, Escaped(AgentProtocol.SystemPrompt), StringComparison.Ordinal);
        var body = JsonNode.Parse(withPrompt)!;
        var originalPayload = body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>();
        var payload = JsonNode.Parse(originalPayload)!;
        var targets = payload["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToArray();
        payload["targetFacts"] = Sources.Value.Production.Flavor.PayloadFacts(Sources.Value.Production, targets);
        var newPayload = payload.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return withPrompt.Replace(Escaped(originalPayload), Escaped(newPayload), StringComparison.Ordinal);
    }

    private static readonly Lazy<(AgentSource Production, P7F1V6Universe Qualified)> Sources = new(() =>
    {
        var path = TestRepository.Path(Pdf);
        var production = AgentSource.Load(path, out var failure) ?? throw new InvalidOperationException(failure);
        var qualified = P7F1V6DirectOwnerTrial.Load(path, P7F1V6DirectOwnerTrial.Sha256Of(path), P7F1V6Universe.LayoutSegmentation, 3);
        return (production, qualified);
    });

    [Fact]
    public void Source_lines_blocks_segments_and_hashes_are_the_qualified_ones()
    {
        var (p, q) = Sources.Value;
        Assert.Equal(q.SourceSha256, p.SourceSha256);
        Assert.Equal(q.UniverseSha256, p.SourceAliasUniverseSha256);
        Assert.Equal(q.EvidenceStoreSha256, p.EvidenceStoreSha256);
        Assert.Equal(q.Tools.AllLines.Select(l => (l.Alias, l.Ordinal, l.Page, l.Text, l.Bbox?.GetRawText(), l.Typography?.GetRawText())),
            p.Lines.Select(l => (l.Alias, l.Ordinal, l.Page, l.Text, l.Bbox?.GetRawText(), l.Typography?.GetRawText())));
        Assert.Equal(q.LayoutBlocks, p.Blocks);
        Assert.Equal(q.Cores, p.Cores);
        for (int n = 0; n < p.Cores.Count; n++)
        {
            var (decide, context) = P7F1V6DirectCanary410.Scope(q, AgentSource.SegmentId(n));
            var (pd, ps) = p.Scope(n);
            Assert.Equal(decide, pd);
            Assert.Equal(context, ps);
        }
    }

    [Fact]
    public void Layout_facts_and_document_styles_are_the_qualified_ones()
    {
        var (p, q) = Sources.Value;
        Assert.Equal(q.LayoutFacts.Count, p.LayoutFacts.Count);
        foreach (var (alias, facts) in q.LayoutFacts)
            Assert.Equal(facts.ToJsonString(), p.LayoutFacts[alias].ToJsonString());
        Assert.Equal(new P7F1V6AgentTools(q.Tools, q.LayoutFacts, factsTool: true, factsVersion: 3).DocumentStyles().ToJsonString(),
            new AgentTools(p).DocumentStyles().ToJsonString());
    }

    [Fact]
    public void Prompt_instruction_and_tool_declarations_are_the_pinned_ones()
    {
        // The qualified prompt is still the reference of the D4.12 plan. Production's prompt is its 2026-10-15 revision (UseLine and
        // FactsToolLine, checked by PromptContractTests), pinned here so that any further change is seen.
        Assert.Equal(PlanPromptSha256, AgentProtocol.Sha(A.ToolFactsAnalysisV3PromptRange));
        Assert.NotEqual(A.ToolFactsAnalysisV3PromptRange, AgentProtocol.SystemPrompt);
        Assert.Equal(ProductionPromptSha256, AgentProtocol.Sha(AgentProtocol.SystemPrompt));
        Assert.Equal(P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines, AgentProtocol.InstructionD412);
        Assert.Equal(P7F1V6AgentTools.Definitions(true).ToJsonString(), AgentTools.Definitions().ToJsonString());
        Assert.Equal(PlanToolDefinitionsSha256, AgentTools.DefinitionsSha256());
    }

    [Fact]
    public void First_requests_differ_from_the_pinned_reference_only_in_the_prompt_and_the_supplied_tool_facts()
    {
        var (p, q) = Sources.Value;
        var styles = new AgentTools(p).DocumentStyles();
        var shas = new List<string>();
        for (int n = 0; n < p.Cores.Count; n++)
        {
            var (decide, shown) = p.Scope(n);
            var mine = AgentProtocol.Build(AgentProtocol.Payload(p.SourceSha256, AgentSource.SegmentId(n), decide, shown.Select(i => p.Lines[i]).ToArray(), styles, AgentProtocol.InstructionD412, targetFacts: p.Flavor.PayloadFacts(p, decide)),
                new JsonArray(), "auto");
            var theirs = C.FirstRequest(A.Arm.ToolFactsAnalysisV3, q, AgentSource.SegmentId(n), jsonObjectAllTurns: true,
                instruction: P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines, cacheMarkers: true);
            Assert.Equal(WithProductionRequest(theirs.Body), mine.Body);
            shas.Add(theirs.Sha256);
        }
        Assert.Equal(PlanFirstRequestsSha256, AgentProtocol.Sha(string.Join("\n", shas)));
    }

    [Fact]
    public void Every_kind_of_tool_call_returns_the_qualified_bytes()
    {
        var (p, q) = Sources.Value;
        var mine = new AgentTools(p);
        var theirs = new P7F1V6AgentTools(q.Tools, q.LayoutFacts, factsTool: true, factsVersion: 3);
        var a = p.Lines;
        string Al(int i) => a[i].Alias;
        var calls = new (string? Name, string Args)[]
        {
            ("get_document_overview", ""),
            ("get_document_overview", "{}"),
            ("get_source_context", $"{{\"alias\":\"{Al(40)}\",\"before\":3,\"after\":6}}"),
            ("get_page_layout", "{\"page\":2,\"offset\":0,\"limit\":40}"),
            ("get_page_layout", "{\"page\":3,\"offset\":10,\"limit\":5}"),
            ("get_source_span", $"{{\"aliases\":[\"{Al(0)}\",\"{Al(1)}\",\"{Al(0)}\"]}}"),
            ("search_document_text", "{\"query\":\"Article\",\"mode\":\"literal\",\"offset\":0,\"limit\":50}"),
            ("search_document_text", "{\"query\":\"chapter i\",\"mode\":\"normalized\",\"offset\":2,\"limit\":5}"),
            ("get_repeated_occurrences", $"{{\"alias\":\"{Al(60)}\",\"maxResults\":20}}"),
            ("compare_source_locations", $"{{\"first\":\"{Al(10)}\",\"second\":\"{Al(300)}\",\"window\":3}}"),
            ("get_line_facts", $"{{\"first\":\"{Al(34)}\",\"last\":\"{Al(67)}\",\"offset\":0}}"),
            ("get_line_facts", $"{{\"first\":\"{Al(34)}\",\"last\":\"{Al(67)}\",\"offset\":24}}"),
            ("get_line_facts", $"{{\"first\":\"{Al(34)}\",\"last\":\"{Al(67)}\",\"offset\":0}}"),   // a repeat: the cache note
            ("get_line_facts", $"{{\"first\":\"{Al(67)}\",\"last\":\"{Al(34)}\",\"offset\":0}}"),   // range invalid
            ("get_source_context", "{\"alias\":\"NOT-AN-ALIAS\",\"before\":1,\"after\":1}"),
            ("get_source_context", "{\"alias\":1}"),
            ("get_page_layout", "{\"page\":9999,\"offset\":0,\"limit\":5}"),
            ("search_document_text", "{\"query\":\"x\",\"mode\":\"literal\",\"offset\":0,\"limit\":5}"),
            ("made_up_tool", "{}"),
            (null, "{}"),
            ("get_page_layout", "not json"),
        };
        for (int i = 0; i < calls.Length; i++)
        {
            var id = $"E1.{i + 1:00}";
            var m = mine.Execute(id, calls[i].Name, calls[i].Args);
            var t = theirs.Execute(id, calls[i].Name, calls[i].Args);
            Assert.True(Encoding.UTF8.GetString(t.Content) == Encoding.UTF8.GetString(m.Content), $"call {i}: {calls[i].Name} {calls[i].Args}");
            Assert.Equal(t.Status, m.Status);
            Assert.Equal(t.ReturnedAliases, m.ReturnedAliases);
        }
        Assert.Equal(theirs.ObservedAliases.Order(StringComparer.Ordinal), mine.ObservedAliases.Order(StringComparer.Ordinal));
        foreach (var code in new[] { "PER_TURN_CALL_CAP_EXCEEDED", "UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED" })
            Assert.Equal(theirs.Reject("E2.01", "get_line_facts", "{}", code).Content, mine.Reject("E2.01", "get_line_facts", "{}", code).Content);
        Assert.Equal(theirs.RejectionBytes("E3.08", "search_document_text"), mine.RejectionBytes("E3.08", "search_document_text"));
    }

    // ---------------- whole conversations ----------------

    /// <summary>One scripted reply: content, tool calls (id, name, arguments), finish reason; or a transport failure code to throw.</summary>
    private sealed record Step(string? Content, (string Id, string Name, string Args)[] Calls, string Finish, string? Throw = null);

    private delegate Step Script(int post, JsonElement payload, string body);

    private static JsonElement PayloadOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
        return JsonDocument.Parse(P7F1V6Protocol.MessageText(content)).RootElement.Clone();
    }

    private static string Answer(JsonElement payload, bool skipLast = false, bool fenced = false)
    {
        var targets = payload.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("alias").GetString()!).ToList();
        if (skipLast) targets.RemoveAt(targets.Count - 1);
        var decisions = targets.Select((alias, i) => i == 0
            ? (object)new { alias, decision = "SELECT", evidenceRefs = new[] { alias }, reason = "the first line" }
            : new { alias, decision = "NOT_SELECT" }).ToArray();
        var json = JsonSerializer.Serialize(new { protocolVersion = "P7_F1_V6_SELECT_OBJECTS_V1", status = "COMPLETE", analysis = "scripted", decisions });
        return fenced ? "```json\n" + json + "\n```" : json;
    }

    private static Step Facts(JsonElement payload, string id) =>
        new(null, [(id, "get_line_facts", $"{{\"first\":\"{payload.GetProperty("targetFirstAlias").GetString()}\",\"last\":\"{payload.GetProperty("targetLastAlias").GetString()}\",\"offset\":0}}")], "tool_calls");

    public static IEnumerable<object[]> Scenarios() => new[] { "facts-then-answer", "answer-first-then-repair", "coverage-repair", "fenced", "transport-retry", "tool-after-cap", "facts-paged" }.Select(s => new object[] { s });

    private static Script ScriptFor(string scenario) => scenario switch
    {
        "facts-then-answer" => (post, payload, _) => post == 1 ? Facts(payload, "c1") : new(Answer(payload), [], "stop"),
        "answer-first-then-repair" => (post, payload, _) => post == 1 ? new(Answer(payload), [], "stop") : post == 2 ? Facts(payload, "c1") : new(Answer(payload), [], "stop"),
        "coverage-repair" => (post, payload, _) => post == 1 ? Facts(payload, "c1") : post == 2 ? new(Answer(payload, skipLast: true), [], "stop") : new(Answer(payload), [], "stop"),
        "fenced" => (post, payload, _) => post == 1 ? Facts(payload, "c1") : new(Answer(payload, fenced: true), [], "stop"),
        "transport-retry" => (post, payload, _) => post == 1 ? new(null, [], "", Throw: "TRANSPORT:STREAM_ERROR") : post == 2 ? Facts(payload, "c1") : new(Answer(payload), [], "stop"),
        "tool-after-cap" => (post, payload, _) => post <= 3 ? Facts(payload, "c" + post) : post == 4 ? Facts(payload, "c4") : new(Answer(payload), [], "stop"),
        "facts-paged" => (post, payload, _) => post == 1
            ? new(null, [("a", "get_line_facts", $"{{\"first\":\"{payload.GetProperty("targetFirstAlias").GetString()}\",\"last\":\"{payload.GetProperty("targetLastAlias").GetString()}\",\"offset\":0}}"),
                ("b", "get_line_facts", $"{{\"first\":\"{payload.GetProperty("targetFirstAlias").GetString()}\",\"last\":\"{payload.GetProperty("targetLastAlias").GetString()}\",\"offset\":24}}"),
                ("c", "get_document_overview", "")], "tool_calls")
            : new(Answer(payload), [], "stop"),
        _ => throw new ArgumentException(scenario),
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void A_scripted_conversation_sends_the_qualified_requests_and_ends_with_the_qualified_outcome(string scenario)
    {
        var (p, q) = Sources.Value;
        const int segment = 1;
        var script = ScriptFor(scenario);

        var theirPosts = 0;
        var (decide, context) = P7F1V6DirectCanary410.Scope(q, AgentSource.SegmentId(segment));
        var theirs = A.RunUnit(A.Arm.ToolFactsAnalysisV3, P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines, q, AgentSource.SegmentId(segment), decide, context,
            (unit, turn, req) =>
            {
                var step = script(++theirPosts, PayloadOf(req.Body), req.Body);
                if (step.Throw is not null) throw new P7F1V6DirectV2.ReplyFailure(step.Throw);
                return new A.ModelTurn(step.Content, step.Calls.Select(c => new A.ToolCall(c.Id, c.Name, c.Args)).ToArray(), step.Finish);
            },
            new P7F1V6DirectV2.Budget(1000, 100, 131072), rangeStated: true, jsonObjectAllTurns: true, unwrapSingleFence: true,
            transportRetries: 3, repairTurn: true, retryDelayMs: 0, maxRepairs: -1, cacheMarkers: true);

        var myPosts = 0;
        var mine = AgentUnit.Run(p, segment, (unit, turn, req) =>
        {
            var step = script(++myPosts, PayloadOf(req.Body), req.Body);
            if (step.Throw is not null) throw new ReplyFailure(step.Throw);
            return new AgentProtocol.ModelTurn(step.Content, step.Calls.Select(c => new AgentProtocol.ToolCall(c.Id, c.Name, c.Args)).ToArray(), step.Finish);
        }, new AgentBudget(1000, 100), retryDelayMs: 0, instruction: AgentProtocol.InstructionD412);

        if (scenario == "answer-first-then-repair")
        {
            // The intentional change: facts are already present, so production accepts the first answer without the old facts repair.
            Assert.Equal(3, theirs.Unit.Requests.Count);
            Assert.Equal(new[] { AgentProtocol.Sha(WithProductionRequest(theirs.Unit.Requests[0].Body)) }, mine.RequestSha256s);
            Assert.Equal("COMPLETE", mine.Status);
            Assert.Null(mine.RepairedFrom);
            Assert.Equal(0, mine.RepairsUsed);
            Assert.Equal(0, mine.ToolCallsSucceeded);
            Assert.Empty(mine.TargetsWithoutFacts!);
            Assert.Equal(theirs.Unit.Decisions.OrderBy(d => d.Key, StringComparer.Ordinal), mine.Decisions.OrderBy(d => d.Key, StringComparer.Ordinal));
            return;
        }
        Assert.Equal(theirs.Unit.Requests.Select(r => AgentProtocol.Sha(WithProductionRequest(r.Body))), mine.RequestSha256s);
        Assert.Equal(theirs.Unit.Status, mine.Status);
        Assert.Equal(theirs.Unit.Failure, mine.Failure);
        Assert.Equal(theirs.Unit.Decisions.OrderBy(d => d.Key, StringComparer.Ordinal), mine.Decisions.OrderBy(d => d.Key, StringComparer.Ordinal));
        Assert.Equal(theirs.RepairedFrom, mine.RepairedFrom);
        Assert.Equal(theirs.RepairsUsed, mine.RepairsUsed);
        Assert.Equal(theirs.TransportRetries, mine.TransportRetries);
        Assert.Equal(theirs.FenceUnwrapped, mine.FenceUnwrapped);
        Assert.Equal(theirs.Analysis, mine.Analysis);
        Assert.Equal(theirs.SuccessfulToolCalls, mine.ToolCallsSucceeded);
        Assert.Equal("COMPLETE", mine.Status);
        Assert.Equal("SELECT", mine.Decisions[decide[0]]);
    }
}
