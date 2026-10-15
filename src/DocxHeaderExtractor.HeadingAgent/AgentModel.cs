using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The audited OpenRouter model of the agent (ported from the qualified V6 transport, SSE reader, SSE guard, usage audit and audited model): one
/// POST of the prepared bytes, no redirect, one timeout, the key only in the Authorization header. The streamed reply must end with [DONE] and
/// carry no error or unknown event; the response must come from Alibaba and qwen/qwen3.7-flash and pass the usage and finish audit; the provider
/// cost is recorded against the call's reserve. Any failure is a <see cref="ReplyFailure"/>; the raw bytes are not kept.
/// </summary>
internal sealed class AgentModel : IDisposable
{
    public const string Url = "https://openrouter.ai/api/v1/chat/completions";

    readonly HttpClient client;
    readonly string apiKey;
    readonly AgentBudget budget;
    readonly object usageGate = new();

    public AgentModel(HttpMessageHandler handler, string apiKey, AgentBudget budget, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API_KEY_MISSING", nameof(apiKey));
        client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(300) };
        this.apiKey = apiKey; this.budget = budget;
    }

    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public int ReasoningTokens { get; private set; }
    public int CachedTokens { get; private set; }

    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    public ModelTurn Turn(string unitId, int turn, PreparedRequest request)
    {
        var text = ReplyRaw(request);
        var (reply, failure) = ReadResponse(text);
        if (reply is null) throw new ReplyFailure("CONTENT:" + failure);
        return reply;
    }

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>One audited call; returns the chat-completion view assembled from the stream.</summary>
    string ReplyRaw(PreparedRequest request)
    {
        var reserve = AgentBudget.AttemptUsd(request.Bytes);
        var (outcome, body) = Send(request);
        if (outcome != "HTTP_OK")
        {
            if (outcome is "BOUNDARY_SHA_MISMATCH" or "PREPARED_SHA_MISMATCH") budget.Halt(outcome);
            budget.RecordActual(null, reserve);
            throw new ReplyFailure("TRANSPORT:" + outcome);
        }
        var raw = body ?? "";
        if (raw.Contains(apiKey, StringComparison.Ordinal))
        {
            budget.Halt("API_KEY_ECHOED");
            throw new ReplyFailure("API_KEY_ECHOED");
        }
        var asm = Sse.Parse(raw);
        var broken = Sse.Guard(raw) ?? (asm.Error ? "STREAM_ERROR" : !asm.Done ? "STREAM_INCOMPLETE" : asm.MalformedEvents > 0 ? "STREAM_MALFORMED" : null);
        if (broken is not null)
        {
            budget.RecordActual(asm.Cost, reserve);
            throw new ReplyFailure("TRANSPORT:" + broken);
        }
        var text = asm.View();
        var audit = Usage.Check(text, request.Bytes);
        budget.RecordActual(audit.ProviderCostUsd, reserve);
        lock (usageGate)
        {
            PromptTokens += audit.PromptTokens ?? 0; CompletionTokens += audit.CompletionTokens ?? 0;
            ReasoningTokens += audit.ReasoningTokens ?? 0; CachedTokens += audit.CachedTokens ?? 0;
        }
        if (!string.Equals(asm.Provider, "Alibaba", StringComparison.Ordinal) || asm.Model != ChatModel)
        {
            budget.Halt("ENDPOINT_OR_MODEL_UNEXPECTED");
            throw new ReplyFailure("ENDPOINT_OR_MODEL_UNEXPECTED");
        }
        if (!(audit.Codes.Count == 1 && audit.Codes[0] == "OK")) throw new ReplyFailure("AUDIT:" + string.Join(",", audit.Codes));
        return text;
    }

    (string Outcome, string? Body) Send(PreparedRequest request)
    {
        var bytes = Encoding.UTF8.GetBytes(request.Body);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != request.Sha256) return ("PREPARED_SHA_MISMATCH", null);
        using var message = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new ByteArrayContent(bytes) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        HttpResponseMessage response;
        try { response = client.SendAsync(message).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return ("TIMEOUT_OR_CANCELLED", null); }
        catch (HttpRequestException) { return ("TRANSPORT_ERROR", null); }
        using (response)
        {
            var status = (int)response.StatusCode;
            byte[] bodyBytes;
            try { bodyBytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult(); }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { return ("TRANSPORT_ERROR", null); }
            string? body;
            try { body = StrictUtf8.GetString(bodyBytes); }
            catch (DecoderFallbackException) { body = null; }
            var outcome = status switch
            {
                >= 200 and < 300 => "HTTP_OK",
                >= 300 and < 400 => "REDIRECT_REFUSED",
                429 => "RATE_LIMITED_NO_RETRY",
                // The provider account has no credit left: not retried, and named apart so the failure says what to fix.
                402 => "PAYMENT_REQUIRED",
                >= 500 => "SERVER_ERROR_NO_RETRY",
                >= 400 => "CLIENT_ERROR",
                _ => "UNEXPECTED_STATUS",
            };
            if (outcome == "HTTP_OK" && body is null) outcome = "RESPONSE_NOT_UTF8";
            return (outcome, body);
        }
    }

    public void Dispose() => client.Dispose();
}

/// <summary>The streamed (SSE) reply: assembly of the data events, and the line-level guard (ported from the qualified F1Q reader and V6 guard).</summary>
internal static class Sse
{
    public sealed record Call(int Index, string? Id, string? Name, string Arguments);

    public sealed record Assembly(string Content, IReadOnlyList<Call> ToolCalls, string? FinishReason, string? NativeFinishReason, JsonElement? Usage,
        bool Error, string? GenerationId, string? Provider, string? Model, bool Done, int MalformedEvents, string Reasoning)
    {
        public double? Cost => Usage is { } u && u.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;

        /// <summary>The chat-completion view of the stream. The reasoning travels as message.reasoning, for the diagnostics hook only.</summary>
        public string View()
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = Content.Length == 0 ? null : Content };
            if (Reasoning.Length > 0) message["reasoning"] = Reasoning;
            if (ToolCalls.Count > 0)
                message["tool_calls"] = new JsonArray(ToolCalls.Select(t => (JsonNode)new JsonObject
                {
                    ["id"] = t.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = t.Name, ["arguments"] = t.Arguments },
                }).ToArray());
            return new JsonObject
            {
                ["id"] = GenerationId, ["provider"] = Provider, ["model"] = Model,
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = FinishReason, ["native_finish_reason"] = NativeFinishReason, ["message"] = message }),
                ["usage"] = Usage is { } u ? JsonNode.Parse(u.GetRawText()) : null,
            }.ToJsonString();
        }
    }

    public static Assembly Parse(string raw)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        string? finish = null, native = null, id = null, provider = null, model = null;
        JsonElement? usage = null; var error = false; var done = false; var malformed = 0;
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].TrimStart();
            if (data == "[DONE]") { done = true; continue; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { malformed++; continue; }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.TryGetProperty("error", out _)) error = true;
                if (r.TryGetProperty("id", out var gid) && gid.ValueKind == JsonValueKind.String) id ??= gid.GetString();
                if (r.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String) provider ??= p.GetString();
                if (r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model ??= m.GetString();
                if (r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object) usage = u.Clone();
                if (!r.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in choices.EnumerateArray())
                {
                    if (c.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String) finish = f.GetString();
                    if (c.TryGetProperty("native_finish_reason", out var nf) && nf.ValueKind == JsonValueKind.String) native = nf.GetString();
                    if (c.TryGetProperty("error", out _)) error = true;
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
        return new(content.ToString(), calls.Select(kv => new Call(kv.Key, kv.Value.Id, kv.Value.Name, kv.Value.Args.ToString())).ToArray(),
            finish, native, usage, error, id, provider, model, done, malformed, reasoning.ToString());
    }

    /// <summary>Only data events, blank lines and comments are accepted; an event line, an unknown field, data after [DONE] or a data event
    /// without choices or error is refused. Returns a failure code, or null.</summary>
    public static string? Guard(string raw)
    {
        var done = false;
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            if (field == "event")
            {
                var name = colon < 0 ? "" : line[(colon + 1)..].Trim();
                return name.Equals("error", StringComparison.OrdinalIgnoreCase) ? "STREAM_ERROR_EVENT" : "STREAM_NAMED_EVENT";
            }
            if (field != "data") return "STREAM_FIELD_UNKNOWN";
            var data = colon < 0 ? "" : line[(colon + 1)..].Trim();
            if (data == "[DONE]") { done = true; continue; }
            if (done) return "STREAM_DATA_AFTER_DONE";
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return "STREAM_EVENT_SHAPE_UNKNOWN";
                if (!root.TryGetProperty("choices", out _) && !root.TryGetProperty("error", out _)) return "STREAM_EVENT_SHAPE_UNKNOWN";
            }
        }
        return null;
    }
}

/// <summary>The usage and finish audit of one response (ported from the qualified V6 usage audit, agent turns).</summary>
internal static class Usage
{
    public sealed record Audit(IReadOnlyList<string> Codes, int? PromptTokens, int? CompletionTokens, int? ReasoningTokens, int? CachedTokens, double? ProviderCostUsd);

    public static Audit Check(string responseJson, int requestBytes)
    {
        var codes = new List<string>();
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        string? finish = null, content = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString();
            if (c0.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String) content = ct.GetString();
        }
        else codes.Add("CHOICES_MISSING");
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            codes.Add("USAGE_MISSING");
            return new Audit(codes, null, null, null, null, null);
        }
        int? prompt = IntOrNull(usage, "prompt_tokens");
        int? completion = IntOrNull(usage, "completion_tokens");
        int? reasoning = null, cached = null;
        if (usage.TryGetProperty("completion_tokens_details", out var det) && det.ValueKind == JsonValueKind.Object) reasoning = IntOrNull(det, "reasoning_tokens");
        if (usage.TryGetProperty("prompt_tokens_details", out var pdet) && pdet.ValueKind == JsonValueKind.Object) cached = IntOrNull(pdet, "cached_tokens");
        if (prompt is null || completion is null) codes.Add("USAGE_FIELDS_MISSING");
        if (completion is not null && reasoning is not null && completion - reasoning < 0) codes.Add("USAGE_INCONSISTENT");
        if (completion is not null && completion > MaxTokens) codes.Add("COMPLETION_OVER_CAP");
        if (prompt is not null && prompt > requestBytes) codes.Add("PROMPT_TOKENS_EXCEED_BYTES");
        if (finish is null) codes.Add("FINISH_REASON_MISSING");
        else if (finish == "length")
        {
            codes.Add("FINISH_LENGTH");
            if (string.IsNullOrWhiteSpace(content) && reasoning is not null && completion is not null && reasoning >= completion - 1) codes.Add("REASONING_EXHAUSTED");
        }
        else if (finish is not ("stop" or "tool_calls")) codes.Add("FINISH_REASON_UNEXPECTED");
        double? providerCost = usage.TryGetProperty("cost", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : null;
        if (codes.Count == 0) codes.Add("OK");
        return new Audit(codes, prompt, completion, reasoning, cached, providerCost);
    }

    static int? IntOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
