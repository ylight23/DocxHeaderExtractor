using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Transport;
using System.Text;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents.Budget;

namespace DocxHeaderExtractor.Conversation;

/// <summary>The running counts of one conversation, which the task reads when it builds its outcome.</summary>
internal sealed class ConversationState(AgentBudget budget)
{
    string? lastRefused;

    public AgentBudget Budget { get; } = budget;
    public int Posts { get; internal set; }
    public int ToolCallsEmitted { get; internal set; }
    public int ToolCallsSucceeded { get; internal set; }
    public int RepairsUsed { get; internal set; }
    public int RetriesUsed { get; internal set; }
    /// <summary>The content bytes of every tool message so far.</summary>
    public int MessageBytes { get; internal set; }
    /// <summary>The failure of the first repair.</summary>
    public string? RepairedFrom { get; internal set; }
    /// <summary>The SHA-256 of every POST, retries included, in order.</summary>
    public List<string> RequestSha256s { get; } = [];

    /// <summary>A refused answer sent again word for word after a repair message will not change on a third try (temperature 0): the loop
    /// stops instead of paying for it.</summary>
    internal bool Unchanged(string? content)
    {
        var same = lastRefused is not null && content == lastRefused;
        lastRefused = content;
        return same;
    }
}

/// <summary>
/// One conversation with a model that may call tools (ported from the qualified V6 RunUnit): turns 1..MaxToolTurns declare tool_choice auto
/// and a reply with tool calls is answered with the tool results; the next turn asks for the answer. A final answer goes to the task to judge;
/// a refused one gets a repair turn. Every POST is admitted by the budget first; a transport failure is sent again a few times, the same bytes.
/// The loop never reads the reasoning, never changes an answer and never adds a call the model did not make.
/// </summary>
internal static class ToolConversation
{
    public static TOutcome Run<TOutcome>(IConversationTask<TOutcome> task, Func<string, int, PreparedRequest, ModelTurn> model, AgentBudget budget,
        int retryDelayMs = 5000, int maxPosts = int.MaxValue)
    {
        var limits = task.Limits;
        var tools = task.Tools;
        var state = new ConversationState(budget);
        var reasoningRetries = 0;
        var forcedChoices = new Queue<string>();
        var history = new JsonArray();
        var issuedIds = new HashSet<string>(StringComparer.Ordinal);

        TOutcome Stop(string status, string failure) => task.Stop(state, status, failure);

        // Repairs have no limit of their own: the budget's call limit stops a conversation that keeps failing.
        void Repair(string failure, IReadOnlyList<string>? missing = null, string? detail = null)
        {
            state.RepairedFrom ??= failure;
            state.RepairsUsed++;
            foreach (var choice in task.ChoicesAfterRepair(failure)) forcedChoices.Enqueue(choice);
            history.Add(new JsonObject { ["role"] = "user", ["content"] = task.RepairMessage(failure, missing, detail) });
        }

        for (int turn = 1; turn <= limits.MaxTurns + 2 * state.RepairsUsed; turn++)
        {
            if (state.Posts >= maxPosts) return Stop("INCOMPLETE", "REVIEW_POST_LIMIT");
            var toolChoice = forcedChoices.Count > 0 ? forcedChoices.Dequeue() : turn <= limits.MaxToolTurns ? "auto" : "none";
            var req = task.Build(history, toolChoice);
            var refusal = budget.Admit(req);
            if (refusal is not null) return Stop("INCOMPLETE", "BUDGET:" + refusal);
            state.RequestSha256s.Add(req.Sha256);
            state.Posts++;
            ModelTurn reply = null!;
            var sent = false;
            string? IncreaseCompletion()
            {
                if (state.Posts >= maxPosts) return "REVIEW_POST_LIMIT";
                req = task.Build(history, toolChoice);
                if (budget.Admit(req) is { } refused) return "BUDGET:" + refused;
                state.RetriesUsed++;
                state.RequestSha256s.Add(req.Sha256);
                state.Posts++;
                return null;
            }
            while (!sent)
            {
                try
                {
                    reply = model(task.UnitId, turn, req);
                    if (reply.FinishReason == "length" && task.TryIncreaseCompletionBudget())
                    {
                        if (IncreaseCompletion() is { } failure) return Stop("INCOMPLETE", failure);
                        continue;
                    }
                    sent = true;
                }
                catch (ReplyFailure f) when (IsCompletionExhaustion(f.Code) && task.TryIncreaseCompletionBudget())
                {
                    if (IncreaseCompletion() is { } failure) return Stop("INCOMPLETE", failure);
                }
                catch (ReplyFailure f) when (!f.TransportRetriesHandled && state.RetriesUsed < limits.TransportRetries && limits.RetriableTransport.Contains(f.Code, StringComparer.Ordinal))
                {
                    // The provider failed this call: the same bytes are sent again after a pause, admitted by the budget like any POST.
                    state.RetriesUsed++;
                    if (retryDelayMs > 0) Thread.Sleep(retryDelayMs);
                    if (state.Posts >= maxPosts) return Stop("INCOMPLETE", "REVIEW_POST_LIMIT");
                    var again = budget.Admit(req);
                    if (again is not null) return Stop("INCOMPLETE", "BUDGET:" + again);
                    state.RequestSha256s.Add(req.Sha256);
                    state.Posts++;
                }
                catch (ReplyFailure f) when (!f.TransportRetriesHandled && reasoningRetries < 1 && limits.RetryOnceCodes.Any(c => f.Code.Contains(c, StringComparison.Ordinal)))
                {
                    // A failure the task retries once (scan: reasoning used every token, or the call timed out): the same bytes once more.
                    reasoningRetries++;
                    state.RetriesUsed++;
                    if (state.Posts >= maxPosts) return Stop("INCOMPLETE", "REVIEW_POST_LIMIT");
                    var again = budget.Admit(req);
                    if (again is not null) return Stop("INCOMPLETE", "BUDGET:" + again);
                    state.RequestSha256s.Add(req.Sha256);
                    state.Posts++;
                }
                catch (ReplyFailure f)
                {
                    if (f.Code.StartsWith("AUDIT:", StringComparison.Ordinal) || f.Code.StartsWith("CONTENT:", StringComparison.Ordinal)) budget.NoteContract(false);
                    return Stop("FAILED", "CALL:" + f.Code);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    return Stop("FAILED", "MODEL_CALL_FAILED");
                }
            }
            state.ToolCallsEmitted += reply.ToolCalls.Count;

            if (reply.ToolCalls.Count > 0 && reply.FinishReason != "tool_calls")
            { budget.NoteContract(false); return Stop("FAILED", "PROTOCOL:TOOL_CALLS_WITH_FINISH_" + (reply.FinishReason ?? "NULL").ToUpperInvariant()); }
            if (reply.ToolCalls.Count == 0 && reply.FinishReason != "stop")
            { budget.NoteContract(false); return Stop("FAILED", "PROTOCOL:ANSWER_WITH_FINISH_" + (reply.FinishReason ?? "NULL").ToUpperInvariant()); }

            if (reply.ToolCalls.Count > 0)
            {
                task.NoteToolRound(reply);
                if (toolChoice == "none")
                {
                    if (reply.ToolCalls.Any(c => string.IsNullOrEmpty(c.Id))) { budget.NoteContract(false); return Stop("FAILED", "PROTOCOL:TOOL_CALL_AFTER_TOOL_CAP"); }
                    // The calls are answered under their own ids as not run, then the repair message asks for the answer.
                    var notRun = new JsonArray(reply.ToolCalls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name ?? "", ["arguments"] = c.Arguments },
                    }).ToArray());
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content, ["tool_calls"] = notRun });
                    foreach (var c in reply.ToolCalls)
                        history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c.Id, ["content"] = "{\"status\":\"NOT_RUN\",\"error\":\"TOOL_ROUNDS_EXHAUSTED\"}" });
                    Repair("PROTOCOL:TOOL_CALL_AFTER_TOOL_CAP");
                    continue;
                }
                // Every tool_call_id is the model's own: non-empty and unique in the conversation.
                var turnIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in reply.ToolCalls)
                {
                    if (string.IsNullOrEmpty(c.Id)) { budget.NoteContract(false); return Stop("FAILED", "PROTOCOL:TOOL_CALL_ID_MISSING"); }
                    if (!turnIds.Add(c.Id) || issuedIds.Contains(c.Id)) { budget.NoteContract(false); return Stop("FAILED", "PROTOCOL:TOOL_CALL_ID_DUPLICATE"); }
                }
                issuedIds.UnionWith(turnIds);
                // Every call of this turn must at least fit its rejection; if not, the turn is refused before any call runs.
                var ids = Enumerable.Range(1, reply.ToolCalls.Count).Select(n => $"E{turn}.{n:00}").ToArray();
                var reserve = reply.ToolCalls.Select((c, i) => tools.RejectionBytes(ids[i], c.Name)).ToArray();
                if (state.MessageBytes + reserve.Sum() > limits.MaxToolMessageBytes) return Stop("INCOMPLETE", "BUDGET:UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED");
                var future = Enumerable.Range(turn + 1, Math.Max(0, limits.MaxToolTurns - turn))
                    .Sum(t => Enumerable.Range(1, limits.MaxCallsPerTurn).Sum(k => tools.ReservedNames.Max(name => tools.RejectionBytes($"E{t}.{k:00}", name))));
                var assistantCalls = new JsonArray();
                var results = new List<JsonObject>();
                for (int n = 1; n <= reply.ToolCalls.Count; n++)
                {
                    var call = reply.ToolCalls[n - 1];
                    var evidenceId = ids[n - 1];
                    var later = reserve.Skip(n).Sum();
                    var result = n > limits.MaxCallsPerTurn ? tools.Reject(evidenceId, call.Name, call.Arguments, "PER_TURN_CALL_CAP_EXCEEDED")
                        : state.MessageBytes + tools.MaxResultBytes + later + future > limits.MaxToolMessageBytes
                            ? tools.Reject(evidenceId, call.Name, call.Arguments, "UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED")
                        : tools.Execute(evidenceId, call.Name, call.Arguments);
                    state.MessageBytes += result.Content.Length;
                    if (state.MessageBytes + later > limits.MaxToolMessageBytes) throw new InvalidOperationException("TOOL_MESSAGE_BYTES_INVARIANT_BROKEN");
                    if (result.Status == "OK") state.ToolCallsSucceeded++;
                    assistantCalls.Add(new JsonObject
                    {
                        ["id"] = call.Id, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name ?? "", ["arguments"] = call.Arguments },
                    });
                    results.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = Encoding.UTF8.GetString(result.Content) });
                }
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content, ["tool_calls"] = assistantCalls });
                foreach (var r in results) history.Add(r);
                // An image a tool returned (a scan crop) cannot travel in a tool message: the images follow as one user message, in call order.
                var images = tools.TakeImages();
                if (images.Count > 0)
                    history.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(images.Prepend(new JsonObject { ["type"] = "text", ["text"] = task.ImagesNote }).Select(n => (JsonNode)n).ToArray()) });
                continue;
            }

            // A reply without tool calls is the final answer: the task judges it.
            switch (task.Judge(reply, state))
            {
                case Verdict<TOutcome>.Finish finish:
                    return finish.Outcome;
                case Verdict<TOutcome>.Repair repair:
                    if (repair.StopIfUnchanged && state.Unchanged(reply.Content)) return Stop("FAILED", repair.Failure + ":UNCHANGED");
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content });
                    Repair(repair.Failure, repair.Missing, repair.Detail);
                    continue;
            }
        }
        throw new InvalidOperationException("AGENT_TURN_LOOP_EXHAUSTED");
    }

    static bool IsCompletionExhaustion(string code) => code.StartsWith("AUDIT:", StringComparison.Ordinal)
        && code[6..].Split(',', ';', '|').All(c => c is "FINISH_LENGTH" or "REASONING_EXHAUSTED");
}
