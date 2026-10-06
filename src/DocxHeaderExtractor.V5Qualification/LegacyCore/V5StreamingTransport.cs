using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

public sealed record V5StreamingTransportRequest(
    string RequestBody,
    bool Stream = true,
    bool IncludeUsage = true,
    int DeadlineSeconds = 300,
    int MaxTransportRetries = 1)
{
    public void Validate()
    {
        if (!Stream || !IncludeUsage) throw new InvalidOperationException("v5-streaming-pins-invalid");
        if (DeadlineSeconds <= 0 || MaxTransportRetries < 0) throw new InvalidOperationException("v5-streaming-budget-invalid");
    }
}

public enum V5StreamFailure
{
    NONE,
    HTTP_PROVIDER_ERROR,
    TIMEOUT,
    INTERRUPTED_STREAM,
    EOF_BEFORE_TERMINAL,
    MALFORMED_SSE,
    TERMINAL_EVENT_INVALID_LOCAL_JSON,
}

public sealed record V5StreamTelemetry(
    int EventCount,
    int ReasoningChunkCount,
    int ContentChunkCount,
    int? PromptTokens,
    int? CompletionTokens,
    int? ReasoningTokens,
    string? FinishReason,
    bool CleanEof,
    bool DoneMarker,
    V5StreamFailure Failure,
    long? FirstEventMs = null,
    long? FirstReasoningMs = null,
    long? FirstContentMs = null,
    long? TerminalMs = null);

public sealed record V5ReassembledStream(
    string Reasoning,
    string Content,
    V5StreamTelemetry Telemetry);

/// <summary>Provider-neutral SSE framing and reassembly. It performs no HTTP or provider call.</summary>
public static class V5StreamingTransportAdapter
{
    public static IReadOnlyList<string> FrameSse(string networkText)
    {
        ArgumentNullException.ThrowIfNull(networkText);
        var normalized = networkText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Split("\n\n", StringSplitOptions.None)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToArray();
    }

    public static V5ReassembledStream Reassemble(IEnumerable<string> rawEvents, bool cleanEof = true)
    {
        ArgumentNullException.ThrowIfNull(rawEvents);
        var reasoning = new StringBuilder();
        var content = new StringBuilder();
        var eventCount = 0;
        var reasoningChunks = 0;
        var contentChunks = 0;
        var finishReason = default(string);
        var promptTokens = default(int?);
        var completionTokens = default(int?);
        var reasoningTokens = default(int?);
        var done = false;
        foreach (var raw in rawEvents)
        {
            var data = raw.Split('\n').Select(line => line.TrimEnd()).Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].TrimStart()).FirstOrDefault();
            if (data is null) continue;
            if (data == "[DONE]") { done = true; continue; }
            eventCount++;
            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                        finishReason = finish.GetString();
                    if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        if (delta.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
                        { reasoning.Append(r.GetString()); reasoningChunks++; }
                        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        { content.Append(c.GetString()); contentChunks++; }
                    }
                }
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    promptTokens = ReadInt(usage, "prompt_tokens");
                    completionTokens = ReadInt(usage, "completion_tokens");
                    if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                        reasoningTokens = ReadInt(details, "reasoning_tokens");
                }
            }
            catch (JsonException)
            {
                return new V5ReassembledStream(reasoning.ToString(), content.ToString(),
                    new V5StreamTelemetry(eventCount, reasoningChunks, contentChunks, promptTokens, completionTokens,
                        reasoningTokens, finishReason, cleanEof, done, V5StreamFailure.MALFORMED_SSE));
            }
        }
        var failure = !cleanEof ? V5StreamFailure.INTERRUPTED_STREAM
            : finishReason is null ? V5StreamFailure.EOF_BEFORE_TERMINAL
            : V5StreamFailure.NONE;
        return new V5ReassembledStream(reasoning.ToString(), content.ToString(),
            new V5StreamTelemetry(eventCount, reasoningChunks, contentChunks, promptTokens, completionTokens,
                reasoningTokens, finishReason, cleanEof, done, failure));
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
}
