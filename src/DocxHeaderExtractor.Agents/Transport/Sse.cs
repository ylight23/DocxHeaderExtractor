using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Agents.Transport;

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
