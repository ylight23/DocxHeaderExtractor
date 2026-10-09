using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record F1QToolCall(int Index, string? Id, string? Name, string Arguments);

/// <summary>Assembled view of one raw SSE stream. Raw bytes stay the authority; this is derived.</summary>
internal sealed record F1QStreamAssembly(string Content, string Reasoning, IReadOnlyList<F1QToolCall> ToolCalls,
    string? FinishReason, string? NativeFinishReason, JsonElement? Usage, JsonElement? Error, string? GenerationId,
    string? Provider, string? Model, int DataEvents, bool Done, IReadOnlyList<string> MalformedEvents);

internal static class P7F1QSse
{
    public static F1QStreamAssembly Parse(string raw)
    {
        var content = new StringBuilder(); var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        string? finish = null, native = null, id = null, provider = null, model = null;
        JsonElement? usage = null, error = null; var events = 0; var done = false; var malformed = new List<string>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].TrimStart();
            if (data == "[DONE]") { done = true; continue; }
            events++;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { malformed.Add(data.Length > 200 ? data[..200] : data); continue; }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.TryGetProperty("error", out var e)) error = e.Clone();
                if (r.TryGetProperty("id", out var gid) && gid.ValueKind == JsonValueKind.String) id ??= gid.GetString();
                if (r.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String) provider ??= p.GetString();
                if (r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model ??= m.GetString();
                if (r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object) usage = u.Clone();
                if (!r.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in choices.EnumerateArray())
                {
                    if (c.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String) finish = f.GetString();
                    if (c.TryGetProperty("native_finish_reason", out var nf) && nf.ValueKind == JsonValueKind.String) native = nf.GetString();
                    if (c.TryGetProperty("error", out var ce)) error = ce.Clone();
                    if (!c.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
                    if (delta.TryGetProperty("content", out var t) && t.ValueKind == JsonValueKind.String) content.Append(t.GetString());
                    if (delta.TryGetProperty("reasoning", out var rs) && rs.ValueKind == JsonValueKind.String) reasoning.Append(rs.GetString());
                    if (delta.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                        foreach (var call in tc.EnumerateArray())
                        {
                            var index = call.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : calls.Count;
                            if (!calls.TryGetValue(index, out var acc)) acc = (null, null, new StringBuilder());
                            if (call.TryGetProperty("id", out var cid) && cid.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cid.GetString())) acc.Id ??= cid.GetString();
                            if (call.TryGetProperty("function", out var fnc) && fnc.ValueKind == JsonValueKind.Object)
                            {
                                if (fnc.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(nm.GetString())) acc.Name ??= nm.GetString();
                                if (fnc.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) acc.Args.Append(a.GetString());
                            }
                            calls[index] = acc;
                        }
                }
            }
        }
        return new(content.ToString(), reasoning.ToString(),
            calls.Select(kv => new F1QToolCall(kv.Key, kv.Value.Id, kv.Value.Name, kv.Value.Args.ToString())).ToArray(),
            finish, native, usage, error, id, provider, model, events, done, malformed);
    }

    public static decimal? Cost(JsonElement? usage) =>
        usage is { } u && u.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
    public static long Tokens(JsonElement? usage, string key) =>
        usage is { } u && u.TryGetProperty(key, out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0;
    public static long ReasoningTokens(JsonElement? usage) =>
        usage is { } u && u.TryGetProperty("completion_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object &&
        d.TryGetProperty("reasoning_tokens", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : 0;
}

internal sealed record F1QHttpObservation(int? StatusCode, byte[] Body, string? TransportError, long ElapsedMs, string? ContentType);

internal interface IF1QTransport
{
    Task<F1QHttpObservation> SendAsync(byte[] body, CancellationToken ct);
}

/// <summary>Single-attempt OpenRouter chat transport. Records raw response bytes exactly; never logs headers.</summary>
internal sealed class P7F1QOpenRouterTransport(Func<string> readKey, TimeSpan timeout) : IF1QTransport, IDisposable
{
    public const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<F1QHttpObservation> SendAsync(byte[] body, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        var key = readKey();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENROUTER_KEY_MISSING");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            string? error = null;
            try { await stream.CopyToAsync(buffer, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException) { error = "STREAM_READ_FAILED:" + ex.GetType().Name; }
            return new((int)response.StatusCode, buffer.ToArray(), error, watch.ElapsedMilliseconds, response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new(null, [], "TRANSPORT_FAILED:" + ex.GetType().Name, watch.ElapsedMilliseconds, null);
        }
    }

    public void Dispose() => http.Dispose();
}

internal sealed record F1QBodyPolicy(string Model, string ProviderTag, int MaxTokens, bool JsonObjectResponse, int ToolSet = 1);

internal sealed record F1QRequestSpec(string Handle, string Case, F1QArm Arm, string SystemPrompt, string UserMessage,
    IReadOnlyList<F1QIssuedOccurrence> Issued, IReadOnlySet<string> InitiallyCitable, P7F1QEvidenceTools? Tools,
    byte[]? FrozenInitialBody, Func<string, F1QValidation>? ControlValidator, bool FenceNormalization = false);

internal sealed record F1QTurnRecord(int Turn, int Attempts, int? StatusCode, string? TransportError, string? FinishReason,
    int ToolCalls, decimal? CostUsd, long PromptTokens, long CompletionTokens, long ReasoningTokens, string RequestSha256,
    string ResponseSha256, string? Provider, string? Model, string? GenerationId, string ToolChoice);

internal sealed record F1QRunCaps(int MaxToolRounds = 3, int MaxCallsPerRound = 8, int MaxTransportAttemptsPerTurn = 2,
    decimal RunawayUsd = 5m, decimal AnomalousRequestUsd = 0.5m, decimal HardCapUsd = decimal.MaxValue,
    decimal WorstCasePromptUsdPerToken = 0.0000002m, decimal WorstCaseCompletionUsdPerToken = 0.0000008m)
{
    /// <summary>Worst-case charge of one call before it is sent: body bytes / 2 as a prompt-token upper bound and the
    /// full completion budget, both at the most expensive pricing tier of the pinned endpoint.</summary>
    public decimal WorstCaseUsd(int bodyBytes, int maxTokens) => bodyBytes / 2m * WorstCasePromptUsdPerToken + maxTokens * WorstCaseCompletionUsdPerToken;
}

internal sealed record F1QRequestOutcome(string Handle, string Status, string? FailureCode, IReadOnlyList<F1QTurnRecord> Turns,
    F1QValidation? Validation, IReadOnlyDictionary<string, int> ToolCallsByName, int InvalidToolCalls, decimal CostUsd,
    bool AllCostsReported, string? FinalResponseSha256);

/// <summary>
/// Bounded model-triggered evidence loop. tool_choice=auto for at most <see cref="F1QRunCaps.MaxToolRounds"/>
/// rounds, then one forced-answer turn with tool_choice=none. One identical-body retry per turn only on
/// transport failure (no status, non-200, unreadable or incomplete stream); never on contract or semantics.
/// Every request body, raw SSE, tool call and tool result is written create-new before it is parsed or used.
/// </summary>
internal sealed class P7F1QConversationRunner(IF1QTransport transport, F1QBodyPolicy policy, F1QRunCaps caps)
{
    public async Task<F1QRequestOutcome> RunAsync(F1QRequestSpec spec, string directory, Func<decimal> spentSoFar, CancellationToken ct)
    {
        if (Directory.Exists(directory)) throw new InvalidOperationException("F1Q_REQUEST_DIRECTORY_EXISTS_NO_REPLAY");
        Directory.CreateDirectory(directory);
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = spec.SystemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = spec.UserMessage },
        };
        var citable = new HashSet<string>(spec.InitiallyCitable, StringComparer.Ordinal);
        var coverage = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        var turns = new List<F1QTurnRecord>(); var byName = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int invalidCalls = 0, okCalls = 0; decimal cost = 0; var allCosts = true;
        F1QRequestOutcome Done(string status, string? code, F1QValidation? v, string? finalSha)
        {
            var outcome = new F1QRequestOutcome(spec.Handle, status, code, turns, v, byName, invalidCalls, cost, allCosts, finalSha);
            WriteNew(Path.Combine(directory, "request-receipt.json"), JsonSerializer.SerializeToUtf8Bytes(outcome, P7F1QEvidenceTools.WireJson));
            return outcome;
        }
        var maxTurns = spec.Tools is null ? 1 : caps.MaxToolRounds + 1;
        for (var turn = 1; turn <= maxTurns; turn++)
        {
            var toolChoice = spec.Tools is null ? "absent" : turn <= caps.MaxToolRounds ? "auto" : "none";
            var body = turn == 1 && spec.FrozenInitialBody is not null ? spec.FrozenInitialBody : Body(messages, spec.Tools is not null, toolChoice);
            if (turn == 1 && spec.FrozenInitialBody is not null && spec.Arm != F1QArm.Control &&
                !body.AsSpan().SequenceEqual(Body(messages, spec.Tools is not null, toolChoice)))
                throw new InvalidOperationException("F1Q_FROZEN_INITIAL_BODY_DRIFT");
            var turnDir = Path.Combine(directory, $"turn-{turn}");
            Directory.CreateDirectory(turnDir);
            if (spentSoFar() + cost + caps.WorstCaseUsd(body.Length, policy.MaxTokens) > caps.HardCapUsd)
                return Done("BUDGET_STOP", "PRE_CALL_WORST_CASE_EXCEEDS_CAP", null, null);
            WriteNew(Path.Combine(turnDir, "request.json"), body);
            F1QHttpObservation? obs = null; F1QStreamAssembly? asm = null; var attempts = 0;
            for (var attempt = 1; attempt <= caps.MaxTransportAttemptsPerTurn; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                attempts = attempt;
                obs = await transport.SendAsync(body, ct).ConfigureAwait(false);
                var attemptDir = Path.Combine(turnDir, $"attempt-{attempt}");
                Directory.CreateDirectory(attemptDir);
                WriteNew(Path.Combine(attemptDir, "response.sse"), obs.Body);
                var text = Encoding.UTF8.GetString(obs.Body);
                asm = P7F1QSse.Parse(text);
                WriteNew(Path.Combine(attemptDir, "observation.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    statusCode = obs.StatusCode, obs.TransportError, obs.ElapsedMs, obs.ContentType,
                    responseSha256 = SpatialCanonical.Hash(obs.Body), requestSha256 = SpatialCanonical.Hash(body),
                    assembled = asm, rawIsAuthority = true,
                }, P7F1QEvidenceTools.WireJson));
                var c = P7F1QSse.Cost(asm.Usage);
                if (c is null) allCosts = false; else cost += c.Value;
                if (!TransportFailed(obs, asm)) break;
            }
            turns.Add(new(turn, attempts, obs!.StatusCode, obs.TransportError, asm!.FinishReason, asm.ToolCalls.Count, P7F1QSse.Cost(asm.Usage),
                P7F1QSse.Tokens(asm.Usage, "prompt_tokens"), P7F1QSse.Tokens(asm.Usage, "completion_tokens"), P7F1QSse.ReasoningTokens(asm.Usage),
                SpatialCanonical.Hash(body), SpatialCanonical.Hash(obs.Body), asm.Provider, asm.Model, asm.GenerationId, toolChoice));
            if (TransportFailed(obs, asm)) return Done("TRANSPORT_FAILED", obs.TransportError ?? (obs.StatusCode != 200 ? $"HTTP_{obs.StatusCode}" : asm.Error is not null ? "STREAM_ERROR" : "STREAM_INCOMPLETE"), null, null);
            if (cost > caps.AnomalousRequestUsd) return Done("BUDGET_STOP", "ANOMALOUS_REQUEST_COST", null, null);
            if (spentSoFar() + cost > caps.RunawayUsd) return Done("BUDGET_STOP", "RUNAWAY_COST_GUARD", null, null);
            if (asm.ToolCalls.Count > 0)
            {
                if (spec.Tools is null) return Done("CONTRACT_FAILED", "TOOL_CALL_WITHOUT_TOOLS", null, null);
                if (toolChoice == "none") return Done("CONTRACT_FAILED", "TOOL_CALL_AFTER_TOOL_CAP", null, null);
                var assistantCalls = new JsonArray();
                var toolMessages = new List<JsonObject>();
                foreach (var (call, n) in asm.ToolCalls.Select((c, i) => (c, i + 1)))
                {
                    var evidenceId = $"E{turn}.{n}";
                    var callId = call.Id ?? $"f1q_call_{turn}_{n}";
                    F1QToolResult result;
                    if (n > caps.MaxCallsPerRound || call.Name is null || !spec.Tools.ToolNames.Contains(call.Name))
                    {
                        var code = n > caps.MaxCallsPerRound ? "PER_ROUND_CALL_CAP_EXCEEDED" : "UNKNOWN_TOOL";
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { evidenceId, tool = call.Name, status = "REJECTED", error = code }, P7F1QEvidenceTools.WireJson);
                        result = new(evidenceId, call.Name ?? "", call.Arguments, "REJECTED", code, bytes, SpatialCanonical.Hash(bytes), []);
                    }
                    else result = spec.Tools.Execute(evidenceId, call.Name, call.Arguments);
                    if (result.Status != "OK") invalidCalls++; else okCalls++;
                    byName[result.Name] = byName.GetValueOrDefault(result.Name) + 1;
                    if (result.Status == "OK") { citable.Add(evidenceId); coverage[evidenceId] = result.ReturnedAliases; foreach (var a in result.ReturnedAliases) citable.Add(a); }
                    WriteNew(Path.Combine(turnDir, $"tool-{n:D2}-{evidenceId}.json"), JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        evidenceId, toolCallId = callId, providerToolCallId = call.Id, name = call.Name, rawArguments = call.Arguments,
                        result.Status, result.ErrorCode, contentSha256 = result.ContentSha256,
                        content = Encoding.UTF8.GetString(result.Content), returnedAliases = result.ReturnedAliases,
                    }, P7F1QEvidenceTools.WireJson));
                    assistantCalls.Add(new JsonObject
                    {
                        ["id"] = callId, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name ?? "", ["arguments"] = call.Arguments },
                    });
                    toolMessages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = Encoding.UTF8.GetString(result.Content) });
                }
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = asm.Content.Length == 0 ? null : asm.Content, ["tool_calls"] = assistantCalls });
                foreach (var m in toolMessages) messages.Add(m);
                continue;
            }
            var final = Encoding.UTF8.GetBytes(asm.Content);
            WriteNew(Path.Combine(directory, "response.txt"), final);
            WriteNew(Path.Combine(directory, "raw-freeze.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                status = "RAW_FROZEN_BEFORE_VALIDATION", responseSha256 = SpatialCanonical.Hash(final), finishReason = asm.FinishReason,
                turns = turns.Select(t => new { t.Turn, t.RequestSha256, t.ResponseSha256 }),
            }, P7F1QEvidenceTools.WireJson));
            if (asm.FinishReason != "stop")
                return Done("CONTRACT_FAILED", "FINISH_REASON_" + (asm.FinishReason ?? "NULL").ToUpperInvariant(), null, SpatialCanonical.Hash(final));
            F1QValidation ValidateText(string text) => spec.ControlValidator is not null ? spec.ControlValidator(text)
                : spec.Arm == F1QArm.F1QEvidenceV2 ? P7F1QProtocolV2.Validate(text, spec.Issued, citable, coverage)
                : P7F1QProtocol.Validate(text, spec.Issued, citable);
            var validation = ValidateText(asm.Content);
            if (spec.FenceNormalization)
            {
                // Issue #6 Decision 4: strict RAW view is kept; the NORMALIZED view (one outer fence removed) is the
                // acceptance view. Raw response.txt is never rewritten.
                var fence = P7F1QFenceNormalization.Normalize(asm.Content);
                var normalized = fence.Applied ? ValidateText(fence.Normalized) : validation;
                WriteNew(Path.Combine(directory, "validation.raw.json"), JsonSerializer.SerializeToUtf8Bytes(validation, P7F1QEvidenceTools.WireJson));
                WriteNew(Path.Combine(directory, "normalization.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = P7F1QFenceNormalization.Version, fence.Applied, fence.Reason, rawStrictAccepted = validation.StrictAccepted,
                    rawFailureCode = validation.FailureCode, normalizedStrictAccepted = normalized.StrictAccepted, normalizedFailureCode = normalized.FailureCode,
                    rawResponseSha256 = SpatialCanonical.Hash(final), normalizedSha256 = fence.Applied ? SpatialCanonical.Hash(System.Text.Encoding.UTF8.GetBytes(fence.Normalized)) : null,
                }, P7F1QEvidenceTools.WireJson));
                validation = normalized;
            }
            WriteNew(Path.Combine(directory, "validation.json"), JsonSerializer.SerializeToUtf8Bytes(validation, P7F1QEvidenceTools.WireJson));
            // Mandatory-evidence arm: a final answer with no successful tool call fails the frozen requirement,
            // even when the JSON itself is valid. Validation is still written for diagnostics.
            if (spec.Arm == F1QArm.F1QToolsMandatory && okCalls == 0)
                return Done("CONTRACT_FAILED", "TOOL_EVIDENCE_REQUIRED_NOT_REQUESTED", validation with { StrictAccepted = false, FailureCode = "TOOL_EVIDENCE_REQUIRED_NOT_REQUESTED" }, SpatialCanonical.Hash(final));
            return Done(validation.StrictAccepted ? "ACCEPTED" : "CONTRACT_FAILED", validation.FailureCode, validation, SpatialCanonical.Hash(final));
        }
        throw new InvalidOperationException("F1Q_TURN_LOOP_EXHAUSTED");
    }

    private static bool TransportFailed(F1QHttpObservation obs, F1QStreamAssembly asm) =>
        obs.TransportError is not null || obs.StatusCode != 200 || asm.Error is not null || !asm.Done || asm.FinishReason is null;

    public byte[] Body(JsonArray messages, bool tools, string toolChoice)
    {
        var body = new JsonObject
        {
            ["model"] = policy.Model,
            ["messages"] = messages.DeepClone(),
            ["temperature"] = 0,
            ["max_tokens"] = policy.MaxTokens,
            ["reasoning"] = new JsonObject { ["enabled"] = true },
        };
        if (policy.JsonObjectResponse) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (tools)
        {
            body["tools"] = policy.ToolSet == 2 ? P7F1QEvidenceTools.DefinitionsV2() : P7F1QEvidenceTools.Definitions();
            body["tool_choice"] = toolChoice;
        }
        body["provider"] = new JsonObject
        {
            ["order"] = new JsonArray("alibaba"), ["allow_fallbacks"] = false, ["require_parameters"] = true,
            ["data_collection"] = "deny", ["zdr"] = false,
        };
        body["stream"] = true;
        body["usage"] = new JsonObject { ["include"] = true };
        return Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions { Encoder = P7F1QEvidenceTools.WireJson.Encoder }));
    }

    public static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true);
    }
}
