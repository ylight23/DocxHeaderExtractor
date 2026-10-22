using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using DocxHeaderExtractor.Contracts.Routing;
using static DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Grouping;

/// <summary>
/// The grouping step after the selection: which of the selected lines belong to one heading. A heading that runs over several lines is made of
/// lines that are next to each other in the parser's reading order, so C# only builds the candidate runs (two or more selected lines with no
/// other line between them) and asks the model how each run divides into headings; a run of one line needs no question. C# checks the answer:
/// every run exactly once, its lines in order, split into contiguous headings of one to eight lines. A run the model divided correctly is kept even
/// when another run of the same answer is refused, and a repair turn asks only for the runs still without a division. A run that is still refused
/// after the repair turns, or whose request failed, is divided by the flavor's rule (for PDF: consecutive lines of the same page and parser layout
/// block) and its lines carry the provenance FALLBACK. <see cref="Group"/> returns those groups for audit and qualification; the production
/// selector does not bind them: it withholds the FALLBACK aliases as UNRESOLVED (fail closed), so a boundary nobody accepted is never guessed.
/// Grouping has no membership authority: every selected alias must be assigned to one boundary group. A dispute-shaped
/// answer is a protocol failure and receives the bounded protocol repair; persistent failures remain unresolved.
/// </summary>
internal static class AgentGrouping
{
    public const string GroupVersion = "PDF_HEADING_GROUPS_V1";
    // Keep each reasoning request small enough to return promptly, while allowing independent batches to run concurrently.
    public const int MaxRunsPerRequest = 8, MaxRepairs = 5, MaxHeadingLines = 8, ContextChars = 120;

    internal sealed record Run(string Id, int[] Lines);

    /// <param name="Failures">One entry per request whose runs were not all divided: the unit, the refusal, and the runs that fell back
    /// (for example <c>GROUP-001:PROTOCOL:RUN_MISSING:R7,R12</c>).</param>
    /// <summary>Legacy qualification shape only; the production grouping parser never emits disputes.</summary>
    public sealed record Dispute(string RunId, IReadOnlyList<string> Aliases, string Reason);
    public sealed record Result(IReadOnlyList<IReadOnlyList<string>> Groups, IReadOnlyList<string> Answers, IReadOnlyList<string> Failures, int Posts)
    {
        /// <summary>Legacy qualification data only. Production Group always leaves this empty.</summary>
        public IReadOnlyList<Dispute> Disputes { get; init; } = [];
        public IReadOnlyList<string> Evidence { get; init; } = [];
        public IReadOnlyList<string> UnresolvedAliases { get; init; } = [];
        public GroupingObservation Observation { get; init; } = new();
        public IReadOnlyDictionary<string, string> Memberships { get; init; } = new Dictionary<string, string>();
        public IReadOnlyList<HeadingAliasProvenance> Provenance { get; init; } = [];
    }

    static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Model-divided runs in document order and layout fallback for protocol failures.</summary>
    public static Result Group(IAgentSource source, IReadOnlySet<string> selected, AgentUnit.Model model, AgentBudget budget, int parallelism, CancellationToken ct, int retryDelayMs = 5000,
        string instruction = Instruction, string unitPrefix = "GROUP", object? verificationFeedback = null, GroupingCache? cache = null)
    {
        var chosen = Enumerable.Range(0, source.Lines.Count).Where(i => selected.Contains(source.Lines[i].Alias)).ToArray();
        // Maximal runs of selected lines that are adjacent in reading order.
        var runs = new List<Run>();
        for (int k = 0; k < chosen.Length;)
        {
            int e = k + 1;
            while (e < chosen.Length && chosen[e] == chosen[e - 1] + 1) e++;
            if (e - k >= 2) runs.Add(new Run("R" + (runs.Count + 1), chosen[k..e]));
            k = e;
        }
        var division = new Dictionary<string, List<int[]>>(StringComparer.Ordinal);   // run id -> contiguous groups of line indices
        var answers = new List<string>();
        var failures = new List<string>();
        var posts = 0;
        var evidence = new List<string>();
        var chunks = runs.Chunk(MaxRunsPerRequest).ToArray();
        var results = new (Dictionary<string, List<int[]>> Division, List<string> Answers, string? Failure, int Posts, int Repairs,
            List<string> Evidence)[chunks.Length];
        var batchTimings = new GroupingBatchTiming[chunks.Length];
        var groupClock = Stopwatch.StartNew();
        Parallel.For(0, chunks.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            n =>
            {
                var started = groupClock.Elapsed.TotalSeconds;
                // A batch whose first request was answered before is read back, not asked again (the qualification feedback is never cached).
                var key = cache is null || verificationFeedback is not null ? null : BatchKey(source, chunks[n], n + 1, instruction);
                var result = key is not null && cache!.TryLoad(source.SourceSha256, key) is { } stored && FromStored(source, chunks[n], stored) is { } reused
                    ? reused
                    : AskChunk(source, chunks[n], n + 1, model, budget, retryDelayMs, instruction, unitPrefix, verificationFeedback);
                if (key is not null && result.Failure is null && result.Division.Count == chunks[n].Length)
                    cache!.Store(source.SourceSha256, key, ToStored(source, result.Division));
                var completed = groupClock.Elapsed.TotalSeconds;
                results[n] = result;
                batchTimings[n] = new GroupingBatchTiming(unitPrefix, unitPrefix + "-" + (n + 1).ToString("000"), chunks[n].Length,
                    result.Posts, result.Repairs, Math.Round(started, 3), Math.Round(completed, 3), Math.Round(completed - started, 3));
            });
        foreach (var r in results)
        {
            posts += r.Posts;
            answers.AddRange(r.Answers);
            evidence.AddRange(r.Evidence);
            if (r.Failure is not null) failures.Add(r.Failure);
            foreach (var kv in r.Division) division[kv.Key] = kv.Value;
        }

        var groups = new List<IReadOnlyList<string>>();
        var runOf = runs.ToDictionary(r => r.Lines[0]);
        for (int k = 0; k < chosen.Length;)
        {
            if (runOf.TryGetValue(chosen[k], out var run))
            {
                var parts = division.TryGetValue(run.Id, out var d) ? d : source.Flavor.FallbackDivision(source, run.Lines);
                foreach (var g in parts) groups.Add(g.Select(i => source.Lines[i].Alias).ToArray());
                k += run.Lines.Length;
            }
            else { groups.Add([source.Lines[chosen[k]].Alias]); k++; }
        }
        var provenance = runs.SelectMany((run, index) => run.Lines.Select(i =>
        {
            var failure = results[index / MaxRunsPerRequest].Failure;
            return new HeadingAliasProvenance(source.Lines[i].Alias, "grouping", unitPrefix + "-" + (index / MaxRunsPerRequest + 1).ToString("000"), run.Id,
                division.ContainsKey(run.Id) ? "DIVIDED" : "FALLBACK", null,
                !division.ContainsKey(run.Id) ? ["GROUP_DIVISION_UNRESOLVED"] : [], [])
                { UnitFailureContext = failure };
        })).ToArray();
        return new Result(groups, answers, failures, posts) { Evidence = evidence, Provenance = provenance,
            Observation = new(ModelDividedRuns: division.Count, FallbackRuns: runs.Count - division.Count, DisputedRuns: 0,
                Batches: batchTimings) };
    }

    /// <summary>The SHA-256 of the first request a batch sends: the same runs and lines give the same key.</summary>
    internal static string BatchKey(IAgentSource source, Run[] chunk, int number, string instruction) =>
        BuildRequest(Payload(source, chunk, number, instruction), new JsonArray(), source.Flavor.GroupPrompt).Sha256;

    /// <summary>The stored division as line indices, when it covers every run of the batch with lines that still exist; otherwise null (asked again).</summary>
    static (Dictionary<string, List<int[]>> Division, List<string> Answers, string? Failure, int Posts, int Repairs, List<string> Evidence)? FromStored(
        IAgentSource source, Run[] chunk, Dictionary<string, List<List<string>>> stored)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < source.Lines.Count; i++) index[source.Lines[i].Alias] = i;
        var division = new Dictionary<string, List<int[]>>(StringComparer.Ordinal);
        foreach (var run in chunk)
        {
            if (!stored.TryGetValue(run.Id, out var groups)) return null;
            var parts = new List<int[]>();
            foreach (var group in groups)
            {
                var lines = new int[group.Count];
                for (int k = 0; k < group.Count; k++)
                {
                    if (!index.TryGetValue(group[k], out var at)) return null;
                    lines[k] = at;
                }
                parts.Add(lines);
            }
            division[run.Id] = parts;
        }
        return (division, [], null, 0, 0, []);
    }

    static Dictionary<string, List<List<string>>> ToStored(IAgentSource source, Dictionary<string, List<int[]>> division) =>
        division.ToDictionary(kv => kv.Key, kv => kv.Value.Select(g => g.Select(i => source.Lines[i].Alias).ToList()).ToList(), StringComparer.Ordinal);

    /// <summary>The fallback: consecutive lines of the same page and parser layout block are one heading.</summary>
    internal static List<int[]> LayoutRule(AgentSource source, int[] lines)
    {
        var groups = new List<List<int>>();
        foreach (var i in lines)
        {
            if (groups.Count > 0 && source.Lines[groups[^1][^1]].Page == source.Lines[i].Page && source.Blocks[groups[^1][^1]] == source.Blocks[i] && groups[^1].Count < MaxHeadingLines)
                groups[^1].Add(i);
            else groups.Add([i]);
        }
        return groups.Select(g => g.ToArray()).ToList();
    }

    static (Dictionary<string, List<int[]>> Division, List<string> Answers, string? Failure, int Posts, int Repairs,
        List<string> Evidence) AskChunk(
        IAgentSource source, Run[] chunk, int number, AgentUnit.Model model, AgentBudget budget, int retryDelayMs, string instruction, string unitPrefix, object? verificationFeedback)
    {
        // The runs divided so far, from any turn: a correct division of one run is kept when another run of the same answer is refused.
        var accepted = new Dictionary<string, List<int[]>>(StringComparer.Ordinal);
        var answers = new List<string>();
        var evidence = new List<string>();
        var unitId = unitPrefix + "-" + number.ToString("000");
        string Fallback(string failure) =>
            unitId + ":" + failure + ":" + string.Join(",", chunk.Where(r => !accepted.ContainsKey(r.Id)).Select(r => r.Id));
        var history = new JsonArray();
        var payload = Payload(source, chunk, number, instruction);
        if (verificationFeedback is not null)
        {
            var node = JsonNode.Parse(payload)!;
            node["verificationFeedback"] = JsonSerializer.SerializeToNode(verificationFeedback);
            payload = node.ToJsonString(Options);
        }
        int posts = 0, retries = 0, repairs = 0;
        var retriedOnce = false;
        while (true)
        {
            var req = BuildRequest(payload, history, source.Flavor.GroupPrompt);
            if (budget.Admit(req) is { } refusal) return (accepted, answers, Fallback("BUDGET:" + refusal), posts, repairs, evidence);
            posts++;
            AgentProtocol.ModelTurn reply;
            try { reply = model(unitId, 1 + repairs, req); }
            catch (ReplyFailure f) when (retries < AgentUnit.TransportRetries && AgentUnit.RetriableTransport.Contains(f.Code, StringComparer.Ordinal))
            {
                retries++;
                if (retryDelayMs > 0) Thread.Sleep(retryDelayMs);
                continue;
            }
            // The flavor's retry-once failures (scan: a timeout, or reasoning that used every token) get one more try, as a selection unit
            // does: without it one slow call withheld every run of the batch (023: three batches timed out at 300 s, 19 runs lost).
            catch (ReplyFailure f) when (!retriedOnce && source.Flavor.RetryOnceCodes.Any(c => f.Code.Contains(c, StringComparison.Ordinal)))
            {
                retriedOnce = true;
                continue;
            }
            catch (ReplyFailure f) { return (accepted, answers, Fallback("CALL:" + f.Code), posts, repairs, evidence); }
            catch (Exception e) when (e is not OutOfMemoryException) { return (accepted, answers, Fallback("MODEL_CALL_FAILED"), posts, repairs, evidence); }

            string? failure, detail = null;
            if (reply.ToolCalls.Count > 0 || reply.FinishReason != "stop") failure = "PROTOCOL:ANSWER_WITH_FINISH_" + (reply.FinishReason ?? "NULL").ToUpperInvariant();
            else
            {
                var content = reply.Content ?? "";
                if (TryUnwrapFence(content, out var inner)) content = inner;
                var parsed = Parse(content, chunk, source, out failure, out detail, accepted.Keys.ToHashSet(StringComparer.Ordinal));
                if (parsed is { Count: > 0 })
                {
                    foreach (var kv in parsed) accepted[kv.Key] = kv.Value;
                    answers.Add(reply.Content!);
                }
            }
            evidence.Add(JsonSerializer.Serialize(new { unit = unitId, turn = 1 + repairs, requestSha256 = req.Sha256, response = reply.Content, failure }, Options));
            if (failure is null) return (accepted, answers, null, posts, repairs, evidence);
            if (repairs >= MaxRepairs) return (accepted, answers, Fallback(failure), posts, repairs, evidence);
            repairs++;
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content ?? "" });
            history.Add(new JsonObject { ["role"] = "user", ["content"] = RepairMessage(failure, detail, accepted.Count > 0) });
        }
    }

    /// <summary>The message sent with a repair turn. The parser knows exactly which lines were left out of a run or put out of order, so it names them
    /// (aliases only; it never says what to decide).</summary>
    internal static string RepairMessage(string failure, string? detail, bool someAccepted = false) =>
        "Your answer was refused (" + failure + ")" + (detail is null ? "" : ": " + detail + ".")
        + " Selection has already decided that every alias in each run is a heading. Do not reconsider membership or report a dispute. Return a boundary partition for every run: every alias must occur exactly once, in source order, in contiguous groups. Do not leave any alias out or invent aliases."
        + (someAccepted
            ? " The runs not named here were accepted and are kept. Answer for the runs named here, each exactly once, with groups of 1 to " + MaxHeadingLines + " lines and aliases copied exactly, as exactly one JSON object of the same form."
            : " Give every run exactly once, with groups of 1 to " + MaxHeadingLines + " lines and aliases copied exactly. Send the complete answer again as exactly one JSON object.");

    static string Payload(IAgentSource source, Run[] chunk, int number, string instruction)
    {
        object Line(int i) => source.Flavor.GroupLineItem(source, i);
        object? Near(int i) => i < 0 || i >= source.Lines.Count ? null : source.Flavor.GroupContextItem(source, i);
        var payload = new Dictionary<string, object?>
        {
            ["instruction"] = instruction,
            ["instructionSha256"] = Sha(instruction),
            ["documentSha256"] = source.SourceSha256,
            ["stage"] = "GROUP", ["chunk"] = number,
            ["runs"] = chunk.Select(r => new
            {
                run = r.Id,
                before = Near(r.Lines[0] - 1),
                lines = r.Lines.Select(Line).ToList(),
                after = Near(r.Lines[^1] + 1),
            }).ToList(),
        };
        return JsonSerializer.Serialize(payload, Options);
    }

    internal static string Clip(string t) => t.Length <= ContextChars ? t : t[..ContextChars];

    /// <summary>One POST body: the group prompt and the payload, json_object, no tools. The provider block, streaming and usage are the agent's.</summary>
    internal static PreparedRequest BuildRequest(string payload, JsonArray history, string? groupPrompt = null)
    {
        // The group prompt and the batch payload are cache-marked like the selection's blocks: a repair turn re-sends both unchanged.
        static JsonNode Marked(string text) =>
            new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } });
        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = Marked(groupPrompt ?? GroupingPrompts.Shared) },
            new JsonObject { ["role"] = "user", ["content"] = Marked(payload) });
        foreach (var m in history) messages.Add(m!.DeepClone());
        var body = new JsonObject
        {
            ["model"] = ChatModel,
            ["messages"] = messages,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["temperature"] = 0,
            ["max_tokens"] = MaxTokens,
            ["reasoning"] = new JsonObject { ["enabled"] = true },
            ["provider"] = JsonSerializer.SerializeToNode(new { only = new[] { ProviderTag }, ignore = Ignore, allow_fallbacks = false, require_parameters = true }, Options),
            ["stream"] = true,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        var text = body.ToJsonString(Options);
        var bytes = Encoding.UTF8.GetBytes(text);
        return new PreparedRequest(text, bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    /// <summary>
    /// The strict parser of a grouping answer. A fault in the shape of the answer refuses all of it: null with the failure code. Otherwise it returns
    /// the runs divided correctly, and sets the failure and its detail when a run was divided wrongly or left out. A run in <paramref name="done"/>
    /// was accepted on an earlier turn: it need not be given again, and when it is, its new division is ignored.
    /// </summary>
    internal static Dictionary<string, List<int[]>>? Parse(string reply, Run[] chunk, IAgentSource source, out string? failure, out string? detail,
        IReadOnlySet<string>? done = null)
    {
        failure = null; detail = null;
        if (Encoding.UTF8.GetByteCount(reply) > VisibleResponseCapBytes) { failure = "PROTOCOL:REPLY_OVER_CAP"; return null; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(reply); } catch (JsonException) { failure = "PROTOCOL:REPLY_NOT_JSON"; return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("protocolVersion" or "runs"))) { failure = "PROTOCOL:ROOT_KEYS_INVALID"; return null; }
            // The model writes "runs" once per run after another, {"runs":[R1],"runs":[R2]} (023 GROUP-001, 2026-10-21: six repairs asked it to
            // join them and it repeated the key each time). JsonDocument keeps every occurrence, so each one is read as a list of runs, in order,
            // and no run is lost. A run listed twice is still refused (RUN_LISTED_TWICE), and a key repeated anywhere else still is refused.
            var runLists = root.EnumerateObject().Where(p => p.Name == "runs").Select(p => p.Value).ToList();
            var repeated = root.EnumerateObject().Where(p => p.Name != "runs").GroupBy(p => p.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => ((string Name, int Count)?)(g.Key, g.Count())).FirstOrDefault()
                ?? runLists.Select(RepeatedKey).FirstOrDefault(x => x is not null);
            if (repeated is { } rep)
            {
                failure = "PROTOCOL:KEY_REPEATED";
                detail = "the key \"" + rep.Name + "\" appears " + rep.Count + " times in one object; give it once";
                return null;
            }
            if (!root.TryGetProperty("protocolVersion", out var v) || v.ValueKind != JsonValueKind.String || v.GetString() != GroupVersion) { failure = "PROTOCOL:PROTOCOL_VERSION_INVALID"; return null; }
            if (runLists.Count == 0 || runLists.Any(a => a.ValueKind != JsonValueKind.Array)) { failure = "PROTOCOL:RUNS_MISSING"; return null; }
            var byId = chunk.ToDictionary(r => r.Id, StringComparer.Ordinal);
            var division = new Dictionary<string, List<int[]>>(StringComparer.Ordinal);
            // A fault in the shape of the answer is returned at once. A fault in one run's division is collected, so one repair message names
            // every run that is wrong (a model that dropped a line in one run usually did the same in another).
            var problems = new List<(string Code, string Text)>();
            foreach (var r in runLists.SelectMany(a => a.EnumerateArray()))
            {
                if (r.ValueKind != JsonValueKind.Object || r.EnumerateObject().Any(p => p.Name is not ("run" or "headings"))) { failure = "PROTOCOL:RUN_KEYS_INVALID"; return null; }
                var id = r.TryGetProperty("run", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString()! : null;
                if (id is null || !byId.TryGetValue(id, out var run)) { failure = "PROTOCOL:RUN_UNKNOWN"; return null; }
                if (division.ContainsKey(id) || problems.Any(x => x.Text.StartsWith("run " + id + " ", StringComparison.Ordinal))) { failure = "PROTOCOL:RUN_LISTED_TWICE"; return null; }
                if (!r.TryGetProperty("headings", out var hs) || hs.ValueKind != JsonValueKind.Array) { failure = "PROTOCOL:HEADINGS_MISSING"; return null; }
                var position = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int k = 0; k < run.Lines.Length; k++) position[source.Lines[run.Lines[k]].Alias] = k;
                var groups = new List<int[]>();
                var next = 0;
                (string Code, string Text)? problem = null;
                foreach (var h in hs.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Array || h.GetArrayLength() is < 1 or > MaxHeadingLines) { failure = "PROTOCOL:HEADING_SIZE_INVALID"; return null; }
                    var lines = new List<int>();
                    foreach (var a in h.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.String || !position.TryGetValue(a.GetString()!, out var at)) { failure = "PROTOCOL:ALIAS_NOT_IN_RUN"; return null; }
                        if (at != next)
                        {
                            problem = ("PROTOCOL:LINES_NOT_IN_ORDER_OR_NOT_COVERED",
                                "run " + id + " has " + source.Lines[run.Lines[at]].Alias + " where " + source.Lines[run.Lines[Math.Min(next, run.Lines.Length - 1)]].Alias + " is expected next");
                            break;
                        }
                        lines.Add(run.Lines[at]);
                        next++;
                    }
                    if (problem is not null) break;
                    groups.Add(lines.ToArray());
                }
                if (problem is null && next != run.Lines.Length)
                    problem = ("PROTOCOL:RUN_NOT_COVERED", "run " + id + " is missing " + string.Join(", ", run.Lines[next..].Select(i => source.Lines[i].Alias)));
                if (problem is { } found) problems.Add(found); else division[id] = groups;
            }
            if (done is not null)
            {
                foreach (var id in done) division.Remove(id);
                problems.RemoveAll(x => done.Any(id => x.Text.StartsWith("run " + id + " ", StringComparison.Ordinal)));
            }
            foreach (var absent in chunk.Where(r => done?.Contains(r.Id) != true && !division.ContainsKey(r.Id) && !problems.Any(x => x.Text.StartsWith("run " + r.Id + " ", StringComparison.Ordinal))))
                problems.Add(("PROTOCOL:RUN_MISSING", "run " + absent.Id + " is not in your answer"));
            if (problems.Count > 0)
            {
                failure = problems[0].Code;
                detail = string.Join("; ", problems.Select(x => x.Text));
            }
            return division;
        }
    }
}
