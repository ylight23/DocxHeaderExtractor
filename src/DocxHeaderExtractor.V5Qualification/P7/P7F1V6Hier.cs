using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7-F1 V6 hierarchical semantic reconciliation, D2.1 (Issue #9, offline). Qwen makes every semantic decision at every node.
/// Local nodes decide their owned occurrences. Reconcile nodes confirm them. Synthesis and global nodes confirm or revise every
/// occurrence they were shown, and may ask for retrieval: a search over the whole-document registry, or reopening a specific
/// id, including ids that a local node decided NOT_SELECT. Retrieved text comes back to Qwen as evidence for the next round.
/// C# only forms shards and groups by position and count, looks up ids and text the model asked for, checks identity, evidence,
/// scope, budgets and completeness, and records provenance. C# never selects a heading, never derives a hierarchy and never
/// resolves a conflict. Dropping an id from passUp removes only its upward visibility; it never changes its decision.
/// </summary>
internal static class P7F1V6Hier
{
    public const string Version = "P7_F1_V6_HIER_V2";
    public const string NodeVersion = "P7_F1_V6_HIER_NODE_V2";
    public const int MaxEvidenceRefs = 8, MaxSearchChars = 200, TextPreviewChars = 400;
    public static readonly string[] Decisions = ["SELECT", "NOT_SELECT", "UNRESOLVED"];
    public static readonly string[] TechnicalStatuses = ["COMPLETE", "RETRIEVE", "INCOMPLETE", "CLARIFICATION_REQUIRED", "NOT_ANSWERABLE"];

    public enum Kind { Local, Reconcile, Synthesis, Global }

    public sealed record Cand(string Alias, int Page, string Text);

    /// <summary>Owned candidates decide; Before/After are context only (read-only, never revisable by this shard).</summary>
    public sealed record Shard(int Index, IReadOnlyList<Cand> Owned, IReadOnlyList<Cand> Before, IReadOnlyList<Cand> After);

    public static IReadOnlyList<Shard> Shards(IReadOnlyList<Cand> ordered, int size, int context)
    {
        var list = new List<Shard>();
        for (int start = 0, idx = 0; start < ordered.Count; start += size, idx++)
        {
            var owned = ordered.Skip(start).Take(size).ToArray();
            var before = ordered.Skip(Math.Max(0, start - context)).Take(Math.Min(context, start)).ToArray();
            var after = ordered.Skip(start + owned.Length).Take(context).ToArray();
            list.Add(new Shard(idx, owned, before, after));
        }
        return list;
    }

    /// <summary>The state of one candidate in the registry: decision, the node that set it, its references, and how many later nodes confirmed it.</summary>
    public sealed record State(string Decision, string SourceNode, string SourceOutputSha256, string? ParentId, string? ContinuesFromId,
        int Confirmations, IReadOnlyList<string>? Evidence = null);

    public sealed record NodeRecord(string NodeId, string Kind, int Level, string InputSha256, int InputBytes, string? OutputSha256,
        int OutputBytes, string Status, string? FailureCode, int Revised, int PassUp);

    public sealed record RunResult(string RunStatus, IReadOnlyDictionary<string, State> Final, IReadOnlyList<NodeRecord> Nodes,
        IReadOnlyList<string> Violations, string TranscriptSha256, IReadOnlyDictionary<string, string> IdByAlias, P7F1V6RawStore Raw,
        int OpenUnresolved, bool GlobalAccepted, int GlobalShown);

    /// <summary>The scripted model: returns the node's raw response text for a node payload.</summary>
    public delegate string NodeModel(string nodeId, Kind kind, string payloadJson);

    /// <summary>
    /// OutputBudgetBytes bounds the RAW bytes of any accepted node response. MaxInputBytes bounds every request. Retrieval limits bound
    /// the rounds and the reopened ids of one node. PassUpCap bounds what a synthesis node passes up (mandatory ids are checked
    /// against it, never added after the check).
    /// </summary>
    public sealed record Options(string Instruction, string SourceSha256, int ShardSize, int Context, int FanIn, int PassUpCap,
        string? RawDir = null, int OutputBudgetBytes = 16384, string PromptOrder = "NORMAL", int MaxInputBytes = 131072,
        int MaxRetrievalRounds = 2, int MaxSearchResults = 20, int MaxReopen = 8);

    static void Validate(Options o)
    {
        if (o.ShardSize < 1) throw new ArgumentOutOfRangeException(nameof(o.ShardSize), "ShardSize must be at least 1");
        if (o.FanIn < 2) throw new ArgumentOutOfRangeException(nameof(o.FanIn), "FanIn must be at least 2 or the tree never shrinks");
        if (o.Context < 0) throw new ArgumentOutOfRangeException(nameof(o.Context));
        if (o.PassUpCap < 1) throw new ArgumentOutOfRangeException(nameof(o.PassUpCap));
        if (o.OutputBudgetBytes < 1024) throw new ArgumentOutOfRangeException(nameof(o.OutputBudgetBytes));
        if (o.MaxInputBytes < 1024) throw new ArgumentOutOfRangeException(nameof(o.MaxInputBytes));
        if (o.MaxRetrievalRounds < 0 || o.MaxSearchResults < 1 || o.MaxReopen < 1) throw new ArgumentOutOfRangeException(nameof(o.MaxRetrievalRounds));
    }

    /// <summary>Per-run state shared by all nodes.</summary>
    sealed class Env
    {
        public required Options O { get; init; }
        public required Dictionary<string, string> Ids { get; init; }
        public required Dictionary<string, string> IdToAlias { get; init; }
        public required Dictionary<string, Cand> ByAlias { get; init; }
        public required IReadOnlyList<Cand> Ordered { get; init; }
        public required object Overview { get; init; }
        public required string InstructionSha { get; init; }
        public required P7F1V6RawStore Raw { get; init; }
        public Dictionary<string, State> States { get; } = new(StringComparer.Ordinal);
        public List<NodeRecord> Nodes { get; } = new();
        public List<string> Violations { get; } = new();
        public List<string> Transcript { get; } = new();
        public string? StopReason { get; set; }
        public bool Stopped => StopReason is not null;
        public bool GlobalAccepted { get; set; }
        public int GlobalShown { get; set; }
    }

    /// <summary>What a node has been shown so far: its own ids, the retrieval results, and the aliases that may be cited as evidence.</summary>
    sealed class Shown
    {
        public List<string> Ids { get; } = new();                          // revisable in this node (base, then reopened)
        public HashSet<string> Revisable { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Citable { get; } = new(StringComparer.Ordinal); // aliases the model was shown (any role)
        public List<object> Searches { get; } = new();
        public List<object> Reopened { get; } = new();
        public int ReopenedCount => Reopened.Count;
    }

    public static RunResult Run(IReadOnlyList<Cand> ordered, Options o, NodeModel model)
    {
        Validate(o);
        var ids = AssignIds(ordered.Select(c => c.Alias));
        var env = new Env
        {
            O = o, Ids = ids, IdToAlias = ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal),
            ByAlias = ordered.ToDictionary(c => c.Alias, c => c, StringComparer.Ordinal), Ordered = ordered,
            Overview = P7F1V6Core.Overview(ordered.Select(c => new F1QSourceLine(c.Alias, 0, c.Page, c.Text, null, null)).ToArray()),
            InstructionSha = P7F1V6Core.InstructionSha256(o.Instruction), Raw = new P7F1V6RawStore(o.RawDir),
        };
        var shards = Shards(ordered, o.ShardSize, o.Context);

        // ---- level 0: a local pass decides the owned ids; a reconcile pass confirms them ----
        var units = new List<(string Name, List<string> Subtree, List<string> Up)>();
        foreach (var sh in shards)
        {
            if (env.Stopped) break;
            var owned = sh.Owned.Select(c => ids[c.Alias]).ToList();
            var ctxAliases = sh.Before.Concat(sh.After).Select(c => c.Alias).ToList();
            var local = $"L0-S{sh.Index:D4}";
            RunLocal(env, local, Kind.Local, 0, owned, sh.Before.Concat(sh.After).ToList(), model);
            var rec = $"L0R-S{sh.Index:D4}";
            RunLocal(env, rec, Kind.Reconcile, 0, owned, sh.Before.Concat(sh.After).ToList(), model);
            units.Add((local, owned, Forward(owned, env.States, o.Context)));
        }

        // ---- synthesis levels: group by count; each node sees the passUp lists of its children ----
        int level = 1;
        while (units.Count > 1 && !env.Stopped)
        {
            var next = new List<(string Name, List<string> Subtree, List<string> Up)>();
            for (int g = 0; g < units.Count && !env.Stopped; g += o.FanIn)
            {
                var children = units.Skip(g).Take(o.FanIn).ToList();
                var name = $"L{level}-N{next.Count + 1:D4}";
                var baseShown = children.SelectMany(c => c.Up).Distinct(StringComparer.Ordinal).ToList();
                var up = RunSynth(env, name, Kind.Synthesis, level, baseShown, model);
                next.Add((name, children.SelectMany(c => c.Subtree).ToList(), up));
            }
            units = next;
            level++;
        }

        // ---- global node over the root's passUp set ----
        if (units.Count == 1 && !env.Stopped)
            RunSynth(env, "GLOBAL", Kind.Global, level, units[0].Up, model);

        var open = env.Ids.Values.Count(id => !env.States.TryGetValue(id, out var s) || s.Decision == "UNRESOLVED");
        string status = env.StopReason is not null ? "STOPPED_" + env.StopReason
            : env.Violations.Count > 0 ? "COMPLETED_WITH_VIOLATIONS"
            : open > 0 ? "COMPLETED_OPEN_UNRESOLVED"
            : !env.GlobalAccepted ? "NO_GLOBAL_REVIEW"
            : "RESOLVED";
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", env.Transcript))));
        return new RunResult(status, env.States, env.Nodes, env.Violations, sha, ids, env.Raw, open, env.GlobalAccepted, env.GlobalShown);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Local and reconcile nodes: no retrieval; the owned ids come from the shard, and the context is read-only.

    static void RunLocal(Env e, string nodeId, Kind kind, int level, List<string> owned, List<Cand> context, NodeModel model)
    {
        var shown = new Shown();
        foreach (var id in owned) { shown.Ids.Add(id); shown.Revisable.Add(id); shown.Citable.Add(e.IdToAlias[id]); }
        foreach (var c in context) shown.Citable.Add(c.Alias);
        var payload = LocalPayload(e, nodeId, kind, level, owned, context);
        Exec(e, nodeId, kind, level, shown, _ => payload, model, allowRetrieve: false, passUpCap: int.MaxValue, owned: owned);
    }

    static string LocalPayload(Env e, string nodeId, Kind kind, int level, List<string> owned, List<Cand> context)
    {
        var o = e.O;
        var ownedCands = owned.Select(id => (Id: id, C: e.ByAlias[e.IdToAlias[id]])).ToArray();
        return PayloadJson(new
        {
            protocolVersion = NodeVersion, nodeId, kind = kind.ToString(), level, instruction = o.Instruction, instructionSha256 = e.InstructionSha,
            overview = e.Overview,
            owned = Order(ownedCands, o.PromptOrder).Select(x => new { id = x.Id, sourceAlias = x.C.Alias, page = x.C.Page, text = x.C.Text,
                current = e.States.TryGetValue(x.Id, out var s) ? s.Decision : "NONE" }).ToArray(),
            context = context.Select(c => new { id = e.Ids[c.Alias], sourceAlias = c.Alias, page = c.Page, text = c.Text, role = "CONTEXT_ONLY" }).ToArray(),
        });
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Synthesis and global nodes: retrieval allowed; the node confirms or revises every id it was shown.

    static List<string> RunSynth(Env e, string nodeId, Kind kind, int level, List<string> baseShown, NodeModel model)
    {
        var shown = new Shown();
        foreach (var id in baseShown) { shown.Ids.Add(id); shown.Revisable.Add(id); shown.Citable.Add(e.IdToAlias[id]); }
        if (kind == Kind.Global) e.GlobalShown = baseShown.Count;
        var up = Exec(e, nodeId, kind, level, shown,
            rs => SynthPayload(e, nodeId, kind, level, rs), model, allowRetrieve: true, passUpCap: e.O.PassUpCap, owned: null);
        return up ?? Mandatory(e, baseShown);
    }

    static string SynthPayload(Env e, string nodeId, Kind kind, int level, Shown rs)
    {
        var o = e.O;
        var shownCands = rs.Ids.Select(id => (Id: id, C: e.ByAlias[e.IdToAlias[id]])).ToArray();
        var pairs = shownCands.GroupBy(x => P7F1V6Tools.Normalize(x.C.Text)).Where(g => g.Count() > 1)
            .Select(g => g.Select(x => new { id = x.Id, sourceAlias = x.C.Alias }).ToArray()).ToArray();
        return PayloadJson(new
        {
            protocolVersion = NodeVersion, nodeId, kind = kind.ToString(), level, instruction = o.Instruction, instructionSha256 = e.InstructionSha,
            overview = e.Overview,
            shown = Order(shownCands, o.PromptOrder).Select(x => new { id = x.Id, sourceAlias = x.C.Alias, page = x.C.Page, text = x.C.Text,
                current = e.States.TryGetValue(x.Id, out var s) ? s.Decision : "NONE" }).ToArray(),
            pairObservations = pairs,
            retrieved = new { searches = rs.Searches, reopened = rs.Reopened },
        });
    }

    // ------------------------------------------------------------------------------------------------------------------
    // One node: model calls, retrieval rounds, binding, and the explicit stop cases. Returns the passUp list when accepted, null otherwise.

    static List<string>? Exec(Env e, string nodeId, Kind kind, int level, Shown rs, Func<Shown, string> build, NodeModel model,
        bool allowRetrieve, int passUpCap, List<string>? owned)
    {
        for (int round = 0; ; round++)
        {
            var payload = build(rs);
            var inBytes = Encoding.UTF8.GetBytes(payload);
            var inSha = e.Raw.Put(inBytes);
            if (inBytes.Length > e.O.MaxInputBytes) { e.StopReason = "INPUT_OVER_BUDGET:" + nodeId; return null; }
            var response = model(nodeId, kind, payload);
            var outBytes = Encoding.UTF8.GetBytes(response);
            var outSha = e.Raw.Put(outBytes);
            e.Transcript.Add($"{nodeId}|r{round}|PAYLOAD|{inSha}|{inBytes.Length}");
            e.Transcript.Add($"{nodeId}|r{round}|OUTPUT|{outSha}|{outBytes.Length}");

            string? fail = null;
            ParseOut? po = null;
            if (outBytes.Length > e.O.OutputBudgetBytes) fail = "OUTPUT_OVER_BUDGET";
            else
            {
                po = Parse(response, nodeId, kind, rs, owned, allowRetrieve, e);
                fail = po.Failure;
            }

            if (fail is null && po!.Retrieve is { } r)
            {
                if (round >= e.O.MaxRetrievalRounds) fail = "RETRIEVAL_ROUNDS_EXCEEDED";
                else
                {
                    fail = ApplyRetrieval(e, rs, r);
                    if (fail is null)
                    {
                        e.Nodes.Add(new NodeRecord(nodeId, kind.ToString(), level, inSha, inBytes.Length, outSha, outBytes.Length, "RETRIEVE", null, 0, 0));
                        continue;
                    }
                }
            }
            if (fail is not null)
            {
                e.Violations.Add($"{nodeId}:{fail}");
                e.Nodes.Add(new NodeRecord(nodeId, kind.ToString(), level, inSha, inBytes.Length, outSha, outBytes.Length, "REJECTED", fail, 0, 0));
                return null;
            }

            // COMPLETE: the mandatory passUp rule is checked BEFORE anything changes, and never adds ids after the check.
            var pass = po!.Complete!;
            var mandatory = kind is Kind.Synthesis or Kind.Global ? Mandatory(e, rs.Ids) : new List<string>();
            var merged = pass.PassUp.Concat(mandatory).Distinct(StringComparer.Ordinal).ToList();
            if (kind is Kind.Synthesis or Kind.Global && merged.Count > passUpCap)
            {
                e.StopReason = "PASSUP_OVER_CAP:" + nodeId;
                e.Nodes.Add(new NodeRecord(nodeId, kind.ToString(), level, inSha, inBytes.Length, outSha, outBytes.Length, "STOPPED", "PASSUP_OVER_CAP", 0, merged.Count));
                return null;
            }
            foreach (var it in pass.Items)
            {
                var prev = e.States.TryGetValue(it.Id, out var p) ? p : null;
                e.States[it.Id] = new State(it.Decision, nodeId, outSha, it.Parent, it.Continues, prev?.Confirmations ?? 0, it.Refs);
            }
            foreach (var id in pass.Confirmed)
                if (e.States.TryGetValue(id, out var s)) e.States[id] = s with { Confirmations = s.Confirmations + 1 };
            if (kind == Kind.Global) e.GlobalAccepted = true;
            e.Nodes.Add(new NodeRecord(nodeId, kind.ToString(), level, inSha, inBytes.Length, outSha, outBytes.Length, "ACCEPTED", null,
                pass.Items.Count + pass.Confirmed.Count, merged.Count));
            return merged;
        }
    }

    /// <summary>Unresolved ids shown to the node and its boundary windows: passed up by rule, never dropped.</summary>
    static List<string> Mandatory(Env e, IReadOnlyList<string> shownOrdered)
    {
        int k = e.O.Context;
        var boundary = shownOrdered.Take(k).Concat(shownOrdered.TakeLast(k)).ToHashSet(StringComparer.Ordinal);
        return shownOrdered.Where(id => boundary.Contains(id) || (e.States.TryGetValue(id, out var s) && s.Decision == "UNRESOLVED")).ToList();
    }

    /// <summary>What a unit passes to the next level, by rule: SELECT and UNRESOLVED ids plus the boundary windows. Structural, never semantic.</summary>
    static List<string> Forward(IReadOnlyList<string> owned, Dictionary<string, State> states, int k)
    {
        var boundary = owned.Take(k).Concat(owned.TakeLast(k)).ToHashSet(StringComparer.Ordinal);
        return owned.Where(id => boundary.Contains(id) || (states.TryGetValue(id, out var s) && s.Decision is "SELECT" or "UNRESOLVED")).ToList();
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Retrieval: a registry search or a reopen by id. Both are lookups the model asked for; C# does not judge the results.

    static string? ApplyRetrieval(Env e, Shown rs, Retrieve r)
    {
        if (r.Search is { } q)
        {
            var needle = P7F1V6Tools.Normalize(q);
            var hits = e.Ordered.Where(c => P7F1V6Tools.Normalize(c.Text).Contains(needle, StringComparison.Ordinal)).Take(e.O.MaxSearchResults).ToArray();
            var rows = hits.Select(c => new { id = e.Ids[c.Alias], sourceAlias = c.Alias, page = c.Page,
                decision = e.States.TryGetValue(e.Ids[c.Alias], out var s) ? s.Decision : "NONE", text = Preview(c.Text) }).ToArray();
            rs.Searches.Add(new { query = q, shown = rows.Length, matches = rows });
            foreach (var c in hits) rs.Citable.Add(c.Alias);   // shown to the model, so citable as evidence
        }
        foreach (var id in r.Reopen)
        {
            if (!e.IdToAlias.ContainsKey(id)) return "ID_NOT_IN_REGISTRY";
            if (rs.Revisable.Contains(id)) continue;
            if (rs.ReopenedCount >= e.O.MaxReopen) return "REOPEN_LIMIT";
            var alias = e.IdToAlias[id];
            var i = e.Ordered.ToList().FindIndex(c => c.Alias == alias);
            var neigh = e.Ordered.Skip(Math.Max(0, i - 2)).Take(5).Where(c => c.Alias != alias).ToArray();
            foreach (var n in neigh) rs.Citable.Add(n.Alias);
            var prev = e.States.TryGetValue(id, out var s) ? s : null;
            rs.Reopened.Add(new
            {
                id, sourceAlias = alias, page = e.ByAlias[alias].Page, text = e.ByAlias[alias].Text,
                decision = prev?.Decision ?? "NONE", sourceNode = prev?.SourceNode,
                neighbours = neigh.Select(n => new { id = e.Ids[n.Alias], sourceAlias = n.Alias, page = n.Page, text = Preview(n.Text),
                    decision = e.States.TryGetValue(e.Ids[n.Alias], out var ns) ? ns.Decision : "NONE", role = "CONTEXT_ONLY" }).ToArray(),
            });
            rs.Ids.Add(id); rs.Revisable.Add(id); rs.Citable.Add(alias);
        }
        return null;
    }

    static string Preview(string t) => t.Length <= TextPreviewChars ? t : t[..TextPreviewChars];

    // ------------------------------------------------------------------------------------------------------------------
    // Parsing: the model's response is checked against what it was shown. Nothing is interpreted semantically.

    public sealed record Item(string Id, string Decision, IReadOnlyList<string> Refs, string? Parent, string? Continues);
    public sealed record Complete(IReadOnlyList<Item> Items, IReadOnlyList<string> Confirmed, List<string> PassUp);
    public sealed record Retrieve(string? Search, IReadOnlyList<string> Reopen);
    public sealed record ParseOut(Complete? Complete, Retrieve? Retrieve, string? Failure);

    static ParseOut Parse(string response, string nodeId, Kind kind, Shown rs, List<string>? owned, bool allowRetrieve, Env e)
    {
        ParseOut Fail(string code) => new(null, null, code);
        // Canonical text only: a unicode escape can stand for an ASCII character six times over, which the bound does not allow.
        if (response.Contains("\\u", StringComparison.Ordinal)) return Fail("ESCAPED_UNICODE_NOT_ALLOWED");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(response); } catch (JsonException) { return Fail("RESPONSE_NOT_JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("ROOT_NOT_OBJECT");
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            string[] required = ["protocolVersion", "nodeId", "status"];
            string[] allowed = ["protocolVersion", "nodeId", "status", "items", "confirmed", "passUp", "retrieve"];
            if (required.Any(k => !names.Contains(k, StringComparer.Ordinal)) || names.Any(n => !allowed.Contains(n, StringComparer.Ordinal))) return Fail("ROOT_KEYS_INVALID");
            if (Str(root, "protocolVersion") != NodeVersion) return Fail("PROTOCOL_VERSION_INVALID");
            if (Str(root, "nodeId") != nodeId) return Fail("NODE_ID_MISMATCH");
            var status = Str(root, "status");
            if (status is null || !TechnicalStatuses.Contains(status, StringComparer.Ordinal)) return Fail("STATUS_INVALID");

            if (status == "RETRIEVE")
            {
                if (!allowRetrieve) return Fail("RETRIEVE_NOT_ALLOWED");
                if (!root.TryGetProperty("retrieve", out var rt) || rt.ValueKind != JsonValueKind.Object) return Fail("RETRIEVE_MISSING");
                if (root.TryGetProperty("items", out var its) && its.GetArrayLength() > 0) return Fail("RETRIEVE_WITH_ITEMS");
                var search = Str(rt, "search");
                if (search is not null && (search.Length == 0 || search.Length > MaxSearchChars)) return Fail("SEARCH_INVALID");
                var reopen = new List<string>();
                if (rt.TryGetProperty("reopen", out var ro))
                {
                    if (ro.ValueKind != JsonValueKind.Array || ro.GetArrayLength() > e.O.MaxReopen || ro.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                        return Fail("REOPEN_INVALID");
                    reopen.AddRange(ro.EnumerateArray().Select(x => x.GetString()!));
                }
                if (search is null && reopen.Count == 0) return Fail("RETRIEVE_EMPTY");
                return new(null, new Retrieve(search, reopen), null);
            }
            if (status != "COMPLETE") return Fail("NODE_" + status + "_CHANGES_NOTHING");

            // Visibility for this node: revisable ids are the owned ids (local, reconcile) or every shown id (synthesis, global).
            var revisable = owned is not null ? owned.ToHashSet(StringComparer.Ordinal) : rs.Revisable;
            var items = new List<Item>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("items", out var itemsEl))
            {
                if (itemsEl.ValueKind != JsonValueKind.Array) return Fail("ITEMS_NOT_ARRAY");
                foreach (var it in itemsEl.EnumerateArray())
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
                            var alias = e.IdToAlias.TryGetValue(r, out var a) ? a : r;
                            if (!rs.Citable.Contains(alias)) return Fail("EVIDENCE_NOT_SHOWN");
                        }
                    }
                    var parent = Str(it, "parentId");
                    var cont = Str(it, "continuesFromId");
                    if (parent is not null && (parent == id || !IsShown(e, rs, parent))) return Fail("PARENT_NOT_SHOWN");
                    if (cont is not null && (cont == id || !IsShown(e, rs, cont))) return Fail("CONTINUES_NOT_SHOWN");
                    items.Add(new Item(id, decision, refs, parent, cont));
                }
            }
            var confirmed = new List<string>();
            if (root.TryGetProperty("confirmed", out var cf))
            {
                if (cf.ValueKind != JsonValueKind.Array) return Fail("CONFIRMED_NOT_ARRAY");
                foreach (var x in cf.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.String) return Fail("ID_NOT_STRING");
                    var id = x.GetString()!;
                    if (!revisable.Contains(id)) return Fail("ID_NOT_REVISABLE_IN_THIS_NODE");
                    if (!seen.Add(id)) return Fail("ID_LISTED_TWICE");
                    confirmed.Add(id);
                }
            }
            // Coverage: a local node decides every owned id; every other node confirms or revises every id it was shown.
            if (kind == Kind.Local && items.Count < revisable.Count) return Fail("LOCAL_NODE_UNDECIDED");
            if (kind is Kind.Reconcile or Kind.Synthesis or Kind.Global)
            {
                var uncovered = revisable.Count(id => !seen.Contains(id));
                if (uncovered > 0) return Fail("UNCONFIRMED_SHOWN_IDS:" + uncovered);
            }
            var passUp = new List<string>();
            if (root.TryGetProperty("passUp", out var pu))
            {
                if (pu.ValueKind != JsonValueKind.Array) return Fail("PASSUP_NOT_ARRAY");
                foreach (var x in pu.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.String) return Fail("ID_NOT_STRING");
                    var id = x.GetString()!;
                    if (!rs.Revisable.Contains(id)) return Fail("PASSUP_NOT_SHOWN");
                    passUp.Add(id);
                }
            }
            return new(new Complete(items, confirmed, passUp.Distinct(StringComparer.Ordinal).ToList()), null, null);
        }
    }

    /// <summary>Order of items inside a prompt. NORMAL keeps reading order. REVERSED and SEEDED:n permute the same items.</summary>
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

    /// <summary>Position-sensitivity probe: how many candidates changed decision, parent, continuation or evidence between two runs.</summary>
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

    /// <summary>Payloads keep Vietnamese text as UTF-8, not as unicode escapes: an escape the model copies back is refused by the response rule.</summary>
    static readonly JsonSerializerOptions PayloadOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string PayloadJson(object value) => JsonSerializer.Serialize(value, PayloadOptions);

    /// <summary>A structural reference is valid only for an id the node was shown, context and retrieved ids included.</summary>
    static bool IsShown(Env e, Shown rs, string id) => e.IdToAlias.TryGetValue(id, out var alias) && rs.Citable.Contains(alias);

    static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
