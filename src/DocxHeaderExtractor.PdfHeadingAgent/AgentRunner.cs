using System.Text;
using System.Text.Json.Nodes;
using static DocxHeaderExtractor.PdfHeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.PdfHeadingAgent;

/// <summary>A call the provider or the audit refused; Code says why (TRANSPORT:..., AUDIT:..., CONTENT:..., ENDPOINT_OR_MODEL_UNEXPECTED).</summary>
internal sealed class ReplyFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// The admission budget of one document: a call limit, a reserve ceiling in USD (each POST reserves its worst case), a request byte cap and an
/// early stop after three consecutive contract failures. Thread-safe; ported from the qualified V6 budget.
/// </summary>
internal sealed class AgentBudget(int maxCalls, double ceilingUsd)
{
    public const int MaxRequestBytes = 131072, PromptTokenBudget = 900000, MaxConsecutiveContractFailures = 3;
    static readonly (int MinPromptTokens, double Prompt, double Completion)[] Tiers = [(0, 0.00000003, 0.00000013), (32000, 0.0000001, 0.0000004), (256000, 0.0000002, 0.0000008)];

    /// <summary>Worst-case cost of one attempt: the bytes bound the prompt tokens; the completion is max_tokens.</summary>
    public static double AttemptUsd(int requestBytes)
    {
        var tier = Tiers.Last(t => requestBytes >= t.MinPromptTokens);
        return requestBytes * tier.Prompt + MaxTokens * tier.Completion;
    }

    readonly object gate = new();
    public int MaxCalls { get; } = maxCalls;
    public double CeilingUsd { get; } = ceilingUsd;
    public int Calls { get; private set; }
    public double ReservedUsd { get; private set; }
    public double ActualUsd { get; private set; }
    public string? HaltReason { get; private set; }
    public int ConsecutiveContractFailures { get; private set; }

    public void Halt(string reason) { lock (gate) HaltReason ??= reason; }

    public void NoteContract(bool passed)
    {
        lock (gate)
        {
            if (passed) { ConsecutiveContractFailures = 0; return; }
            if (++ConsecutiveContractFailures >= MaxConsecutiveContractFailures) HaltReason ??= "CONSECUTIVE_CONTRACT_FAILURES";
        }
    }

    public void RecordActual(double? providerCostUsd, double attemptReserveUsd)
    {
        lock (gate)
        {
            if (providerCostUsd is null) return;
            ActualUsd += providerCostUsd.Value;
            if (providerCostUsd.Value > attemptReserveUsd) HaltReason ??= "PROVIDER_COST_OVER_RESERVE";
        }
    }

    /// <summary>Null when the request may be sent (and reserves it), else the refusal code.</summary>
    public string? Admit(PreparedRequest request)
    {
        lock (gate)
        {
            var worst = AttemptUsd(request.Bytes);
            if (HaltReason is not null) return "HALTED:" + HaltReason;
            if (Calls >= MaxCalls) return "CALL_LIMIT";
            if (request.Bytes > MaxRequestBytes) return "REQUEST_OVER_BYTE_CAP";
            if (request.Bytes > PromptTokenBudget) return "REQUEST_OVER_TOKEN_BUDGET";
            if (ReservedUsd + worst > CeilingUsd) return "RESERVE_OVER_CEILING";
            Calls++;
            ReservedUsd += worst;
            return null;
        }
    }
}

/// <summary>What one unit (one segment) ended with.</summary>
internal sealed record AgentUnitOutcome(string UnitId, string Status, string? Failure, IReadOnlyDictionary<string, string> Decisions, int Posts,
    int ToolCallsEmitted, int ToolCallsSucceeded, bool FenceUnwrapped, string? Analysis, IReadOnlyDictionary<string, string>? Reasons,
    string? RepairedFrom, int RepairsUsed, int TransportRetries, IReadOnlyList<string> RequestSha256s, IReadOnlyDictionary<string, string>? Patterns = null);

/// <summary>
/// One unit of the D4.12 method as a conversation (ported from the qualified V6 RunUnit, arm TOOLFACTS_ANALYSIS_V3, with json_object on every
/// turn, the single-fence unwrap, two prompt-cache marks, 3 transport retries and repair turns with no limit of their own):
///   turns 1..3: tools declared, tool_choice auto; a reply with tool calls is answered with the tool results and the conversation continues;
///   turn 4: tool_choice none; the model answers from what it has.
/// The first answer must come after a successful get_line_facts call. A refused answer gets a repair turn (the refused reply and a message
/// naming the refusal). C# never reads the reasoning, never changes a decision and never adds a call the model did not make.
/// </summary>
internal static class AgentUnit
{
    public const int TransportRetries = 3;
    public static readonly string[] RetriableTransport = ["TRANSPORT:STREAM_ERROR", "TRANSPORT:STREAM_INCOMPLETE", "TRANSPORT:RATE_LIMITED_NO_RETRY", "TRANSPORT:SERVER_ERROR_NO_RETRY", "TRANSPORT:TRANSPORT_ERROR"];

    public delegate ModelTurn Model(string unitId, int turn, PreparedRequest request);

    public static AgentUnitOutcome Run(AgentSource source, int segment, Model model, AgentBudget budget, int retryDelayMs = 5000, string instruction = Instruction,
        bool patterns = false)
    {
        var unitId = AgentSource.SegmentId(segment);
        var (decide, shownIdx) = source.Scope(segment);
        var lines = source.Lines;
        var tools = new AgentTools(source);
        var ordered = decide.OrderBy(tools.PositionOf).ToList();
        var shownAliases = shownIdx.Select(i => lines[i].Alias).ToHashSet(StringComparer.Ordinal);
        var styles = tools.DocumentStyles();
        var payload = Payload(source.SourceSha256, unitId, ordered, shownIdx.Select(i => lines[i]).ToArray(), styles, instruction);
        // The style table shows the first lines of each style: they are in the request, so the model may cite them.
        shownAliases.UnionWith(styles["styles"]!.AsArray().SelectMany(s => s!["firstLines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>())));

        string? repairedFrom = null;
        int repairsUsed = 0, retriesUsed = 0, posts = 0, okCalls = 0, messageBytes = 0, emitted = 0;
        var fenceUnwrapped = false;
        var forcedChoices = new Queue<string>();
        var history = new JsonArray();
        var requestShas = new List<string>();
        var issuedIds = new HashSet<string>(StringComparer.Ordinal);

        AgentUnitOutcome Done(string status, string? failure, IReadOnlyDictionary<string, string>? decisions = null, string? analysis = null,
            IReadOnlyDictionary<string, string>? reasons = null, IReadOnlyDictionary<string, string>? patternsOf = null) =>
            new(unitId, status, failure, decisions ?? ordered.ToDictionary(a => a, _ => "UNRESOLVED", StringComparer.Ordinal), posts, emitted, okCalls,
                fenceUnwrapped, analysis, reasons, repairedFrom, repairsUsed, retriesUsed, requestShas, patternsOf);

        // Repairs have no limit of their own: the budget's call limit stops a unit that keeps failing.
        bool TryRepair(string failure, IReadOnlyList<string>? missing = null)
        {
            repairedFrom ??= failure;
            repairsUsed++;
            if (failure == "PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED") { forcedChoices.Enqueue("auto"); forcedChoices.Enqueue("none"); }
            else forcedChoices.Enqueue("none");
            history.Add(new JsonObject { ["role"] = "user", ["content"] = RepairMessage(failure, missing) });
            return true;
        }

        for (int turn = 1; turn <= MaxPostsPerUnit + 2 * repairsUsed; turn++)
        {
            var toolChoice = forcedChoices.Count > 0 ? forcedChoices.Dequeue() : turn <= MaxToolTurns ? "auto" : "none";
            var req = Build(payload, history, toolChoice, patterns ? SystemPromptPatterns : null);
            var refusal = budget.Admit(req);
            if (refusal is not null) return Done("INCOMPLETE", "BUDGET:" + refusal);
            requestShas.Add(req.Sha256);
            posts++;
            ModelTurn reply = null!;
            var sent = false;
            while (!sent)
            {
                try { reply = model(unitId, turn, req); sent = true; }
                catch (ReplyFailure f) when (retriesUsed < TransportRetries && RetriableTransport.Contains(f.Code, StringComparer.Ordinal))
                {
                    // The provider failed this call: the same bytes are sent again after a pause, admitted by the budget like any POST.
                    retriesUsed++;
                    if (retryDelayMs > 0) Thread.Sleep(retryDelayMs);
                    var again = budget.Admit(req);
                    if (again is not null) return Done("INCOMPLETE", "BUDGET:" + again);
                    requestShas.Add(req.Sha256);
                    posts++;
                }
                catch (ReplyFailure f)
                {
                    if (f.Code.StartsWith("AUDIT:", StringComparison.Ordinal) || f.Code.StartsWith("CONTENT:", StringComparison.Ordinal)) budget.NoteContract(false);
                    return Done("FAILED", "CALL:" + f.Code);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    return Done("FAILED", "MODEL_CALL_FAILED");
                }
            }
            emitted += reply.ToolCalls.Count;

            if (reply.ToolCalls.Count > 0 && reply.FinishReason != "tool_calls")
            { budget.NoteContract(false); return Done("FAILED", "PROTOCOL:TOOL_CALLS_WITH_FINISH_" + (reply.FinishReason ?? "NULL").ToUpperInvariant()); }
            if (reply.ToolCalls.Count == 0 && reply.FinishReason != "stop")
            { budget.NoteContract(false); return Done("FAILED", "PROTOCOL:ANSWER_WITH_FINISH_" + (reply.FinishReason ?? "NULL").ToUpperInvariant()); }

            if (reply.ToolCalls.Count > 0)
            {
                if (toolChoice == "none")
                {
                    if (reply.ToolCalls.Any(c => string.IsNullOrEmpty(c.Id))) { budget.NoteContract(false); return Done("FAILED", "PROTOCOL:TOOL_CALL_AFTER_TOOL_CAP"); }
                    // The calls are answered under their own ids as not run, then the repair message asks for the answer.
                    var notRun = new JsonArray(reply.ToolCalls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name ?? "", ["arguments"] = c.Arguments },
                    }).ToArray());
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content, ["tool_calls"] = notRun });
                    foreach (var c in reply.ToolCalls)
                        history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c.Id, ["content"] = "{\"status\":\"NOT_RUN\",\"error\":\"TOOL_ROUNDS_EXHAUSTED\"}" });
                    TryRepair("PROTOCOL:TOOL_CALL_AFTER_TOOL_CAP");
                    continue;
                }
                // Every tool_call_id is the model's own: non-empty and unique in the conversation.
                var turnIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in reply.ToolCalls)
                {
                    if (string.IsNullOrEmpty(c.Id)) { budget.NoteContract(false); return Done("FAILED", "PROTOCOL:TOOL_CALL_ID_MISSING"); }
                    if (!turnIds.Add(c.Id) || issuedIds.Contains(c.Id)) { budget.NoteContract(false); return Done("FAILED", "PROTOCOL:TOOL_CALL_ID_DUPLICATE"); }
                }
                issuedIds.UnionWith(turnIds);
                // Every call of this turn must at least fit its rejection; if not, the turn is refused before any call runs.
                var ids = Enumerable.Range(1, reply.ToolCalls.Count).Select(n => $"E{turn}.{n:00}").ToArray();
                var reserve = reply.ToolCalls.Select((c, i) => tools.RejectionBytes(ids[i], c.Name)).ToArray();
                if (messageBytes + reserve.Sum() > MaxToolMessageBytesPerUnit) return Done("INCOMPLETE", "BUDGET:UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED");
                var future = Enumerable.Range(turn + 1, Math.Max(0, MaxToolTurns - turn))
                    .Sum(t => Enumerable.Range(1, MaxCallsPerTurn).Sum(k => AgentTools.Names.Max(name => tools.RejectionBytes($"E{t}.{k:00}", name))));
                var assistantCalls = new JsonArray();
                var results = new List<JsonObject>();
                for (int n = 1; n <= reply.ToolCalls.Count; n++)
                {
                    var call = reply.ToolCalls[n - 1];
                    var evidenceId = ids[n - 1];
                    var later = reserve.Skip(n).Sum();
                    var result = n > MaxCallsPerTurn ? tools.Reject(evidenceId, call.Name, call.Arguments, "PER_TURN_CALL_CAP_EXCEEDED")
                        : messageBytes + AgentTools.MaxResultBytes + later + future > MaxToolMessageBytesPerUnit
                            ? tools.Reject(evidenceId, call.Name, call.Arguments, "UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED")
                        : tools.Execute(evidenceId, call.Name, call.Arguments);
                    messageBytes += result.Content.Length;
                    if (messageBytes + later > MaxToolMessageBytesPerUnit) throw new InvalidOperationException("TOOL_MESSAGE_BYTES_INVARIANT_BROKEN");
                    if (result.Status == "OK") okCalls++;
                    assistantCalls.Add(new JsonObject
                    {
                        ["id"] = call.Id, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name ?? "", ["arguments"] = call.Arguments },
                    });
                    results.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = Encoding.UTF8.GetString(result.Content) });
                }
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content, ["tool_calls"] = assistantCalls });
                foreach (var r in results) history.Add(r);
                continue;
            }

            // A reply without tool calls is the final answer. It must come after the facts were read.
            if (!tools.Ledger.Any(c => c.Tool == AgentTools.LineFacts && c.Status == "OK"))
            {
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content });
                TryRepair("PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED");
                continue;
            }
            var observed = shownAliases.Concat(tools.ObservedAliases).Distinct(StringComparer.Ordinal).ToDictionary(a => a, tools.PositionOf, StringComparer.Ordinal);
            var content = reply.Content ?? "";
            if (TryUnwrapFence(content, out var inner)) { content = inner; fenceUnwrapped = true; }
            var parsed = ParseSelect(content, observed, ordered, patternsAllowed: patterns);
            if (!parsed.Ok)
            {
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content });
                TryRepair("PROTOCOL:" + parsed.Failure, parsed.Missing);
                continue;
            }
            budget.NoteContract(true);
            if (parsed.Status == "CLARIFICATION_REQUIRED") return Done("CLARIFICATION_REQUIRED", null, analysis: parsed.Analysis);
            return Done(parsed.Status!, null, parsed.Values, parsed.Analysis, parsed.Reasons, parsed.Patterns);
        }
        throw new InvalidOperationException("AGENT_TURN_LOOP_EXHAUSTED");
    }
}
