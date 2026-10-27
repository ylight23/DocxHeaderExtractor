using System.Text.Json;
using static DocxHeaderExtractor.Agents.OpenRouterWire;

namespace DocxHeaderExtractor.Agents.Transport;

/// <summary>The usage and finish audit of one response (ported from the qualified V6 usage audit, agent turns).</summary>
internal static class Usage
{
    public sealed record Audit(IReadOnlyList<string> Codes, int? PromptTokens, int? CompletionTokens, int? ReasoningTokens, int? CachedTokens, double? ProviderCostUsd);

    public static Audit Check(string responseJson, int requestBytes, int completionCap = MaxTokens)
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
        if (completion is not null && completion > completionCap) codes.Add("COMPLETION_OVER_CAP");
        // Native prompt tokens and HTTP body bytes have different units (especially with images).
        if (prompt < 0 || completion < 0 || reasoning < 0 || cached < 0) codes.Add("USAGE_INCONSISTENT");
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
