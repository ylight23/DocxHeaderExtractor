using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Deterministic, non-provider tests for P7-F1Q plan V3 (protocol P7_F1Q_V2, tool set V2, claim audit).</summary>
public sealed class P7F1QEvidenceV2Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "p7f1q-v2-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static JsonElement Box(double l, double r, double b, double t) => JsonSerializer.SerializeToElement(new { left = l, right = r, bottom = b, top = t });
    private static JsonElement Font(double size, double bold) => JsonSerializer.SerializeToElement(new { fontSize = size, fontName = "times", boldRatio = bold, italicRatio = 0.0 });

    // An invitation-like page: letterhead left, national header right, centred title + subtitle, a 3-column table row, footer.
    private static readonly F1QSourceLine[] Lines =
    [
        new("L0000:S0", 0, 1, "MINISTRY OF SCIENCE", Box(85, 262, 774, 784), Font(12, 1)),
        new("L0000:S1", 1, 1, "SOCIALIST REPUBLIC", Box(289, 538, 774, 784), Font(12, 1)),
        new("L0003:S0", 2, 1, "INVITATION", Box(278, 346, 674, 684), Font(14, 1)),
        new("L0004:S0", 3, 1, "Attend the national conference on", Box(97, 526, 658, 668), Font(14, 1)),
        new("L0005:S0", 4, 1, "Body sentence of the letter.", Box(85, 538, 600, 610), Font(13, 0)),
        new("L0040:S0", 5, 1, "Time", Box(87, 140, 400, 410), Font(13, 1)),
        new("L0040:S1", 6, 1, "Content", Box(245, 294, 400, 410), Font(13, 1)),
        new("L0040:S2", 7, 1, "Owner", Box(433, 488, 400, 410), Font(13, 1)),
        new("L0090:S0", 8, 1, "Report Title Page 1", Box(280, 330, 83, 93), Font(9, 0)),
        new("L0091:S0", 9, 2, "Report Title Page 2", Box(280, 330, 83, 93), Font(9, 0)),
    ];
    private static readonly Dictionary<string, string> Issued = new() { ["O1"] = "L0000:S0", ["O5"] = "L0003:S0", ["O6"] = "L0004:S0", ["O45"] = "L0040:S0" };
    private static P7F1QEvidenceTools Tools(int set = 2) => new("src", "u", "st", Lines, Issued, set);
    private static JsonElement Result(F1QToolResult r) => JsonDocument.Parse(r.Content).RootElement;

    [Fact] public void Context_v2_returns_layout_spatial_relations_and_row_peers()
    {
        var r = Tools().Execute("E1.1", "get_occurrence_context", """{"target":"O6","before":1,"after":1}""");
        Assert.Equal("OK", r.Status);
        var result = Result(r).GetProperty("result");
        var lines = result.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(14, lines[0].GetProperty("layout").GetProperty("font").GetProperty("size").GetDouble());
        Assert.Equal(16, lines[0].GetProperty("relationToTarget").GetProperty("verticalOffset").GetDouble()); // title is 16pt above
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("relationToTarget").ValueKind); // the target itself
        var peers = Tools().Execute("E1.2", "get_occurrence_context", """{"target":"O45","before":0,"after":0}""");
        Assert.Equal(["L0040:S0", "L0040:S1", "L0040:S2"], peers.ReturnedAliases.Order().ToArray());
        Assert.Equal(2, Result(peers).GetProperty("result").GetProperty("sameBaselinePeers").GetArrayLength());
    }

    [Fact] public void Compare_returns_both_locations_and_arithmetic_deltas_only_in_tool_set_v2()
    {
        var r = Tools().Execute("E1.1", "compare_occurrences", """{"first":"L0090:S0","second":"L0091:S0","window":1}""");
        var delta = Result(r).GetProperty("result").GetProperty("delta");
        Assert.True(delta.GetProperty("sameRepeatKey").GetBoolean());
        Assert.Equal(1, delta.GetProperty("pageDistance").GetInt32());
        Assert.Equal("INVALID_ARGUMENT", Tools().Execute("E1.1", "compare_occurrences", """{"first":"O1","second":"O1","window":1}""").Status);
        Assert.Equal("UNKNOWN_TOOL", Tools(1).Execute("E1.1", "compare_occurrences", """{"first":"O1","second":"O5","window":1}""").ErrorCode);
        Assert.Equal(5, P7F1QEvidenceTools.DefinitionsV2().Count);
        Assert.Equal(4, P7F1QEvidenceTools.Definitions().Count);
    }

    [Fact] public void Tool_set_v1_results_are_unchanged_by_v2()
    {
        var r = Tools(1).Execute("E1.1", "get_occurrence_context", """{"target":"O6","before":1,"after":1}""");
        Assert.False(Result(r).GetProperty("result").GetProperty("lines")[0].TryGetProperty("layout", out _));
        Assert.Equal(P7F1QEvidenceTools.Version, Result(r).GetProperty("version").GetString());
    }

    private static readonly F1QIssuedOccurrence[] Subjects = [new("O1", "L0000:S0", 1, "MINISTRY OF SCIENCE"), new("O6", "L0004:S0", 1, "Attend the national conference on")];
    private static readonly HashSet<string> Citable = ["L0000:S0", "L0000:S1", "L0003:S0", "L0004:S0", "L0005:S0"];
    private static string D(string id, string alias, string refs = "", string fn = "\"OTHER\"", string assessment = "SUPPORTED", string missing = "") =>
        $$"""{"occurrence":"{{id}}","sourceAlias":"{{alias}}","assessment":"{{assessment}}","function":{{fn}},"observedRole":"r","evidenceRefs":[{{(refs.Length == 0 ? $"\"{alias}\"" : refs)}}],"interpretation":"i","missingEvidence":[{{missing}}]}""";
    private static string Resp(params string[] d) => $$"""{"protocolVersion":"P7_F1Q_V2","decisions":[{{string.Join(",", d)}}]}""";
    private static F1QValidation V(string response, Dictionary<string, IReadOnlyCollection<string>>? coverage = null, HashSet<string>? citable = null) =>
        P7F1QProtocolV2.Validate(response, Subjects, citable ?? Citable, coverage ?? []);

    [Fact] public void V2_accepts_identity_bound_subject_relevant_decisions()
    {
        var v = V(Resp(D("O1", "L0000:S0"), D("O6", "L0004:S0", "\"E1.1\"", "\"ESTABLISHES_STRUCTURE\"")),
            new() { ["E1.1"] = ["L0003:S0", "L0004:S0"] }, [.. Citable, "E1.1"]);
        Assert.True(v.StrictAccepted, v.FailureCode);
    }

    [Theory]
    [InlineData("SOURCE_ALIAS_MISMATCH")]
    [InlineData("EVIDENCE_NOT_ABOUT_SUBJECT")]
    [InlineData("SUPPORTED_FUNCTION_INVALID")]
    [InlineData("DECISION_KEYS_INVALID")]
    public void V2_rejects_identity_shift_irrelevant_evidence_and_null_function(string code)
    {
        var first = code switch
        {
            "SOURCE_ALIAS_MISMATCH" => D("O1", "L0000:S1", "\"L0000:S0\""), // neighbour's alias under O1
            "EVIDENCE_NOT_ABOUT_SUBJECT" => D("O1", "L0000:S0", "\"L0003:S0\""), // cites only another line
            "SUPPORTED_FUNCTION_INVALID" => D("O1", "L0000:S0", fn: "null"),
            _ => """{"occurrence":"O1","assessment":"SUPPORTED","function":"OTHER","observedRole":"r","evidenceRefs":["L0000:S0"],"interpretation":"i","missingEvidence":[]}""",
        };
        var v = V(Resp(first, D("O6", "L0004:S0")));
        Assert.False(v.StrictAccepted);
        Assert.Equal(code, v.FailureCode);
    }

    [Fact] public void V2_evidence_id_must_cover_the_subject_to_count_as_relevant()
    {
        var v = V(Resp(D("O1", "L0000:S0", "\"E1.1\""), D("O6", "L0004:S0")), new() { ["E1.1"] = ["L0003:S0"] }, [.. Citable, "E1.1"]);
        Assert.Equal("EVIDENCE_NOT_ABOUT_SUBJECT", v.FailureCode);
    }

    [Fact] public void V2_abstention_is_valid_and_not_other()
    {
        var v = V(Resp(D("O1", "L0000:S0"), D("O6", "L0004:S0", fn: "null", assessment: "INSUFFICIENT_EVIDENCE", missing: "\"PAGE_IMAGE\"")));
        Assert.True(v.StrictAccepted, v.FailureCode);
        Assert.Null(v.Rows[1].Decision!.Function);
    }

    [Theory]
    [InlineData(79, new[] { 40, 39 })]
    [InlineData(96, new[] { 48, 48 })]
    [InlineData(91, new[] { 46, 45 })]
    [InlineData(30, new[] { 30 })]
    [InlineData(48, new[] { 48 })]
    public void Packs_split_into_balanced_contiguous_chunks_keeping_original_ids(int n, int[] sizes)
    {
        var issued = Enumerable.Range(1, n).Select(i => new P7F1QProtocolV2.V5Issued($"O{i}", $"L{i:D4}:S0", 1, "t", JsonSerializer.SerializeToElement(Array.Empty<object>()))).ToArray();
        var chunks = P7F1QProtocolV2.Chunks(issued);
        Assert.Equal(sizes, chunks.Select(c => c.Count).ToArray());
        Assert.Equal(issued.Select(i => i.Occurrence), chunks.SelectMany(c => c).Select(i => i.Occurrence));
        Assert.All(chunks, c => Assert.True(c.Count <= P7F1QProtocolV2.ChunkSize));
    }

    [Fact] public void V2_user_message_issues_only_the_chunk_and_projects_layout_without_labels()
    {
        var all = new[] { "O1", "O5", "O6", "O45" }.Select(id => new P7F1QProtocolV2.V5Issued(id, Issued[id], 1, Lines.Single(l => l.Alias == Issued[id]).Text, JsonSerializer.SerializeToElement(Array.Empty<object>()))).ToArray();
        var user = JsonNode.Parse(P7F1QProtocolV2.UserMessage(all[..2], all, [("L0005:S0", 1, "Body sentence of the letter.")], Tools()))!;
        Assert.Equal(["O1", "O5"], user["issuedOccurrences"]!.AsArray().Select(o => o!["id"]!.GetValue<string>()));
        var context = user["contextOnlyEvidence"]!.AsArray();
        Assert.Equal(3, context.Count);
        Assert.All(context, c => Assert.Null(c!["id"]));
        Assert.Equal(1, user["issuedOccurrences"]![0]!["layout"]!["font"]!["boldRatio"]!.GetValue<double>());
        var text = user.ToJsonString();
        Assert.DoesNotContain("semanticFunction", text); Assert.DoesNotContain("ESTABLISHES", text);
    }

    [Fact] public void V2_prompt_carries_the_evidence_grounded_judgment_and_identity_contract()
    {
        var prompt = P7F1QProtocolV2.SystemPrompt();
        Assert.Contains("EVIDENCE-GROUNDED SEMANTIC JUDGMENT", prompt);
        Assert.Contains("A valid source identifier is not by itself proof", prompt);
        Assert.Contains("return INSUFFICIENT_EVIDENCE, not OTHER", prompt);
        Assert.Contains("\"protocolVersion\":\"P7_F1Q_V2\"", prompt);
        Assert.Contains("compare_occurrences", prompt);
        Assert.DoesNotContain("\r", prompt);
    }

    [Theory]
    [InlineData("L0000:S0", "Top-center bold letterhead", "CONTRADICTED", "centered")]
    [InlineData("L0000:S0", "Letterhead on the left side, bold uppercase", "LAYOUT_CLAIMS_CONSISTENT", "left side")]
    [InlineData("L0003:S0", "Centered bold title at the top of the page", "LAYOUT_CLAIMS_CONSISTENT", "centered")]
    [InlineData("L0005:S0", "Bold heading line", "CONTRADICTED", "bold")]
    [InlineData("L0040:S0", "Column label of the schedule table", "LAYOUT_CLAIMS_CONSISTENT", "column label (shares a row)")]
    [InlineData("L0004:S0", "Column header of a table", "CONTRADICTED", "column label (shares a row)")]
    [InlineData("L0090:S0", "Running footer with page number", "LAYOUT_CLAIMS_CONSISTENT", "running/page footer")]
    [InlineData("L0003:S0", "Running header", "CONTRADICTED", "running/page header")]
    [InlineData("L0005:S0", "Narrative sentence of the letter", "NO_CHECKABLE_LAYOUT_CLAIM", null)]
    public void Claim_audit_checks_layout_claims_against_the_subject_own_observations(string alias, string role, string status, string? claim)
    {
        var audit = P7F1QClaimAudit.Check(role, "short interpretation", alias, Tools());
        Assert.Equal(status, audit.Status);
        if (claim is not null) Assert.Contains(audit.Checks, c => c.Claim == claim);
    }

    [Fact] public void Frozen_v3_plan_binds_chunked_bodies_prompt_tools_and_retry_policy()
    {
        var dir = TestRepository.Path("artifacts/web-pdf-semantic-diagnostic");
        using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "p7.f1q.execution-plan.v3.json")));
        var r = plan.RootElement;
        Assert.Equal("P7_F1Q_EVIDENCE_GROUNDED_PLAN_V3", r.GetProperty("version").GetString());
        Assert.False(r.GetProperty("goldRead").GetBoolean());
        Assert.StartsWith("AT_MOST_ONE_IDENTICAL_INITIAL_BODY_RETRY_PER_REQUEST", r.GetProperty("retryPolicy").GetString());
        Assert.Equal(SpatialCanonical.Hash(Encoding.UTF8.GetBytes(P7F1QEvidenceTools.DefinitionsV2().ToJsonString())), r.GetProperty("toolDefinitionsSha256").GetString());
        var requests = r.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(20, requests.Length);
        foreach (var caseGroup in requests.GroupBy(q => q.GetProperty("case").GetString()))
        {
            var issued = caseGroup.SelectMany(q => q.GetProperty("issued").EnumerateArray().Select(i => i.GetProperty("occurrence").GetString())).ToArray();
            Assert.Equal(issued.Length, issued.Distinct().Count()); // chunks partition the pack
            var expected = r.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("case").GetString() == caseGroup.Key).GetProperty("issued").GetInt32();
            Assert.Equal(expected, issued.Length);
        }
        foreach (var q in requests)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, "p7.f1q.evidence-v2-raw.v1", "initial-bodies", q.GetProperty("bodyFile").GetString()!));
            Assert.Equal(q.GetProperty("bodySha256").GetString(), SpatialCanonical.Hash(bytes));
            var body = JsonNode.Parse(bytes)!;
            Assert.Equal(P7F1QProtocolV2.SystemPrompt(), body["messages"]![0]!["content"]!.GetValue<string>());
            Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
            Assert.Equal(5, body["tools"]!.AsArray().Count);
            Assert.InRange(q.GetProperty("issued").GetArrayLength(), 1, P7F1QProtocolV2.ChunkSize);
        }
    }

    // ---- runner with V2 validator, using scripted (non-provider) SSE ----
    private sealed class Scripted(params Func<byte[], F1QHttpObservation>[] script) : IF1QTransport
    {
        public List<byte[]> Bodies { get; } = [];
        public Task<F1QHttpObservation> SendAsync(byte[] body, CancellationToken ct) { Bodies.Add(body); return Task.FromResult(script[Math.Min(Bodies.Count - 1, script.Length - 1)](body)); }
    }
    private static F1QHttpObservation Sse(object delta, string finish) => new(200, Encoding.UTF8.GetBytes(
        $"data: {{\"provider\":\"Alibaba\",\"choices\":[{{\"delta\":{JsonSerializer.Serialize(delta)}}}]}}\n\ndata: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"{finish}\"}}],\"usage\":{{\"prompt_tokens\":1,\"completion_tokens\":1,\"cost\":0.0001}}}}\n\ndata: [DONE]\n\n"), null, 1, "text/event-stream");

    [Fact] public async Task V2_runner_uses_tool_set_v2_json_object_and_subject_coverage_of_evidence_ids()
    {
        var t = new Scripted(
            _ => Sse(new { tool_calls = new[] { new { index = 0, id = "c1", type = "function", function = new { name = "compare_occurrences", arguments = """{"first":"O5","second":"O6","window":0}""" } } } }, "tool_calls"),
            _ => Sse(new { content = Resp(D("O1", "L0000:S0"), D("O6", "L0004:S0", "\"E1.1\"", "\"ESTABLISHES_STRUCTURE\"")) }, "stop"));
        var policy = new F1QBodyPolicy("qwen/qwen3.7-flash", "alibaba", 32768, JsonObjectResponse: true, ToolSet: 2);
        var spec = new F1QRequestSpec("x|F1QEvidenceV2", "x", F1QArm.F1QEvidenceV2, "sys", "user", Subjects, Citable, Tools(), null, null);
        var outcome = await new P7F1QConversationRunner(t, policy, new(MaxTransportAttemptsPerTurn: 1)).RunAsync(spec, Path.Combine(root, "v2"), () => 0, default);
        Assert.Equal("ACCEPTED", outcome.Status);
        var first = JsonNode.Parse(t.Bodies[0])!;
        Assert.Equal("json_object", first["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal(5, first["tools"]!.AsArray().Count);
    }

    [Fact] public async Task V2_runner_has_no_in_turn_transport_retry_when_capped_to_one_attempt()
    {
        var t = new Scripted(_ => new(502, Encoding.UTF8.GetBytes("bad"), null, 1, "text/plain"));
        var spec = new F1QRequestSpec("x|F1QEvidenceV2", "x", F1QArm.F1QEvidenceV2, "sys", "user", Subjects, Citable, Tools(), null, null);
        var outcome = await new P7F1QConversationRunner(t, new("m", "alibaba", 10, true, 2), new(MaxTransportAttemptsPerTurn: 1)).RunAsync(spec, Path.Combine(root, "v2t"), () => 0, default);
        Assert.Equal("TRANSPORT_FAILED", outcome.Status);
        Assert.Single(t.Bodies);
    }
}
