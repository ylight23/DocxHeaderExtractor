using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D2 (offline): hierarchical reconciliation with scripted models on pages 1–2 of the real RFC 094.</summary>
public sealed class P7F1V6HierTests
{
    const string Rfc = "todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf";
    const string Instr = "Trích xuất các heading chính";

    static IReadOnlyList<P7F1V6Hier.Cand> Cands()
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(Rfc));
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var legacy = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        return legacy.AllLines.Where(l => l.Page is 1 or 2).Select(l => new P7F1V6Hier.Cand(l.Alias, l.Page!.Value, l.Text)).ToArray();
    }

    static P7F1V6Hier.Options Opts(string instr = Instr) => new(instr, "src-sha", ShardSize: 6, Context: 2, FanIn: 3, PassUpCap: 8);

    static bool Even(string alias) => int.Parse(alias[1..5]) % 2 == 0;

    static JsonElement Root(string payload) => JsonDocument.Parse(payload).RootElement;

    static string Node(string nodeId, string status, string itemsJson = "", string passUpJson = "") =>
        $"{{\"protocolVersion\":\"P7_F1_V6_HIER_NODE_V1\",\"nodeId\":\"{nodeId}\",\"status\":\"{status}\",\"items\":[{itemsJson}]{(passUpJson == "" ? "" : $",\"passUp\":[{passUpJson}]")}}}";

    /// <summary>Local nodes decide every owned candidate (SELECT when the alias index is even). Other nodes change nothing.</summary>
    static string Honest(string nodeId, P7F1V6Hier.Kind kind, string payload, Func<string, string>? localRule = null)
    {
        var r = Root(payload);
        if (kind == P7F1V6Hier.Kind.Local)
        {
            var items = r.GetProperty("owned").EnumerateArray().Select(o =>
            {
                var a = o.GetProperty("sourceAlias").GetString()!;
                var d = localRule?.Invoke(a) ?? (Even(a) ? "SELECT" : "NOT_SELECT");
                return $"{{\"id\":\"{o.GetProperty("id").GetString()}\",\"decision\":\"{d}\",\"evidenceRefs\":[\"{a}\"]}}";
            });
            return Node(nodeId, "COMPLETE", string.Join(",", items));
        }
        if (kind == P7F1V6Hier.Kind.Synthesis)
        {
            // passes up its selected ids (the first passUp cap of them): a model-chosen summary, here a simple one.
            var up = r.GetProperty("shown").EnumerateArray().Where(s => s.GetProperty("current").GetString() == "SELECT")
                .Take(8).Select(s => $"\"{s.GetProperty("id").GetString()}\"");
            return Node(nodeId, "COMPLETE", "", string.Join(",", up));
        }
        return Node(nodeId, "COMPLETE");
    }

    static string Id(string payload, string alias) =>
        Root(payload).GetProperty("shown").EnumerateArray().First(s => s.GetProperty("sourceAlias").GetString() == alias).GetProperty("id").GetString()!;

    [Fact] public void A_full_hierarchical_run_gives_every_candidate_an_explicit_final_state_with_provenance()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) => Honest(nodeId, kind, payload));
        Assert.Empty(run.Violations);
        Assert.Equal(c.Count, run.Final.Count);
        Assert.All(run.Final.Values, s => Assert.NotEqual("MISSING", s.Decision));
        Assert.Contains(run.Nodes, n => n.Kind == "Synthesis");
        Assert.Equal("GLOBAL", run.Nodes[^1].NodeId);                 // one global node at the top
        Assert.True(run.Raw.Verify());
        Assert.All(run.Nodes, n => Assert.True(run.Raw.Items.ContainsKey(n.InputSha256)));
    }

    [Fact] public void A_heading_split_across_two_shards_is_joined_by_a_synthesis_node_that_names_its_continuation()
    {
        var c = Cands();
        // Shard 0 owns c[0..5], shard 1 owns c[6..11]. Choose a = last of shard 0 (SELECT) and b = first of shard 1 (NOT_SELECT).
        var a = c[5].Alias; var b = c[6].Alias;
        Assert.True(Even(a) && !Even(b), "fixture needs a SELECT at the boundary and a NOT_SELECT just after it");
        string? aId = null, bId = null;
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (kind == P7F1V6Hier.Kind.Synthesis && payload.Contains(b, StringComparison.Ordinal) && payload.Contains(a, StringComparison.Ordinal) && aId is null)
            {
                aId = Id(payload, a); bId = Id(payload, b);
                return Node(nodeId, "COMPLETE", $"{{\"id\":\"{bId}\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"{b}\"],\"continuesFromId\":\"{aId}\"}}");
            }
            return Honest(nodeId, kind, payload);
        });
        Assert.Empty(run.Violations);
        Assert.NotNull(bId);
        var sb = run.Final[bId!];
        Assert.Equal("SELECT", sb.Decision);
        Assert.Equal(aId, sb.ContinuesFromId);
        Assert.StartsWith("L1", sb.SourceNode, StringComparison.Ordinal);       // decided by the first synthesis node, not by a shard
        Assert.True(run.Raw.Items.ContainsKey(sb.SourceOutputSha256));          // provenance to the raw response
    }

    [Fact] public void A_parent_reference_to_an_id_that_was_not_shown_is_rejected_and_changes_nothing()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (kind == P7F1V6Hier.Kind.Synthesis && nodeId == "L1-N0001")
            {
                var target = Root(payload).GetProperty("shown").EnumerateArray().First().GetProperty("id").GetString()!;
                return Node(nodeId, "COMPLETE", $"{{\"id\":\"{target}\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"{Root(payload).GetProperty("shown")[0].GetProperty("sourceAlias").GetString()}\"],\"parentId\":\"zzzz\"}}");
            }
            return Honest(nodeId, kind, payload);
        });
        Assert.Contains(run.Violations, v => v.Contains("L1-N0001:PARENT_NOT_SHOWN", StringComparison.Ordinal));
        Assert.Contains(run.Nodes, n => n.NodeId == "L1-N0001" && n.Status == "REJECTED");
    }

    [Fact] public void An_INCOMPLETE_node_is_rejected_whole_and_no_decision_changes()
    {
        var c = Cands();
        var baseline = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) => Honest(nodeId, kind, payload));
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
                // The context line after shard 0 is read-only for shard 0. Trying to decide it must be refused.
                var ctx = Root(payload).GetProperty("context")[0];
                return Node(nodeId, "COMPLETE", $"{{\"id\":\"{ctx.GetProperty("id").GetString()}\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"{ctx.GetProperty("sourceAlias").GetString()}\"]}}");
            }
            return Honest(nodeId, kind, payload);
        });
        Assert.Contains(run.Violations, v => v.Contains("L0-S0000:LOCAL_NODE_UNDECIDED", StringComparison.Ordinal) || v.Contains("ID_NOT_REVISABLE_IN_THIS_NODE", StringComparison.Ordinal));
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
            // Keep only the first shown id as a selection; the others are decided NOT_SELECT on their own evidence.
            var items = shownToGlobal.Skip(1).Select(s => $"{{\"id\":\"{s.Item1}\",\"decision\":\"NOT_SELECT\",\"evidenceRefs\":[\"{s.Item2}\"]}}");
            return Node(nodeId, "COMPLETE", string.Join(",", items));
        });
        Assert.Empty(run.Violations);
        Assert.NotEmpty(shownToGlobal);
        var shownIds = shownToGlobal.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        // Among what the global node saw, at most one selection survives unless it was carried.
        Assert.True(shownIds.Count(id => run.Final[id].Decision == "SELECT" && run.Final[id].SourceNode == "GLOBAL") <= 1);
        // Ids the global node did not see keep the source they had: it never revised them.
        Assert.All(run.Final.Where(kv => !shownIds.Contains(kv.Key)), kv => Assert.NotEqual("GLOBAL", kv.Value.SourceNode));
    }

    [Fact] public void Two_different_requests_on_the_same_PDF_give_different_final_states_with_the_same_tree()
    {
        var c = Cands();
        P7F1V6Hier.NodeModel byInstruction = (nodeId, kind, payload) =>
        {
            var instr = Root(payload).GetProperty("instruction").GetString();
            return instr == Instr ? Honest(nodeId, kind, payload)
                : Honest(nodeId, kind, payload, _ => "NOT_SELECT");
        };
        var a = P7F1V6Hier.Run(c, Opts(Instr), byInstruction);
        var b = P7F1V6Hier.Run(c, Opts("Chỉ lấy tiêu đề các điều trong nghị định"), byInstruction);
        Assert.Equal(a.Nodes.Count, b.Nodes.Count);                       // the same tree shape
        Assert.NotEqual(a.TranscriptSha256, b.TranscriptSha256);
        Assert.NotEqual(a.Final.Count(kv => kv.Value.Decision == "SELECT"), b.Final.Count(kv => kv.Value.Decision == "SELECT"));
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
        var normal = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) => Honest(nodeId, kind, payload));
        var reversed = P7F1V6Hier.Run(c, Opts() with { PromptOrder = "REVERSED" }, (nodeId, kind, payload) => Honest(nodeId, kind, payload));
        var diff = P7F1V6Hier.SensitivityDiff(normal, reversed);
        Assert.Equal((0, 0, 0, 0), (diff.Decisions, diff.Parents, diff.Continuations, diff.Evidence));
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
