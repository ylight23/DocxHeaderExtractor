using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7-F1 V6 hierarchical semantic reconciliation (Issue #9, design D2, offline). Qwen makes every semantic decision at every node:
/// local decisions on a shard, a shard self-review, then synthesis nodes that merge children by count, then one global node.
/// C# only forms shards and groups by position and count, checks identity, evidence, scope and completeness, and records
/// provenance. C# never selects a heading, never derives a hierarchy and never resolves a conflict.
/// Node contract (P7_F1_V6_HIER_NODE_V1): status, items (decisions and optional parent/continuation references), passUp (ids
/// Qwen passes to the next level). Unlisted ids of a COMPLETE synthesis node are CARRIED from their last decision, with the
/// node that carried them recorded; an INCOMPLETE node changes nothing.
/// </summary>
internal static class P7F1V6Hier
{
    public const string Version = "P7_F1_V6_HIER_V1";
    public const string NodeVersion = "P7_F1_V6_HIER_NODE_V1";
    public const int MaxEvidenceRefs = 8, MaxNoteChars = 300;
    public static readonly string[] Decisions = ["SELECT", "NOT_SELECT", "UNRESOLVED"];
    public static readonly string[] TechnicalStatuses = ["COMPLETE", "INCOMPLETE", "CLARIFICATION_REQUIRED", "NOT_ANSWERABLE"];

    public enum Kind { Local, Reconcile, Synthesis, Global }

    public sealed record Cand(string Alias, int Page, string Text);

    /// <summary>Owned candidates decide; Before/After are context only (read-only, never revisable by this shard).</summary>
    public sealed record Shard(int Index, IReadOnlyList<Cand> Owned, IReadOnlyList<Cand> Before, IReadOnlyList<Cand> After);

    /// <summary>Positional shards: consecutive owned blocks of <paramref name="size"/> candidates, with <paramref name="context"/> lines on each side.</summary>
    public static IReadOnlyList<Shard> Shards(IReadOnlyList<Cand> ordered, int size, int context)
    {
        var list = new List<Shard>();
        for (int start = 0, idx = 0; start < ordered.Count; start += size, idx++)
        {
            var owned = ordered.Skip(start).Take(size).ToArray();
            var before = ordered.Skip(Math.Max(0, start - context)).Take(Math.Min(context, start)).ToArray();
            int afterStart = start + owned.Length;
            var after = ordered.Skip(afterStart).Take(context).ToArray();
            list.Add(new Shard(idx, owned, before, after));
        }
        return list;
    }

    /// <summary>The state of one candidate: its decision, the node that set it, and any reference Qwen gave.</summary>
    public sealed record State(string Decision, string SourceNode, string SourceOutputSha256, string? ParentId, string? ContinuesFromId, int Carried,
        IReadOnlyList<string>? Evidence = null);

    public sealed record NodeRecord(string NodeId, string Kind, int Level, string InputSha256, int InputBytes, string? OutputSha256,
        int OutputBytes, string Status, string? FailureCode, int Revised, int PassUp);

    public sealed record RunResult(string RunStatus, IReadOnlyDictionary<string, State> Final, IReadOnlyList<NodeRecord> Nodes,
        IReadOnlyList<string> Violations, string TranscriptSha256, IReadOnlyDictionary<string, string> IdByAlias, P7F1V6RawStore Raw);

    /// <summary>The scripted model: returns the node's raw response text for a node payload.</summary>
    public delegate string NodeModel(string nodeId, Kind kind, string payloadJson);

    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>OutputBudgetBytes bounds the RAW bytes of any accepted node response; a larger response is rejected, so an accepted
    /// response is within the bound by construction. The bound must stay below the visible-output budget for the endpoint.</summary>
    public sealed record Options(string Instruction, string SourceSha256, int ShardSize, int Context, int FanIn, int PassUpCap,
        string? RawDir = null, int OutputBudgetBytes = 16384, string PromptOrder = "NORMAL");

    public static RunResult Run(IReadOnlyList<Cand> ordered, Options o, NodeModel model)
    {
        var raw = new P7F1V6RawStore(o.RawDir);
        var ids = AssignIds(ordered.Select(c => c.Alias));
        var idToAlias = ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var textByAlias = ordered.ToDictionary(c => c.Alias, c => c, StringComparer.Ordinal);
        var overview = P7F1V6Core.Overview(ordered.Select(c => new F1QSourceLine(c.Alias, 0, c.Page, c.Text, null, null)).ToArray());
        var instructionSha = P7F1V6Core.InstructionSha256(o.Instruction);
        var states = new Dictionary<string, State>(StringComparer.Ordinal);
        var nodes = new List<NodeRecord>();
        var violations = new List<string>();
        var transcript = new List<string>();
        var shards = Shards(ordered, o.ShardSize, o.Context);

        // ---- level 0: local decisions and a shard self-review, per shard ----
        var units = new List<(string Name, List<string> Ids, List<string> Revisable)>();
        foreach (var sh in shards)
        {
            var ownedIds = sh.Owned.Select(c => ids[c.Alias]).ToList();
            var localNode = $"L0-S{sh.Index:D4}";
            var payload = Payload(localNode, Kind.Local, 0, o, instructionSha, overview, ids,
                sh.Owned.Select(c => (c, (string?)null)).ToList(), sh.Before.Concat(sh.After).ToList(), states: null, ownedIds, null, textByAlias);
            Apply(localNode, Kind.Local, 0, payload, model, ids, ownedIds, sh.Owned.Select(c => c.Alias).ToHashSet(StringComparer.Ordinal),
                sh.Before.Concat(sh.After).Select(c => c.Alias).ToHashSet(StringComparer.Ordinal), states, nodes, violations, transcript, raw, o, passUpCap: int.MaxValue,
                ids, idToAlias, textByAlias);
            var recNode = $"L0R-S{sh.Index:D4}";
            var recPayload = Payload(recNode, Kind.Reconcile, 0, o, instructionSha, overview, ids,
                sh.Owned.Select(c => (c, (string?)null)).ToList(), sh.Before.Concat(sh.After).ToList(), states, ownedIds, null, textByAlias);
            Apply(recNode, Kind.Reconcile, 0, recPayload, model, ids, ownedIds, sh.Owned.Select(c => c.Alias).ToHashSet(StringComparer.Ordinal),
                sh.Before.Concat(sh.After).Select(c => c.Alias).ToHashSet(StringComparer.Ordinal), states, nodes, violations, transcript, raw, o, int.MaxValue,
                ids, idToAlias, textByAlias);
            units.Add((localNode, ownedIds, Forward(ownedIds, states, o.Context)));
        }

        // ---- synthesis levels: group units by count; each node sees the children's passUp sets and its boundary windows ----
        int level = 1;
        while (units.Count > 1)
        {
            var next = new List<(string Name, List<string> Ids, List<string> Revisable)>();
            for (int g = 0; g < units.Count; g += o.FanIn)
            {
                var children = units.Skip(g).Take(o.FanIn).ToList();
                var nodeName = $"L{level}-N{next.Count + 1:D4}";
                var shown = children.SelectMany(c => c.Revisable).Distinct(StringComparer.Ordinal).ToList();
                var payload = Payload(nodeName, Kind.Synthesis, level, o, instructionSha, overview, ids, null, null, states, shown, children.Select(c => c.Name).ToArray(), textByAlias);
                var outUnits = Apply(nodeName, Kind.Synthesis, level, payload, model, ids, shown, null, null, states, nodes, violations, transcript, raw,
                    o, o.PassUpCap, ids, idToAlias, textByAlias, mustPassUp: children.SelectMany(c => c.Ids).Where(id => states.TryGetValue(id, out var s) && s.Decision is "UNRESOLVED").ToHashSet(StringComparer.Ordinal));
                next.Add((nodeName, children.SelectMany(c => c.Ids).ToList(), outUnits));
            }
            units = next;
            level++;
        }

        // ---- global node over the root's passUp set ----
        if (units.Count == 1)
        {
            var globalShown = units[0].Revisable;
            var nodeName = "GLOBAL";
            var payload = Payload(nodeName, Kind.Global, level, o, instructionSha, overview, ids, null, null, states, globalShown, new[] { units[0].Name }, textByAlias);
            Apply(nodeName, Kind.Global, level, payload, model, ids, globalShown, null, null, states, nodes, violations, transcript, raw, o, int.MaxValue,
                ids, idToAlias, textByAlias, mustPassUp: new HashSet<string>(StringComparer.Ordinal));
        }

        var status = violations.Count == 0 ? "COMPLETED" : "COMPLETED_WITH_VIOLATIONS";
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", transcript))));
        return new RunResult(status, states, nodes, violations, sha, ids, raw);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // One node: payload, model call, binding. Returns the ids the node passes up (for synthesis nodes).

    static List<string> Apply(string nodeId, Kind kind, int lvl, string payload, NodeModel model, IReadOnlyDictionary<string, string> ids,
        IEnumerable<string> shownOrOwned, HashSet<string>? ownedAliases, HashSet<string>? contextAliases, Dictionary<string, State> states,
        List<NodeRecord> nodes, List<string> violations, List<string> transcript, P7F1V6RawStore raw, Options o, int passUpCap,
        IReadOnlyDictionary<string, string> idsAgain, IReadOnlyDictionary<string, string> idToAlias, Dictionary<string, Cand> textByAlias,
        HashSet<string>? mustPassUp = null)
    {
        var shown = shownOrOwned.ToList();
        var inputBytes = Encoding.UTF8.GetBytes(payload);
        var inputSha = raw.Put(inputBytes);
        var response = model(nodeId, kind, payload);
        var outBytes = Encoding.UTF8.GetBytes(response);
        var outSha = raw.Put(outBytes);
        transcript.Add($"{nodeId}|PAYLOAD|{inputSha}|{inputBytes.Length}");
        transcript.Add($"{nodeId}|OUTPUT|{outSha}|{outBytes.Length}");

        var shownSet = shown.ToHashSet(StringComparer.Ordinal);
        var shownAliases = shown.Select(id => idToAlias[id]).ToHashSet(StringComparer.Ordinal);
        if (contextAliases is not null) foreach (var a in contextAliases) shownAliases.Add(a);
        // Revisable: owned ids for local/reconcile nodes; every shown id for synthesis and global nodes.
        var revisable = ownedAliases is null ? shownSet : ownedAliases.Select(a => ids[a]).ToHashSet(StringComparer.Ordinal);
        var parsed = outBytes.Length > o.OutputBudgetBytes ? new ParsedNode([], [], "OUTPUT_OVER_BUDGET")
            : ParseNode(response, nodeId, kind, revisable, shownSet, idToAlias, shownAliases, passUpCap);
        int revised = 0; int passUp = parsed.PassUp.Count; string? failure = parsed.Failure;
        if (failure is null)
        {
            foreach (var it in parsed.Items)
            {
                var previous = states.TryGetValue(it.Id, out var p) ? p : null;
                states[it.Id] = new State(it.Decision, nodeId, outSha, it.ParentId, it.ContinuesFromId, previous?.Carried ?? 0, it.EvidenceRefs);
                revised++;
            }
            if (kind is Kind.Synthesis or Kind.Global)
            {
                // COMPLETE: unlisted revisable ids keep their decision and record the carry.
                foreach (var id in revisable.Where(id => !parsed.Items.Any(i => i.Id == id)))
                    if (states.TryGetValue(id, out var s)) states[id] = s with { Carried = s.Carried + 1 };
            }
            if (mustPassUp is not null)
                foreach (var m in mustPassUp.Where(m => !parsed.PassUp.Contains(m)))
                    parsed.PassUp.Add(m);   // unresolved ids and boundary windows go up by rule, never dropped
        }
        else violations.Add($"{nodeId}:{failure}");
        nodes.Add(new NodeRecord(nodeId, kind.ToString(), lvl, inputSha, inputBytes.Length, outSha, outBytes.Length,
            failure is null ? "ACCEPTED" : "REJECTED", failure, revised, passUp));
        return parsed.PassUp.ToList();
    }

    /// <summary>What a unit passes to the next level, by rule: SELECT and UNRESOLVED ids, plus the boundary windows (first and last
    /// K ids) so that a split heading can still be seen across the group edge. Structural, never semantic.</summary>
    static List<string> Forward(IReadOnlyList<string> ownedIds, Dictionary<string, State> states, int k)
    {
        var boundary = ownedIds.Take(k).Concat(ownedIds.TakeLast(k)).ToHashSet(StringComparer.Ordinal);
        return ownedIds.Where(id => boundary.Contains(id) || (states.TryGetValue(id, out var s) && s.Decision is "SELECT" or "UNRESOLVED")).ToList();
    }

    // ------------------------------------------------------------------------------------------------------------------

    public sealed record ParsedItem(string Id, string Decision, IReadOnlyList<string> EvidenceRefs, string? ParentId, string? ContinuesFromId);

    public sealed record ParsedNode(IReadOnlyList<ParsedItem> Items, List<string> PassUp, string? Failure);

    static ParsedNode ParseNode(string response, string nodeId, Kind kind, IReadOnlySet<string> revisable, IReadOnlySet<string> shown,
        IReadOnlyDictionary<string, string> idToAlias, IReadOnlySet<string> shownAliases, int passUpCap)
    {
        var none = new List<ParsedItem>();
        ParsedNode Fail(string code) => new(none, [], code);
        // Canonical text only: a unicode escape can stand for an ASCII character six times over, which the bound does not allow.
        if (response.Contains("\\u", StringComparison.Ordinal)) return Fail("ESCAPED_UNICODE_NOT_ALLOWED");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(response); } catch (JsonException) { return Fail("RESPONSE_NOT_JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("ROOT_NOT_OBJECT");
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            string[] required = ["protocolVersion", "nodeId", "status", "items"];
            string[] allowed = ["protocolVersion", "nodeId", "status", "items", "passUp"];
            if (required.Any(k => !names.Contains(k, StringComparer.Ordinal)) || names.Any(n => !allowed.Contains(n, StringComparer.Ordinal))) return Fail("ROOT_KEYS_INVALID");
            if (Str(root, "protocolVersion") != NodeVersion) return Fail("PROTOCOL_VERSION_INVALID");
            if (Str(root, "nodeId") != nodeId) return Fail("NODE_ID_MISMATCH");
            var status = Str(root, "status");
            if (status is null || !TechnicalStatuses.Contains(status, StringComparer.Ordinal)) return Fail("STATUS_INVALID");
            if (status != "COMPLETE") return Fail("NODE_" + status + "_CHANGES_NOTHING");
            var items = new List<ParsedItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in root.GetProperty("items").EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) return Fail("ITEM_NOT_OBJECT");
                var id = Str(it, "id") ?? "";
                if (!revisable.Contains(id)) return Fail("ID_NOT_REVISABLE_IN_THIS_NODE");
                if (!seen.Add(id)) return Fail("ID_LISTED_TWICE");
                var decision = Str(it, "decision");
                if (decision is null || !Decisions.Contains(decision, StringComparer.Ordinal)) return Fail("DECISION_VALUE_INVALID");
                var refs = new List<string>();
                if (it.TryGetProperty("evidenceRefs", out var er))
                {
                    if (er.ValueKind != JsonValueKind.Array || er.GetArrayLength() > MaxEvidenceRefs || er.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                        return Fail("EVIDENCE_REFS_INVALID");
                    refs.AddRange(er.EnumerateArray().Select(x => x.GetString()!));
                }
                if (decision is "SELECT" or "NOT_SELECT")
                {
                    if (refs.Count == 0) return Fail("EVIDENCE_REQUIRED");
                    foreach (var r in refs)
                    {
                        var alias = idToAlias.TryGetValue(r, out var a) ? a : r;
                        if (!shownAliases.Contains(alias)) return Fail("EVIDENCE_NOT_SHOWN");
                    }
                }
                var parent = Str(it, "parentId");
                var cont = Str(it, "continuesFromId");
                if (parent is not null && (!shown.Contains(parent) || parent == id)) return Fail("PARENT_NOT_SHOWN");
                if (cont is not null && (!shown.Contains(cont) || cont == id)) return Fail("CONTINUES_NOT_SHOWN");
                items.Add(new ParsedItem(id, decision, refs, parent, cont));
            }
            var passUp = new List<string>();
            if (root.TryGetProperty("passUp", out var pu))
            {
                if (pu.ValueKind != JsonValueKind.Array || pu.GetArrayLength() > passUpCap) return Fail("PASSUP_INVALID_OR_OVER_CAP");
                foreach (var x in pu.EnumerateArray())
                {
                    var id = x.GetString() ?? "";
                    if (!shown.Contains(id)) return Fail("PASSUP_NOT_SHOWN");
                    passUp.Add(id);
                }
            }
            if (kind == Kind.Local && items.Count < revisable.Count) return Fail("LOCAL_NODE_UNDECIDED");
            return new ParsedNode(items, passUp.Distinct(StringComparer.Ordinal).ToList(), null);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Payloads: the same contract for every node; the instruction is data and never read by code.

    /// <summary>Order of items inside a prompt. NORMAL keeps reading order. REVERSED and SEEDED:n permute the same items; the content and ids
    /// are unchanged, so a difference in decisions can only come from position. Used by the position-sensitivity probe.</summary>
    public static IEnumerable<T> Order<T>(IEnumerable<T> xs, string mode)
    {
        var list = xs.ToList();
        if (mode == "NORMAL") return list;
        if (mode == "REVERSED") { list.Reverse(); return list; }
        if (mode.StartsWith("SEEDED:", StringComparison.Ordinal))
        {
            var rng = new Random(int.Parse(mode["SEEDED:".Length..]));
            return list.OrderBy(_ => rng.Next()).ToList();
        }
        throw new ArgumentException("PROMPT_ORDER_UNKNOWN:" + mode);
    }

    /// <summary>Position-sensitivity probe: how many candidates changed decision, parent, continuation or evidence between two runs of
    /// the same document, the same tree and the same content, differing only in prompt order.</summary>
    public static (int Decisions, int Parents, int Continuations, int Evidence, int Compared) SensitivityDiff(RunResult a, RunResult b)
    {
        int dec = 0, par = 0, con = 0, ev = 0; int n = 0;
        foreach (var kv in a.Final)
        {
            if (!b.Final.TryGetValue(kv.Key, out var y)) continue;
            n++;
            var x = kv.Value;
            if (x.Decision != y.Decision) dec++;
            if (x.ParentId != y.ParentId) par++;
            if (x.ContinuesFromId != y.ContinuesFromId) con++;
            if (!(x.Evidence ?? []).SequenceEqual(y.Evidence ?? [])) ev++;
        }
        return (dec, par, con, ev, n);
    }

    static string Payload(string nodeId, Kind kind, int level, Options o, string instructionSha, object overview, IReadOnlyDictionary<string, string> ids,
        List<(Cand Cand, string? Note)>? owned, List<Cand>? context, Dictionary<string, State>? states, IEnumerable<string> shownOrOwnedIds, string[]? children,
        IReadOnlyDictionary<string, Cand> textByAlias)
    {
        var ownedIds = shownOrOwnedIds.ToList();
        var aliasById = ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        if (owned is not null)
        {
            return JsonSerializer.Serialize(new
            {
                protocolVersion = NodeVersion, nodeId, kind = kind.ToString(), level, instruction = o.Instruction, instructionSha256 = instructionSha,
                overview,
                owned = Order(owned, o.PromptOrder).Select(x => new { id = ids[x.Cand.Alias], sourceAlias = x.Cand.Alias, page = x.Cand.Page, text = x.Cand.Text,
                    current = states is not null && states.TryGetValue(ids[x.Cand.Alias], out var s) ? s.Decision : "NONE" }).ToArray(),
                context = (context ?? []).Select(c => new { id = ids[c.Alias], sourceAlias = c.Alias, page = c.Page, text = c.Text, role = "CONTEXT_ONLY" }).ToArray(),
            });
        }
        // Synthesis and global nodes: shown ids with their current state, the children they came from, and structural pair observations.
        var shown = ownedIds.Select(id => (Id: id, Alias: aliasById[id])).ToArray();
        var pairs = shown.GroupBy(x => P7F1V6Tools.Normalize(textByAlias[x.Alias].Text)).Where(g => g.Count() > 1)
            .Select(g => g.Select(x => new { id = x.Id, sourceAlias = x.Alias }).ToArray()).ToArray();
        return JsonSerializer.Serialize(new
        {
            protocolVersion = NodeVersion, nodeId, kind = kind.ToString(), level, instruction = o.Instruction, instructionSha256 = instructionSha,
            overview, children = children ?? [],
            shown = Order(shown, o.PromptOrder).Select(x => new { id = x.Id, sourceAlias = x.Alias, page = textByAlias[x.Alias].Page, text = textByAlias[x.Alias].Text,
                current = states is not null && states.TryGetValue(x.Id, out var s) ? s.Decision : "NONE" }).ToArray(),
            pairObservations = pairs,
        });
    }

    // ------------------------------------------------------------------------------------------------------------------

    public static Dictionary<string, string> AssignIds(IEnumerable<string> aliases)
    {
        const string letters = "bcdfghjkmnpqrstvwxz";
        var map = new Dictionary<string, string>(StringComparer.Ordinal); var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in aliases.Distinct(StringComparer.Ordinal))
            for (int salt = 0; ; salt++)
            {
                var h = SHA256.HashData(Encoding.UTF8.GetBytes($"P7-F1-V6-HIER-ID|{s}|{salt}"));
                var id = new string(Enumerable.Range(0, 4).Select(i => letters[h[i] % letters.Length]).ToArray());
                if (used.Add(id)) { map[s] = id; break; }
            }
        return map;
    }

    static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
