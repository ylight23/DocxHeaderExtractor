using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7-F1 V6 hierarchical semantic reconciliation, D2.3 (Issue #9, offline). Qwen makes every semantic decision at every node.
/// Local nodes decide their owned occurrences. Reconcile nodes confirm them. Synthesis and global nodes confirm or revise every
/// occurrence they were shown, and may ask for retrieval: a page of a search over the whole-document registry, or reopening a
/// specific id, including ids that a local node decided NOT_SELECT. Retrieved text and layout come back to Qwen as evidence.
/// C# only forms shards and groups by position and count, looks up ids and text the model asked for, checks identity, evidence,
/// scope, budgets, manifest and completeness, and records provenance. C# never selects a heading, never derives a hierarchy and
/// never resolves a conflict. Dropping an id from passUp removes only its upward visibility; it never changes its decision.
/// </summary>
internal static class P7F1V6Hier
{
    public const string Version = "P7_F1_V6_HIER_V3";
    public const string NodeVersion = "P7_F1_V6_HIER_NODE_V2";
    public const int MaxEvidenceRefs = 8, MaxSearchChars = 200, TextPreviewChars = 400;
    public static readonly string[] Decisions = ["SELECT", "NOT_SELECT", "UNRESOLVED"];
    public static readonly string[] TechnicalStatuses = ["COMPLETE", "RETRIEVE", "INCOMPLETE", "CLARIFICATION_REQUIRED", "NOT_ANSWERABLE"];

    public enum Kind { Local, Reconcile, Synthesis, Global }

    /// <summary>One source line. Layout is the compact geometry and typography of the line, or null when the source has none.</summary>
    public sealed record Cand(string Alias, int Page, string Text, object? Layout = null);

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

    /// <summary>Protocol-level result, reported separately so that one word cannot stand for all of it.</summary>
    /// <summary>EvidenceRefsPresent means each decided occurrence names an evidence reference; it is not a claim that the cited source,
    /// or the whole source, was shown to the model. PresentedOccurrences counts occurrences that appeared in some payload as text.
    /// UnpagedSearches counts (node, query) pairs with unread matches at node completion; UnreadMatches sums their unread matches.</summary>
    public sealed record RunSummary(string ProtocolStatus, bool ProtocolResolved, bool EvidenceRefsPresent, int Occurrences, int Decided,
        int PresentedOccurrences, double PresentedShare, int GlobalReviewed, double GlobalReviewCoverage, int AcceptedNodes, int RetrievalTurns,
        int RejectedNodes, int StoppedNodes, int SearchPagesWithMore, int UnpagedSearches, int UnreadMatches, bool CandidatesPinned,
        string CandidatesSha256, string ManifestSha256);

    public sealed record RunResult(string RunStatus, IReadOnlyDictionary<string, State> Final, IReadOnlyList<NodeRecord> Nodes,
        IReadOnlyList<string> Violations, string TranscriptSha256, IReadOnlyDictionary<string, string> IdByAlias, P7F1V6RawStore Raw,
        int OpenUnresolved, bool GlobalAccepted, int GlobalShown, IReadOnlySet<string> ReopenedIds, IReadOnlySet<string> GlobalReviewedIds,
        RunSummary Summary);

    /// <summary>
    /// Provenance accounting, a partition of the final states. Source: LocalResolved (decided or revised by a local or reconcile node, not
    /// reopened), ReopenedConfirmed (reopened, then confirmed without change), ReopenedResolved (reopened, then revised by a synthesis
    /// or global node), SynthesisResolved (revised by a synthesis or global node without reopening). Global: GloballyReviewed (shown to
    /// the global node, which was accepted) and CarriedWithoutGlobalReview (every other final state).
    /// </summary>
    public sealed record Accounting(int LocalResolved, int ReopenedConfirmed, int ReopenedResolved, int SynthesisResolved,
        int GloballyReviewed, int CarriedWithoutGlobalReview, int Total);

    public static Accounting Account(RunResult r)
    {
        int local = 0, reopenedConf = 0, reopenedRes = 0, synth = 0, global = 0;
        foreach (var kv in r.Final)
        {
            var reopened = r.ReopenedIds.Contains(kv.Key);
            if (kv.Value.SourceNode.StartsWith("L0", StringComparison.Ordinal))
            {
                if (reopened) reopenedConf++; else local++;
            }
            else if (reopened) reopenedRes++;
            else synth++;
            if (r.GlobalReviewedIds.Contains(kv.Key)) global++;
        }
        return new Accounting(local, reopenedConf, reopenedRes, synth, global, r.Final.Count - global, r.Final.Count);
    }

    /// <summary>The scripted model: returns the node's raw response text for a node payload.</summary>
    public delegate string NodeModel(string nodeId, Kind kind, string payloadJson);

    /// <summary>
    /// OutputBudgetBytes bounds the RAW bytes of any accepted node response. MaxInputBytes bounds every request. Retrieval limits bound
    /// the rounds, the reopened ids and the search page size of one node. PassUpCap bounds what a synthesis node passes up (mandatory ids
    /// are checked against it, never added after the check). The manifest fields identify the source; they are part of every payload.
    /// </summary>
    public sealed record Options(string Instruction, string SourceSha256, int ShardSize, int Context, int FanIn, int PassUpCap,
        string? RawDir = null, int OutputBudgetBytes = 16384, string PromptOrder = "NORMAL", int MaxInputBytes = 131072,
        int MaxRetrievalRounds = 2, int MaxSearchResults = 20, int MaxReopen = 8,
        string SourceUniverseSha256 = "", string ParserVersion = "", string EvidenceStoreVersion = "", string? ExpectedCandidatesSha256 = null);

    static void Validate(Options o)
    {
        if (o.ShardSize < 1) throw new ArgumentOutOfRangeException(nameof(o.ShardSize), "ShardSize must be at least 1");
        if (o.FanIn < 2) throw new ArgumentOutOfRangeException(nameof(o.FanIn), "FanIn must be at least 2 or the tree never shrinks");
        if (o.Context < 0) throw new ArgumentOutOfRangeException(nameof(o.Context));
        if (o.PassUpCap < 1) throw new ArgumentOutOfRangeException(nameof(o.PassUpCap));
        if (o.OutputBudgetBytes < 1024) throw new ArgumentOutOfRangeException(nameof(o.OutputBudgetBytes));
        if (o.MaxInputBytes < 1024) throw new ArgumentOutOfRangeException(nameof(o.MaxInputBytes));
        if (o.MaxRetrievalRounds < 0 || o.MaxSearchResults < 1 || o.MaxReopen < 1) throw new ArgumentOutOfRangeException(nameof(o.MaxRetrievalRounds));
        if (string.IsNullOrWhiteSpace(o.SourceSha256)) throw new ArgumentException("MANIFEST_INCOMPLETE:sourceSha256");
        if (string.IsNullOrWhiteSpace(o.SourceUniverseSha256)) throw new ArgumentException("MANIFEST_INCOMPLETE:sourceUniverseSha256");
        if (string.IsNullOrWhiteSpace(o.ParserVersion)) throw new ArgumentException("MANIFEST_INCOMPLETE:parserVersion");
        if (string.IsNullOrWhiteSpace(o.EvidenceStoreVersion)) throw new ArgumentException("MANIFEST_INCOMPLETE:evidenceStoreVersion");
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
        public required object Manifest { get; init; }
        public required P7F1V6RawStore Raw { get; init; }
        public Dictionary<string, State> States { get; } = new(StringComparer.Ordinal);
        public List<NodeRecord> Nodes { get; } = new();
        public List<string> Violations { get; } = new();
        public List<string> Transcript { get; } = new();
        public string? StopReason { get; set; }
        public bool Stopped => StopReason is not null;
        public bool GlobalAccepted { get; set; }
        public int GlobalShown { get; set; }
        public HashSet<string> Reopened { get; } = new(StringComparer.Ordinal);
        public HashSet<string> GlobalReviewed { get; } = new(StringComparer.Ordinal);
        public int SearchPagesWithMore { get; set; }
        public int UnpagedSearches { get; set; }
        public int UnreadMatches { get; set; }
        public HashSet<string> Presented { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Reading progress of one query in one node. ReadEnd is the contiguous prefix read so far; a page that starts beyond it is a gap,
    /// and everything after the gap stays counted as unread (conservative).</summary>
    sealed class Cursor
    {
        public int Total { get; set; }
        public int ReadEnd { get; set; }
        public bool Gap { get; set; }
    }

    /// <summary>What a node has been shown so far: its own ids, the retrieval results, and the aliases that may be cited as evidence.</summary>
    sealed class Shown
    {
        public List<string> Ids { get; } = new();                          // revisable in this node (base, then reopened)
        public HashSet<string> Revisable { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Citable { get; } = new(StringComparer.Ordinal); // aliases the model was shown (any role)
        public List<object> Searches { get; } = new();
        public List<object> Reopened { get; } = new();
        public Dictionary<string, Cursor> Cursors { get; } = new(StringComparer.Ordinal);  // one per query text, never shared between queries
        public int ReopenedCount => Reopened.Count;
    }

    public static RunResult Run(IReadOnlyList<Cand> ordered, Options o, NodeModel model)
    {
        Validate(o);
        var candidatesSha = CandidatesSha256(ordered);
        if (o.ExpectedCandidatesSha256 is not null && o.ExpectedCandidatesSha256 != candidatesSha)
            throw new ArgumentException("CANDIDATES_MISMATCH");
        var instructionSha = P7F1V6Core.InstructionSha256(o.Instruction);
        var manifest = new
        {
            protocolVersion = Version, sourceSha256 = o.SourceSha256, sourceUniverseSha256 = o.SourceUniverseSha256,
            parserVersion = o.ParserVersion, evidenceStoreVersion = o.EvidenceStoreVersion, candidatesSha256 = candidatesSha,
            candidateCount = ordered.Count, instructionSha256 = instructionSha,
        };
        var manifestSha = Sha(PayloadJson(manifest));
        var ids = AssignIds(ordered.Select(c => c.Alias));
        var env = new Env
        {
            O = o, Ids = ids, IdToAlias = ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal),
            ByAlias = ordered.ToDictionary(c => c.Alias, c => c, StringComparer.Ordinal), Ordered = ordered,
            Overview = P7F1V6Core.Overview(ordered.Select(c => new F1QSourceLine(c.Alias, 0, c.Page, c.Text, null, null)).ToArray()),
            InstructionSha = instructionSha, Manifest = manifest, Raw = new P7F1V6RawStore(o.RawDir),
        };
        var shards = Shards(ordered, o.ShardSize, o.Context);

        // ---- level 0: a local pass decides the owned ids; a reconcile pass confirms them ----
        var units = new List<(string Name, List<string> Subtree, List<string> Up)>();
        foreach (var sh in shards)
        {
            if (env.Stopped) break;
            var owned = sh.Owned.Select(c => ids[c.Alias]).ToList();
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
            : "PROTOCOL_RESOLVED";
        var sha = Sha(string.Join("\n", env.Transcript));
        var evidenceRefsPresent = open == 0 && env.States.Values.All(s => s.Decision == "UNRESOLVED" || (s.Evidence?.Count ?? 0) > 0);
        var globalReviewed = env.GlobalReviewed.Count(id => env.States.ContainsKey(id));
        var presented = env.Presented.Count;
        var summary = new RunSummary(status, status == "PROTOCOL_RESOLVED", evidenceRefsPresent, ids.Count, env.States.Count,
            presented, ids.Count == 0 ? 0 : presented / (double)ids.Count,
            globalReviewed, ids.Count == 0 ? 0 : globalReviewed / (double)ids.Count,
            env.Nodes.Count(n => n.Status == "ACCEPTED"), env.Nodes.Count(n => n.Status == "RETRIEVE"),
            env.Nodes.Count(n => n.Status == "REJECTED"), env.Nodes.Count(n => n.Status == "STOPPED"),
            env.SearchPagesWithMore, env.UnpagedSearches, env.UnreadMatches, o.ExpectedCandidatesSha256 is not null,
            candidatesSha, manifestSha);
        return new RunResult(status, env.States, env.Nodes, env.Violations, sha, ids, env.Raw, open, env.GlobalAccepted, env.GlobalShown,
            env.Reopened, env.GlobalReviewed, summary);
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
        foreach (var (id, _) in ownedCands) e.Presented.Add(id);
        foreach (var c in context) e.Presented.Add(e.Ids[c.Alias]);
        return PayloadJson(new
        {
            protocolVersion = NodeVersion, nodeId, kind = kind.ToString(), level, instruction = o.Instruction, instructionSha256 = e.InstructionSha,
            manifest = e.Manifest, overview = e.Overview,
            owned = Order(ownedCands, o.PromptOrder, x => x.C.Alias).Select(x => new { id = x.Id, sourceAlias = x.C.Alias, page = x.C.Page, text = x.C.Text,
                layoutEvidence = LayoutEvidence(x.C), layout = x.C.Layout,
                current = e.States.TryGetValue(x.Id, out var s) ? s.Decision : "NONE" }).ToArray(),
            context = context.Select(c => new { id = e.Ids[c.Alias], sourceAlias = c.Alias, page = c.Page, text = c.Text,
                layoutEvidence = LayoutEvidence(c), layout = c.Layout, role = "CONTEXT_ONLY" }).ToArray(),
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
        // The global review covers every id it ended with, including ids reopened during the node.
        if (kind == Kind.Global && e.GlobalAccepted) e.GlobalReviewed.UnionWith(shown.Revisable);
        return up ?? Mandatory(e, baseShown);
    }

    static string SynthPayload(Env e, string nodeId, Kind kind, int level, Shown rs)
    {
        var o = e.O;
        var shownCands = rs.Ids.Select(id => (Id: id, C: e.ByAlias[e.IdToAlias[id]])).ToArray();
        foreach (var (id, _) in shownCands) e.Presented.Add(id);
        var pairs = shownCands.GroupBy(x => P7F1V6Tools.Normalize(x.C.Text)).Where(g => g.Count() > 1)
            .Select(g => g.Select(x => new { id = x.Id, sourceAlias = x.C.Alias }).ToArray()).ToArray();
        return PayloadJson(new
        {
            protocolVersion = NodeVersion, nodeId, kind = kind.ToString(), level, instruction = o.Instruction, instructionSha256 = e.InstructionSha,
            manifest = e.Manifest, overview = e.Overview,
            // Access rule: synthesis and global nodes see text only. Geometry is sent with owned and context lines (local nodes), with
            // search pages and with reopened ids, so a node can always reach the source layout without carrying it for every shown id.
            shown = Order(shownCands, o.PromptOrder, x => x.C.Alias).Select(x => new { id = x.Id, sourceAlias = x.C.Alias, page = x.C.Page, text = x.C.Text,
                layoutEvidence = x.C.Layout is null ? "ABSENT" : "AVAILABLE_ON_REOPEN",
                current = e.States.TryGetValue(x.Id, out var s) ? s.Decision : "NONE" }).ToArray(),
            pairObservations = pairs,
            retrieved = new { searches = rs.Searches, reopened = rs.Reopened },
        });
    }

    static string LayoutEvidence(Cand c) => c.Layout is null ? "ABSENT" : "PRESENT";

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
            foreach (var cur in rs.Cursors.Values)     // a query with unread matches when the node finished is reported, never silently treated as complete
                if (cur.ReadEnd < cur.Total) { e.UnpagedSearches++; e.UnreadMatches += cur.Total - cur.ReadEnd; }
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
    // Retrieval: a page of a registry search, or a reopen by id. Both are lookups the model asked for; C# does not judge the results.

    static string? ApplyRetrieval(Env e, Shown rs, Retrieve r)
    {
        if (r.Search is { } q)
        {
            var needle = P7F1V6Tools.Normalize(q);
            var all = e.Ordered.Where(c => P7F1V6Tools.Normalize(c.Text).Contains(needle, StringComparison.Ordinal)).ToArray();
            var page = all.Skip(r.Offset).Take(e.O.MaxSearchResults).ToArray();
            int next = r.Offset + page.Length;
            bool hasMore = next < all.Length;
            var rows = page.Select(c => new { id = e.Ids[c.Alias], sourceAlias = c.Alias, page = c.Page,
                decision = e.States.TryGetValue(e.Ids[c.Alias], out var s) ? s.Decision : "NONE", text = Preview(c.Text),
                layoutEvidence = LayoutEvidence(c), layout = c.Layout }).ToArray();
            // Nothing is cut silently: the model sees the total, whether more follows, and where the next page starts.
            rs.Searches.Add(new
            {
                query = q, offset = r.Offset, pageSize = e.O.MaxSearchResults, totalMatches = all.Length, returned = page.Length,
                hasMore, nextOffset = hasMore ? (int?)next : null, scannedLines = e.Ordered.Count, matches = rows,
            });
            foreach (var c in page) { rs.Citable.Add(c.Alias); e.Presented.Add(e.Ids[c.Alias]); }   // shown to the model, so citable as evidence
            if (!rs.Cursors.TryGetValue(q, out var cur)) rs.Cursors[q] = cur = new Cursor();
            cur.Total = all.Length;
            if (r.Offset <= cur.ReadEnd) cur.ReadEnd = Math.Max(cur.ReadEnd, next); else cur.Gap = true;
            if (hasMore) e.SearchPagesWithMore++;
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
            var target = e.ByAlias[alias];
            rs.Reopened.Add(new
            {
                id, sourceAlias = alias, page = target.Page, text = target.Text, layoutEvidence = LayoutEvidence(target), layout = target.Layout,
                decision = prev?.Decision ?? "NONE", sourceNode = prev?.SourceNode,
                neighbours = neigh.Select(n => new { id = e.Ids[n.Alias], sourceAlias = n.Alias, page = n.Page, text = Preview(n.Text),
                    layoutEvidence = LayoutEvidence(n), layout = n.Layout,
                    decision = e.States.TryGetValue(e.Ids[n.Alias], out var ns) ? ns.Decision : "NONE", role = "CONTEXT_ONLY" }).ToArray(),
            });
            rs.Ids.Add(id); rs.Revisable.Add(id); rs.Citable.Add(alias);
            e.Reopened.Add(id);
        }
        return null;
    }

    static string Preview(string t) => t.Length <= TextPreviewChars ? t : t[..TextPreviewChars];

    // ------------------------------------------------------------------------------------------------------------------
    // Parsing: the model's response is checked against what it was shown. Nothing is interpreted semantically.

    public sealed record Item(string Id, string Decision, IReadOnlyList<string> Refs, string? Parent, string? Continues);
    public sealed record Complete(IReadOnlyList<Item> Items, IReadOnlyList<string> Confirmed, List<string> PassUp);
    public sealed record Retrieve(string? Search, IReadOnlyList<string> Reopen, int Offset);
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
                int offset = 0;
                if (rt.TryGetProperty("offset", out var of))
                {
                    if (of.ValueKind != JsonValueKind.Number || !of.TryGetInt32(out offset) || offset < 0) return Fail("OFFSET_INVALID");
                }
                if (offset > 0 && search is null) return Fail("OFFSET_WITHOUT_SEARCH");
                var reopen = new List<string>();
                if (rt.TryGetProperty("reopen", out var ro))
                {
                    if (ro.ValueKind != JsonValueKind.Array || ro.GetArrayLength() > e.O.MaxReopen || ro.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                        return Fail("REOPEN_INVALID");
                    reopen.AddRange(ro.EnumerateArray().Select(x => x.GetString()!));
                }
                if (search is null && reopen.Count == 0) return Fail("RETRIEVE_EMPTY");
                return new(null, new Retrieve(search, reopen, offset), null);
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

    /// <summary>Order of items inside a prompt. NORMAL keeps reading order. REVERSED and SEEDED:n permute the same items.
    /// TARGET:alias:HEAD|MIDDLE|TAIL moves one candidate (when it is in the list) to that position and keeps the rest in reading order.</summary>
    public static IEnumerable<T> Order<T>(IEnumerable<T> xs, string mode, Func<T, string> alias)
    {
        var list = xs.ToList();
        if (mode == "NORMAL") return list;
        if (mode == "REVERSED") { list.Reverse(); return list; }
        if (mode.StartsWith("SEEDED:", StringComparison.Ordinal))
        {
            var rng = new Random(int.Parse(mode["SEEDED:".Length..]));
            return list.OrderBy(_ => rng.Next()).ToList();
        }
        if (mode.StartsWith("TARGET:", StringComparison.Ordinal))
        {
            // The alias itself contains ':' (for example L0005:S0), so the position is the text after the last ':'.
            int cut = mode.LastIndexOf(':');
            var targetAlias = mode["TARGET:".Length..cut];
            var position = mode[(cut + 1)..];
            var idx = list.FindIndex(x => alias(x) == targetAlias);
            if (idx < 0) return list;
            var target = list[idx];
            list.RemoveAt(idx);
            int pos = position switch
            {
                "HEAD" => 0,
                "MIDDLE" => list.Count / 2,
                "TAIL" => list.Count,
                _ => throw new ArgumentException("PROMPT_ORDER_UNKNOWN:" + mode),
            };
            list.Insert(pos, target);
            return list;
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

    /// <summary>The candidate universe hash: alias, page, text and layout of every candidate, in order. This is the value to pin.</summary>
    public static string CandidatesSha256(IReadOnlyList<Cand> cands) =>
        Sha(string.Join("\n", cands.Select(c => $"{c.Alias}|{c.Page}|{c.Text}|{(c.Layout is null ? "" : PayloadJson(c.Layout))}")));

    static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>A structural reference is valid only for an id the node was shown, context and retrieved ids included.</summary>
    static bool IsShown(Env e, Shown rs, string id) => e.IdToAlias.TryGetValue(id, out var alias) && rs.Citable.Contains(alias);

    static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
