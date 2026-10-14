using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D2.1 (offline): hierarchical reconciliation with scripted models on pages 1–2 of the real RFC 094.</summary>
public sealed class P7F1V6HierTests
{
    const string Rfc = "todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf";
    const string Instr = "Trích xuất các heading chính";
    const string Proto = "P7_F1_V6_HIER_NODE_V3";
    const int HonestPassUp = 4;   // PassUpCap 8 less two boundary windows of Context 2
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static IReadOnlyList<P7F1V6Hier.Cand> Cands()
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(Rfc));
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var legacy = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        return legacy.AllLines.Where(l => l.Page is 1 or 2).Select(l => new P7F1V6Hier.Cand(l.Alias, l.Page!.Value, l.Text, P7F1QEvidenceTools.CompactLayout(l.Bbox, l.Typography))).ToArray();
    }

    static P7F1V6Hier.Options Opts(string instr = Instr) => new(instr, "src-sha", ShardSize: 6, Context: 2, FanIn: 3, PassUpCap: 8, SourceUniverseSha256: "universe-sha", ParserVersion: "parser-v1", EvidenceStoreVersion: "store-v1");

    static bool Even(string alias) => int.Parse(alias[1..5]) % 2 == 0;

    static JsonElement Root(string payload) => JsonDocument.Parse(payload).RootElement;

    static string Quoted(IEnumerable<string> ids) => string.Join(",", ids.Select(i => $"\"{i}\""));

    static string Node(string nodeId, string status, string items = "", string passUp = "", string confirmed = "") =>
        $"{{\"protocolVersion\":\"{Proto}\",\"nodeId\":\"{nodeId}\",\"status\":\"{status}\",\"items\":[{items}]"
        + (passUp == "" ? "" : $",\"passUp\":[{passUp}]")
        + (confirmed == "" ? "" : $",\"confirmed\":[{confirmed}]")
        // a reply that does not complete must give a reason, and a CLARIFICATION_REQUIRED reply an open question
        + (status is "COMPLETE" or "RETRIEVE" ? "" : ",\"reason\":\"test reason\"" + (status == "CLARIFICATION_REQUIRED" ? ",\"openQuestions\":[\"test question\"]" : ""))
        + "}";

    static string RetrieveJson(string nodeId, string? search = null, string? reopen = null)
    {
        object rt = search is not null ? new { search } : new { reopen = new[] { reopen! } };
        return JsonSerializer.Serialize(new { protocolVersion = Proto, nodeId, status = "RETRIEVE", retrieve = rt }, Json);
    }

    static IEnumerable<string> ShownIds(string payload) =>
        Root(payload).GetProperty("shown").EnumerateArray().Select(s => s.GetProperty("id").GetString()!);

    static IEnumerable<string> OwnedIds(string payload) =>
        Root(payload).GetProperty("owned").EnumerateArray().Select(s => s.GetProperty("id").GetString()!);

    /// <summary>Local nodes decide every owned candidate (SELECT when the alias index is even). Reconcile and synthesis nodes confirm
    /// every id they were shown, and pass up the first few selections. Global confirms everything it was shown.</summary>
    static string Honest(string nodeId, P7F1V6Hier.Kind kind, string payload, Func<string, string>? localRule = null)
    {
        if (kind == P7F1V6Hier.Kind.Interpretation) return Interp(payload);
        if (kind == P7F1V6Hier.Kind.Local)
        {
            var items = Root(payload).GetProperty("owned").EnumerateArray().Select(o =>
            {
                var a = o.GetProperty("sourceAlias").GetString()!;
                var d = localRule?.Invoke(a) ?? (Even(a) ? "SELECT" : "NOT_SELECT");
                return $"{{\"id\":\"{o.GetProperty("id").GetString()}\",\"decision\":\"{d}\",\"evidenceRefs\":[\"{a}\"]}}";
            });
            return Node(nodeId, "COMPLETE", string.Join(",", items));
        }
        if (kind == P7F1V6Hier.Kind.Reconcile) return Node(nodeId, "COMPLETE", confirmed: Quoted(OwnedIds(payload)));
        if (kind == P7F1V6Hier.Kind.Synthesis)
        {
            var up = Root(payload).GetProperty("shown").EnumerateArray()
                .Where(s => s.GetProperty("current").GetString() == "SELECT").Take(HonestPassUp).Select(s => s.GetProperty("id").GetString()!);
            return Node(nodeId, "COMPLETE", passUp: Quoted(up), confirmed: Quoted(ShownIds(payload)));
        }
        return Node(nodeId, "COMPLETE", confirmed: Quoted(ShownIds(payload)));
    }

    /// <summary>The scripted interpretation: a COMPLETE reply that restates the instruction it received, through the D1 contract.</summary>
    static string Interp(string payload) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = "P7_F1_V6_INTERPRET_V1", requestId = "INTERPRET", status = "COMPLETE",
            interpretation = "Task: " + Root(payload).GetProperty("originalUserInstruction").GetString(),
        }, Json);

    static string Id(string payload, string alias) =>
        Root(payload).GetProperty("shown").EnumerateArray().First(s => s.GetProperty("sourceAlias").GetString() == alias).GetProperty("id").GetString()!;

    static string Others(string payload, string except) => Quoted(ShownIds(payload).Where(i => i != except));

    // ---------------- structure, provenance and coverage ----------------

    [Fact] public void A_full_hierarchical_run_gives_every_candidate_an_explicit_final_state_with_provenance()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (a, b, c) => Honest(a, b, c));
        Assert.Empty(run.Violations);
        Assert.Equal("PROTOCOL_RESOLVED", run.RunStatus);
        Assert.True(run.GlobalAccepted);
        Assert.Equal(c.Count, run.Final.Count);
        Assert.All(run.Final.Values, s => Assert.NotEqual("MISSING", s.Decision));
        Assert.Contains(run.Nodes, n => n.Kind == "Synthesis");
        Assert.Equal("GLOBAL", run.Nodes[^1].NodeId);
        Assert.True(run.Raw.Verify());
        Assert.All(run.Nodes, n => Assert.True(run.Raw.Items.ContainsKey(n.InputSha256)));
    }

    [Fact] public void A_heading_split_across_two_shards_is_joined_by_a_synthesis_node_that_names_its_continuation()
    {
        var c = Cands();
        // Shard 0 owns c[0..5], shard 1 owns c[6..11]. a = last of shard 0 (SELECT), b = first of shard 1 (NOT_SELECT).
        var a = c[5].Alias; var b = c[6].Alias;
        Assert.True(Even(a) && !Even(b), "fixture needs a SELECT at the boundary and a NOT_SELECT just after it");
        string? aId = null, bId = null;
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (kind == P7F1V6Hier.Kind.Synthesis && payload.Contains(b, StringComparison.Ordinal) && payload.Contains(a, StringComparison.Ordinal) && aId is null)
            {
                aId = Id(payload, a); bId = Id(payload, b);
                return Node(nodeId, "COMPLETE",
                    $"{{\"id\":\"{bId}\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"{b}\"],\"continuesFromId\":\"{aId}\"}}",
                    confirmed: Others(payload, bId));
            }
            return Honest(nodeId, kind, payload);
        });
        Assert.Empty(run.Violations);
        Assert.NotNull(bId);
        var sb = run.Final[bId!];
        Assert.Equal("SELECT", sb.Decision);
        Assert.Equal(aId, sb.ContinuesFromId);
        Assert.StartsWith("L1", sb.SourceNode, StringComparison.Ordinal);
        Assert.True(run.Raw.Items.ContainsKey(sb.SourceOutputSha256));
    }

    [Fact] public void A_parent_reference_to_an_id_that_was_not_shown_is_rejected_and_changes_nothing()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (kind == P7F1V6Hier.Kind.Synthesis && nodeId == "L1-N0001")
            {
                var first = Root(payload).GetProperty("shown")[0];
                return Node(nodeId, "COMPLETE",
                    $"{{\"id\":\"{first.GetProperty("id").GetString()}\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"{first.GetProperty("sourceAlias").GetString()}\"],\"parentId\":\"zzzz\"}}",
                    confirmed: Others(payload, first.GetProperty("id").GetString()!));
            }
            return Honest(nodeId, kind, payload);
        });
        Assert.Contains(run.Violations, v => v == "L1-N0001:PARENT_NOT_SHOWN");
        Assert.Contains(run.Nodes, n => n.NodeId == "L1-N0001" && n.Status == "REJECTED");
    }

    [Fact] public void An_INCOMPLETE_node_is_rejected_whole_and_no_decision_changes()
    {
        var c = Cands();
        var baseline = P7F1V6Hier.Run(c, Opts(), (a, b, c) => Honest(a, b, c));
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
            kind == P7F1V6Hier.Kind.Synthesis ? Node(nodeId, "INCOMPLETE", "{\"id\":\"xxxx\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"L0000:S0\"]}")
            : Honest(nodeId, kind, payload));
        Assert.Contains(run.Violations, v => v.Contains("NODE_INCOMPLETE_CHANGES_NOTHING", StringComparison.Ordinal));
        Assert.Equal(baseline.Final.Select(kv => (kv.Key, kv.Value.Decision)).OrderBy(x => x.Key), run.Final.Select(kv => (kv.Key, kv.Value.Decision)).OrderBy(x => x.Key));
    }

    [Fact] public void A_node_may_not_revise_a_context_only_id()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (kind == P7F1V6Hier.Kind.Local && nodeId == "L0-S0000")
            {
                var ctx = Root(payload).GetProperty("context")[0];
                return Node(nodeId, "COMPLETE", $"{{\"id\":\"{ctx.GetProperty("id").GetString()}\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"{ctx.GetProperty("sourceAlias").GetString()}\"]}}");
            }
            return Honest(nodeId, kind, payload);
        });
        Assert.Contains(run.Violations, v => v == "L0-S0000:ID_NOT_REVISABLE_IN_THIS_NODE");
    }

    [Fact] public void A_global_node_revises_only_what_it_was_shown_and_nothing_else_changes_source()
    {
        var c = Cands();
        var shownToGlobal = new List<(string Id, string Alias)>();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (kind != P7F1V6Hier.Kind.Global) return Honest(nodeId, kind, payload);
            shownToGlobal = Root(payload).GetProperty("shown").EnumerateArray()
                .Select(s => (s.GetProperty("id").GetString()!, s.GetProperty("sourceAlias").GetString()!)).ToList();
            // The first shown id is confirmed; the others are decided NOT_SELECT on their own evidence.
            var items = shownToGlobal.Skip(1).Select(s => $"{{\"id\":\"{s.Id}\",\"decision\":\"NOT_SELECT\",\"evidenceRefs\":[\"{s.Alias}\"]}}");
            return Node(nodeId, "COMPLETE", string.Join(",", items), confirmed: $"\"{shownToGlobal[0].Id}\"");
        });
        Assert.Empty(run.Violations);
        Assert.NotEmpty(shownToGlobal);
        var shownIds = shownToGlobal.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(run.Final.Where(kv => !shownIds.Contains(kv.Key)), kv => Assert.NotEqual("GLOBAL", kv.Value.SourceNode));
    }

    [Fact] public void A_global_COMPLETE_that_confirms_nothing_is_not_a_review()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
            kind == P7F1V6Hier.Kind.Global ? Node(nodeId, "COMPLETE") : Honest(nodeId, kind, payload));
        Assert.True(run.GlobalShown > 0);
        Assert.Contains(run.Violations, v => v.StartsWith("GLOBAL:UNCONFIRMED_SHOWN_IDS", StringComparison.Ordinal));
        Assert.False(run.GlobalAccepted);
        Assert.NotEqual("PROTOCOL_RESOLVED", run.RunStatus);
    }

    // ---------------- false-negative recovery: a middle NOT_SELECT is found by search and corrected after reopening ----------------

    [Fact] public void A_middle_NOT_SELECT_heading_is_retrieved_by_search_then_reopened_and_corrected_by_synthesis()
    {
        var c = Cands();
        // Shard 0 owns c[0..5]. With context 2, c[2] and c[3] are middle lines: not forwarded upward by the structural rule.
        var t = c.Skip(2).Take(2).First(x => !Even(x.Alias));
        var needle = t.Text.Length > 60 ? t.Text[..60] : t.Text;
        var baseline = P7F1V6Hier.Run(c, Opts(), (a, b, c) => Honest(a, b, c));
        var tId = baseline.IdByAlias[t.Alias];
        Assert.Equal("NOT_SELECT", baseline.Final[tId].Decision);

        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (nodeId != "L1-N0001") return Honest(nodeId, kind, payload);
            var rt = Root(payload).GetProperty("retrieved");
            if (rt.GetProperty("searches").GetArrayLength() == 0) return RetrieveJson(nodeId, search: needle);
            if (rt.GetProperty("reopened").GetArrayLength() == 0)
            {
                var found = rt.GetProperty("searches").EnumerateArray().First().GetProperty("matches").EnumerateArray()
                    .First(m => m.GetProperty("sourceAlias").GetString() == t.Alias).GetProperty("id").GetString()!;
                return RetrieveJson(nodeId, reopen: found);
            }
            var reopened = rt.GetProperty("reopened").EnumerateArray().First().GetProperty("id").GetString()!;
            Assert.Equal(tId, reopened);
            return Node(nodeId, "COMPLETE",
                $"{{\"id\":\"{reopened}\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"{t.Alias}\"]}}",
                confirmed: Others(payload, reopened));
        });

        Assert.Empty(run.Violations);
        Assert.Equal("PROTOCOL_RESOLVED", run.RunStatus);
        Assert.Equal("SELECT", run.Final[tId].Decision);
        Assert.Equal("L1-N0001", run.Final[tId].SourceNode);
        Assert.Equal(2, run.Nodes.Count(n => n.NodeId == "L1-N0001" && n.Status == "RETRIEVE"));
        Assert.Contains(run.Final[tId].Evidence!, e => e == t.Alias);
    }

    [Fact] public void Retrieval_rounds_are_bounded_and_refused_when_exhausted()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
            nodeId == "L1-N0001" ? RetrieveJson(nodeId, search: "RFC") : Honest(nodeId, kind, payload));
        Assert.Contains(run.Violations, v => v == "L1-N0001:RETRIEVAL_ROUNDS_EXCEEDED");
    }

    [Fact] public void A_reopen_of_an_id_outside_the_registry_is_refused()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
            nodeId == "L1-N0001" ? RetrieveJson(nodeId, reopen: "zzzz") : Honest(nodeId, kind, payload));
        Assert.Contains(run.Violations, v => v == "L1-N0001:ID_NOT_IN_REGISTRY");
    }

    [Fact] public void Retrieval_is_not_allowed_in_a_local_node()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
            nodeId == "L0-S0000" ? RetrieveJson(nodeId, search: "RFC") : Honest(nodeId, kind, payload));
        Assert.Contains(run.Violations, v => v == "L0-S0000:RETRIEVE_NOT_ALLOWED");
    }

    // ---------------- passUp, budgets and the canonical text rule ----------------

    [Fact] public void A_passUp_over_the_cap_stops_the_run_explicitly_and_nothing_is_added_after_the_check()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (kind == P7F1V6Hier.Kind.Synthesis && nodeId == "L1-N0001")
            {
                var shown = ShownIds(payload).ToArray();
                return Node(nodeId, "COMPLETE", passUp: Quoted(shown), confirmed: Quoted(shown));
            }
            return Honest(nodeId, kind, payload);
        });
        Assert.Equal("STOPPED_PASSUP_OVER_CAP:L1-N0001", run.RunStatus);
        Assert.Contains(run.Nodes, n => n.NodeId == "L1-N0001" && n.FailureCode == "PASSUP_OVER_CAP");
    }

    [Fact] public void A_passUp_id_that_was_not_shown_is_refused()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (kind == P7F1V6Hier.Kind.Synthesis && nodeId == "L1-N0001")
                return Node(nodeId, "COMPLETE", passUp: "\"zzzz\"", confirmed: Quoted(ShownIds(payload)));
            return Honest(nodeId, kind, payload);
        });
        Assert.Contains(run.Violations, v => v == "L1-N0001:PASSUP_NOT_SHOWN");
    }

    [Fact] public void Output_over_the_byte_budget_is_refused_whole()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts() with { OutputBudgetBytes = 2048 }, (nodeId, kind, payload) =>
            nodeId == "L0-S0000" ? Honest(nodeId, kind, payload) + new string(' ', 3000) : Honest(nodeId, kind, payload));
        Assert.Contains(run.Violations, v => v == "L0-S0000:OUTPUT_OVER_BUDGET");
    }

    [Fact] public void A_unicode_escape_in_a_response_is_refused_even_when_it_decodes_to_plain_text()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
            nodeId == "L0-S0000" ? "{\"protocolVersion\":\"" + Proto + "\",\"nodeId\":\"L0-S0000\",\"status\":\"INCOMPLETE\",\"note\":\"\\u0041\"}"
            : Honest(nodeId, kind, payload));
        Assert.Contains(run.Violations, v => v == "L0-S0000:ESCAPED_UNICODE_NOT_ALLOWED");
    }

    [Fact] public void A_payload_carries_Vietnamese_as_text_not_as_escapes()
    {
        var c = Cands();
        var seen = new List<string>();
        P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) => { seen.Add(payload); return Honest(nodeId, kind, payload); });
        Assert.DoesNotContain(seen, p => p.Contains("\\u", StringComparison.Ordinal));
        Assert.Contains(seen, p => p.Contains(Instr, StringComparison.Ordinal));
    }

    // ---------------- parameters are checked before any model call ----------------

    [Fact] public void Invalid_parameters_are_refused_before_any_model_call()
    {
        var c = Cands();
        int calls = 0;
        P7F1V6Hier.NodeModel m = (a, b, d) => { calls++; return Honest(a, b, d); };
        var bad = new[]
        {
            Opts() with { ShardSize = 0 }, Opts() with { FanIn = 1 }, Opts() with { Context = -1 }, Opts() with { PassUpCap = 0 },
            Opts() with { OutputBudgetBytes = 10 }, Opts() with { MaxInputBytes = 10 }, Opts() with { MaxRetrievalRounds = -1 },
            Opts() with { MaxSearchResults = 0 }, Opts() with { MaxReopen = 0 },
        };
        foreach (var o in bad) Assert.ThrowsAny<ArgumentException>(() => P7F1V6Hier.Run(c, o, m));
        Assert.Equal(0, calls);
    }

    // ---------------- property-style: random responses cannot break the bounds ----------------

    static string RandomResponse(Random rng, string nodeId, P7F1V6Hier.Kind kind, string payload)
    {
        if (kind == P7F1V6Hier.Kind.Interpretation) return Interp(payload);
        var r = Root(payload);
        var cands = new List<(string Id, string Alias)>();
        foreach (var p in new[] { "owned", "shown", "context" })
            if (r.TryGetProperty(p, out var arr))
                cands.AddRange(arr.EnumerateArray().Select(x => (x.GetProperty("id").GetString()!, x.GetProperty("sourceAlias").GetString()!)));
        string Pick() => cands.Count == 0 ? "zzzz" : cands[rng.Next(cands.Count)].Id;
        string Alias() => cands.Count == 0 ? "xx" : cands[rng.Next(cands.Count)].Alias;
        string[] decisions = ["SELECT", "NOT_SELECT", "UNRESOLVED"];
        switch (rng.Next(12))
        {
            case 0: return "not json at all";
            case 1: return "{\"note\":\"\\u0041\"}";
            case 2: return new string(' ', 17000) + "{}";
            case 3: return Node(nodeId, "RETRIEVE");
            case 4: return RetrieveJson(nodeId, search: "abc");
            case 5: return RetrieveJson(nodeId, reopen: Pick());
            case 6: return Node(nodeId, "INCOMPLETE");
            case 7: return "{\"protocolVersion\":\"" + Proto + "\",\"nodeId\":\"" + nodeId + "\",\"status\":\"COMPLETE\",\"confirmed\":[1,true,null]}";
            default:
            {
                var items = new List<string>();
                var confirmed = new List<string>();
                foreach (var (id, _) in cands)
                {
                    int roll = rng.Next(5);
                    if (roll == 0) continue;
                    if (roll == 1) { confirmed.Add(id); continue; }
                    var d = decisions[rng.Next(3)];
                    var refs = d == "UNRESOLVED" ? "" : $"\"{Alias()}\"";
                    items.Add($"{{\"id\":\"{id}\",\"decision\":\"{d}\",\"evidenceRefs\":[{refs}]}}");
                }
                if (rng.Next(4) == 0) items.Add($"{{\"id\":\"{Pick()}\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"{Alias()}\"],\"parentId\":\"{Pick()}\"}}");
                var passUp = Quoted(Enumerable.Range(0, rng.Next(0, 12)).Select(_ => Pick()));
                return Node(nodeId, "COMPLETE", string.Join(",", items), passUp, Quoted(confirmed));
            }
        }
    }

    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Random_responses_never_throw_and_every_accepted_output_is_within_budget(int seed)
    {
        var c = Cands();
        var o = Opts() with { OutputBudgetBytes = 16384 };
        var rng = new Random(seed);
        var run = P7F1V6Hier.Run(c, o, (nodeId, kind, payload) => RandomResponse(rng, nodeId, kind, payload));
        Assert.All(run.Nodes.Where(n => n.Status == "ACCEPTED"), n => Assert.True(n.OutputBytes <= o.OutputBudgetBytes));
        Assert.All(run.Nodes, n => Assert.True(n.InputBytes <= o.MaxInputBytes));
        Assert.All(run.Final.Values, s => Assert.Contains(s.Decision, new[] { "SELECT", "NOT_SELECT", "UNRESOLVED" }));
        Assert.True(run.Raw.Verify());
        Assert.All(run.Final.Values, s => Assert.True(run.Raw.Items.ContainsKey(s.SourceOutputSha256)));
    }

    // ---------------- position sensitivity (Lost in the Middle: a design criterion, not a claim about Qwen) ----------------

    /// <summary>A deliberately position-sensitive scripted model: in every local prompt the first owned id is SELECT, the rest NOT_SELECT.</summary>
    static string FirstOnly(string nodeId, P7F1V6Hier.Kind kind, string payload)
    {
        if (kind != P7F1V6Hier.Kind.Local) return Honest(nodeId, kind, payload);
        var owned = Root(payload).GetProperty("owned").EnumerateArray().ToArray();
        var items = owned.Select((o, i) => $"{{\"id\":\"{o.GetProperty("id").GetString()}\",\"decision\":\"{(i == 0 ? "SELECT" : "NOT_SELECT")}\",\"evidenceRefs\":[\"{o.GetProperty("sourceAlias").GetString()}\"]}}");
        return Node(nodeId, "COMPLETE", string.Join(",", items));
    }

    [Fact] public void Two_different_requests_on_the_same_PDF_give_different_final_states_with_the_same_tree()
    {
        var c = Cands();
        P7F1V6Hier.NodeModel byInstruction = (nodeId, kind, payload) =>
        {
            if (kind == P7F1V6Hier.Kind.Interpretation) return Interp(payload);
            var instr = Root(payload).GetProperty("instruction").GetString();
            return instr == Instr ? Honest(nodeId, kind, payload) : Honest(nodeId, kind, payload, _ => "NOT_SELECT");
        };
        var a = P7F1V6Hier.Run(c, Opts(Instr), byInstruction);
        var b = P7F1V6Hier.Run(c, Opts("Chỉ lấy tiêu đề các điều trong nghị định"), byInstruction);
        Assert.Equal(a.Nodes.Count, b.Nodes.Count);
        Assert.NotEqual(a.TranscriptSha256, b.TranscriptSha256);
        Assert.NotEqual(a.Final.Count(kv => kv.Value.Decision == "SELECT"), b.Final.Count(kv => kv.Value.Decision == "SELECT"));
    }

    [Fact] public void The_position_probe_detects_a_position_sensitive_model()
    {
        var c = Cands();
        var normal = P7F1V6Hier.Run(c, Opts(), FirstOnly);
        var reversed = P7F1V6Hier.Run(c, Opts() with { PromptOrder = "REVERSED" }, FirstOnly);
        var diff = P7F1V6Hier.SensitivityDiff(normal, reversed);
        Assert.Equal(c.Count, diff.Compared);
        Assert.True(diff.Decisions > 0, "a position-sensitive model must change decisions when the order changes");
    }

    [Fact] public void The_position_probe_is_silent_for_a_position_insensitive_model()
    {
        var c = Cands();
        var normal = P7F1V6Hier.Run(c, Opts(), (a, b, c) => Honest(a, b, c));
        var reversed = P7F1V6Hier.Run(c, Opts() with { PromptOrder = "REVERSED" }, (a, b, c) => Honest(a, b, c));
        var diff = P7F1V6Hier.SensitivityDiff(normal, reversed);
        Assert.Equal((0, 0, 0, 0), (diff.Decisions, diff.Parents, diff.Continuations, diff.Evidence));
    }

    [Fact] public void Target_at_head_middle_and_tail_keeps_the_source_set_and_ids_fixed_and_moves_only_the_sensitive_decision()
    {
        var c = Cands();
        var target = c[10].Alias;     // shard 1 at size 6: owned c[6..11]
        var sensitive = new Dictionary<string, P7F1V6Hier.RunResult>();
        var insensitive = new Dictionary<string, P7F1V6Hier.RunResult>();
        foreach (var pos in new[] { "HEAD", "MIDDLE", "TAIL" })
        {
            var o = Opts() with { PromptOrder = $"TARGET:{target}:{pos}" };
            sensitive[pos] = P7F1V6Hier.Run(c, o, (n, k, p) => FirstOnly(n, k, p));
            insensitive[pos] = P7F1V6Hier.Run(c, o, (n, k, p) => Honest(n, k, p));
        }
        var ids = sensitive["HEAD"].IdByAlias.OrderBy(x => x.Key);
        Assert.All(sensitive.Values, r => Assert.Equal(ids, r.IdByAlias.OrderBy(x => x.Key)));
        Assert.All(insensitive.Values, r => Assert.Equal(ids, r.IdByAlias.OrderBy(x => x.Key)));
        var tid = sensitive["HEAD"].IdByAlias[target];
        Assert.Equal("SELECT", sensitive["HEAD"].Final[tid].Decision);
        Assert.Equal("NOT_SELECT", sensitive["MIDDLE"].Final[tid].Decision);
        Assert.Equal("NOT_SELECT", sensitive["TAIL"].Final[tid].Decision);
        Assert.All(insensitive.Values, r => Assert.Equal(insensitive["HEAD"].Final[tid].Decision, r.Final[tid].Decision));
        Assert.All(sensitive.Values, r => Assert.Equal(c.Count, r.Final.Count));
    }

    [Fact] public void Retrieval_scenario_one_search_round_then_confirmation_is_accepted()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (nodeId != "L1-N0001") return Honest(nodeId, kind, payload);
            var rt = Root(payload).GetProperty("retrieved");
            if (rt.GetProperty("searches").GetArrayLength() == 0)
                return JsonSerializer.Serialize(new { protocolVersion = Proto, nodeId, status = "RETRIEVE", retrieve = new { search = "the" } }, Json);
            return Node(nodeId, "COMPLETE", confirmed: Quoted(ShownIds(payload)));
        });
        Assert.Empty(run.Violations);
        Assert.Equal("PROTOCOL_RESOLVED", run.RunStatus);
        Assert.Single(run.Nodes.Where(n => n.NodeId == "L1-N0001" && n.Status == "RETRIEVE"));
    }

    [Fact] public void Provenance_accounting_partitions_every_final_state_and_links_to_raw_outputs()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) => Honest(nodeId, kind, payload));
        var acc = P7F1V6Hier.Account(run);
        Assert.Equal(c.Count, acc.Total);
        Assert.Equal(acc.Total, acc.LocalResolved + acc.ReopenedConfirmed + acc.ReopenedResolved + acc.SynthesisResolved);
        Assert.Equal(acc.Total, acc.GloballyReviewed + acc.CarriedWithoutGlobalReview);
        Assert.Equal(0, acc.ReopenedConfirmed + acc.ReopenedResolved);          // no reopening in the honest run
        Assert.True(acc.GloballyReviewed > 0 && acc.CarriedWithoutGlobalReview > 0);
        Assert.All(run.Final.Values, s =>
        {
            var rec = run.Nodes.Last(n => n.NodeId == s.SourceNode && n.Status == "ACCEPTED");
            Assert.Equal(rec.OutputSha256, s.SourceOutputSha256);
            Assert.True(run.Raw.Items.ContainsKey(s.SourceOutputSha256));
        });
    }

    [Fact] public void Reopened_then_revised_id_is_counted_as_ReopenedResolved()
    {
        var c = Cands();
        var t = c.Skip(2).Take(2).First(x => !Even(x.Alias));   // a middle NOT_SELECT line of shard 0
        var needle = t.Text.Length > 60 ? t.Text[..60] : t.Text;
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (nodeId != "L1-N0001") return Honest(nodeId, kind, payload);
            var rt = Root(payload).GetProperty("retrieved");
            if (rt.GetProperty("searches").GetArrayLength() == 0)
                return JsonSerializer.Serialize(new { protocolVersion = Proto, nodeId, status = "RETRIEVE", retrieve = new { search = needle } }, Json);
            if (rt.GetProperty("reopened").GetArrayLength() == 0)
            {
                var found = rt.GetProperty("searches").EnumerateArray().First().GetProperty("matches").EnumerateArray()
                    .First(m => m.GetProperty("sourceAlias").GetString() == t.Alias).GetProperty("id").GetString()!;
                return JsonSerializer.Serialize(new { protocolVersion = Proto, nodeId, status = "RETRIEVE", retrieve = new { reopen = new[] { found } } }, Json);
            }
            var reopened = rt.GetProperty("reopened").EnumerateArray().First().GetProperty("id").GetString()!;
            return Node(nodeId, "COMPLETE", $"{{\"id\":\"{reopened}\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"{t.Alias}\"]}}", confirmed: Others(payload, reopened));
        });
        Assert.Empty(run.Violations);
        var acc = P7F1V6Hier.Account(run);
        Assert.Equal(1, acc.ReopenedResolved);
        Assert.Equal(0, acc.ReopenedConfirmed);
        Assert.Equal(c.Count, acc.Total);
    }

    [Fact] public void A_rejected_synthesis_node_never_yields_RESOLVED_even_when_global_is_accepted()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
            nodeId == "L1-N0001" ? "not json" : Honest(nodeId, kind, payload));
        Assert.Contains(run.Violations, v => v == "L1-N0001:RESPONSE_NOT_JSON");
        Assert.True(run.GlobalAccepted);
        Assert.NotEqual("PROTOCOL_RESOLVED", run.RunStatus);
    }

    [Fact] public void Input_over_the_budget_stops_the_run_and_is_never_RESOLVED()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts() with { MaxInputBytes = 1024 }, (nodeId, kind, payload) => Honest(nodeId, kind, payload));
        Assert.StartsWith("STOPPED_INPUT_OVER_BUDGET", run.RunStatus, StringComparison.Ordinal);
        Assert.NotEqual("PROTOCOL_RESOLVED", run.RunStatus);
    }

    [Fact] public void A_seeded_order_is_reproducible_and_changes_only_the_transcript_order()
    {
        var c = Cands();
        var a = P7F1V6Hier.Run(c, Opts() with { PromptOrder = "SEEDED:7" }, FirstOnly);
        var b = P7F1V6Hier.Run(c, Opts() with { PromptOrder = "SEEDED:7" }, FirstOnly);
        Assert.Equal(a.TranscriptSha256, b.TranscriptSha256);
        Assert.Equal(0, P7F1V6Hier.SensitivityDiff(a, b).Decisions);
    }
}
