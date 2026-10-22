using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Agents;

/// <summary>One request body, built once: the bytes measured, sent and hashed are this value.</summary>
internal sealed record PreparedRequest(string Body, int Bytes, string Sha256);

/// <summary>One tool call of an assistant turn, as the response states it.</summary>
internal sealed record ToolCall(string? Id, string? Name, string Arguments);

/// <summary>One assistant turn as the response states it. Content is the visible text (null when absent). Reasoning is carried for the
/// diagnostics hook only: no decision reads it.</summary>
internal sealed record ModelTurn(string? Content, IReadOnlyList<ToolCall> ToolCalls, string? FinishReason, string? Reasoning = null);

/// <summary>
/// The OpenRouter chat wire the agents use: the route that is qualified (model, provider lock, token cap), the request envelope and the reading
/// of a response. Nothing here knows a document, a heading or a tool; the agent that calls it supplies the system prompt, the payload and the
/// tool declarations.
/// </summary>
internal static class OpenRouterWire
{
    public const string ChatModel = "qwen/qwen3.7-flash";
    public const string ProviderTag = "alibaba";
    public static readonly string[] Ignore = ["alibaba/us-east-1"];
    public const int MaxTokens = 32768;

    static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>One POST body: model, messages (system prompt and payload as cache-marked text blocks, then the history), json_object, temperature
    /// 0, max_tokens, reasoning enabled, the tools, this turn's tool_choice, the locked provider block, streaming and usage.</summary>
    public static PreparedRequest Build(string systemPrompt, string payload, JsonArray history, string toolChoice, JsonArray tools)
    {
        if (toolChoice is not ("auto" or "none")) throw new ArgumentException("TOOL_CHOICE_INVALID", nameof(toolChoice));
        static JsonNode Content(string text) =>
            new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } });
        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = Content(systemPrompt) },
            new JsonObject { ["role"] = "user", ["content"] = Content(payload) });
        foreach (var m in history) messages.Add(m!.DeepClone());
        var body = new JsonObject
        {
            ["model"] = ChatModel,
            ["messages"] = messages,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["temperature"] = 0,
            ["max_tokens"] = MaxTokens,
            ["reasoning"] = new JsonObject { ["enabled"] = true },
            ["tools"] = tools,
            ["tool_choice"] = toolChoice,
            ["provider"] = JsonSerializer.SerializeToNode(new { only = new[] { ProviderTag }, ignore = Ignore, allow_fallbacks = false, require_parameters = true }, Options),
            ["stream"] = true,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        var text = body.ToJsonString(Options);
        var bytes = Encoding.UTF8.GetBytes(text);
        return new PreparedRequest(text, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Reads choices[0].message of a chat response: content and tool_calls, as given. Never repairs anything.</summary>
    public static (ModelTurn? Turn, string? Failure) ReadResponse(string responseJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(responseJson); }
        catch (JsonException) { return (null, "RESPONSE_NOT_JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return (null, "NO_CHOICES");
            var c0 = choices[0];
            if (!c0.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return (null, "NO_MESSAGE");
            var finish = c0.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;
            string? content = msg.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() : null;
            var calls = new List<ToolCall>();
            if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                foreach (var c in tc.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) return (null, "TOOL_CALL_NOT_OBJECT");
                    var id = c.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                    string? name = null; var args = "";
                    if (c.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object)
                    {
                        if (f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
                        if (f.TryGetProperty("arguments", out var a)) args = a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText();
                    }
                    calls.Add(new ToolCall(id, name, args));
                }
            if (calls.Count == 0 && finish == "tool_calls") return (null, "FINISH_TOOL_CALLS_WITHOUT_CALLS");
            if (calls.Count == 0 && content is null) return (null, "CONTENT_NOT_TEXT");
            var reasoning = msg.TryGetProperty("reasoning", out var rz) && rz.ValueKind == JsonValueKind.String ? rz.GetString() : null;
            return (new ModelTurn(content, calls, finish, reasoning), null);
        }
    }
}
