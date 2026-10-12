using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Offline audit of one chat response's usage object (Gate T0). It reads a response that has already been received and reports what it
/// shows. It makes no call. The fields it reads are the ones OpenRouter documents: usage.prompt_tokens, usage.completion_tokens,
/// usage.completion_tokens_details.reasoning_tokens, and choices[0].finish_reason. Where a field is absent the audit says so; it never
/// guesses. Prices are the alibaba route tiers recorded in the Gate E1 raw response (SHA-256 in the T0 audit).
/// </summary>
internal static class P7F1V6Usage
{
    /// <summary>Route prices in USD per token, from the E1 response for the locked route.</summary>
    public sealed record Tier(int MinPromptTokens, double Prompt, double Completion);

    public static readonly Tier[] AlibabaTiers =
    [
        new(0, 0.00000003, 0.00000013),
        new(32000, 0.0000001, 0.0000004),
        new(256000, 0.0000002, 0.0000008),
    ];

    /// <summary>ComputedCostUsd is this audit's estimate from the route price table. ProviderCostUsd is usage.cost as the provider reported
    /// it, when it did. The two are kept apart and never overwrite each other.</summary>
    public sealed record Audit(IReadOnlyList<string> Codes, int? PromptTokens, int? CompletionTokens, int? ReasoningTokens,
        string? FinishReason, int? VisibleTokens, double? ComputedCostUsd, double? ProviderCostUsd);

    /// <summary>The tier is chosen on the prompt tokens actually billed for the request, as the E1 metadata describes the override rule
    /// (its per-request application is still to be confirmed; the audit states it as an assumption).</summary>
    public static double? Cost(int promptTokens, int completionTokens)
    {
        var tier = AlibabaTiers.Last(t => promptTokens >= t.MinPromptTokens);
        return promptTokens * tier.Prompt + completionTokens * tier.Completion;
    }

    /// <summary>Checks one response. requestBytes is the UTF-8 size of the prepared body that was sent; maxTokens is the max_tokens of that request.
    /// Tokens are at most bytes for UTF-8 text, so prompt_tokens above requestBytes is an inconsistency, not a large request.</summary>
    public static Audit Check(string responseJson, int requestBytes, int maxTokens)
    {
        var codes = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(responseJson); }
        catch (JsonException) { return new Audit(["RESPONSE_NOT_JSON"], null, null, null, null, null, null, null); }
        using (doc)
        {
            var root = doc.RootElement;
            string? finish = null, content = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var c0 = choices[0];
                if (c0.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString();
                if (c0.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String)
                    content = ct.GetString();
            }
            else codes.Add("CHOICES_MISSING");

            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                codes.Add("USAGE_MISSING");
                return new Audit(codes, null, null, null, finish, null, null, null);
            }

            int? prompt = IntOrNull(usage, "prompt_tokens");
            int? completion = IntOrNull(usage, "completion_tokens");
            int? reasoning = null;
            if (usage.TryGetProperty("completion_tokens_details", out var det) && det.ValueKind == JsonValueKind.Object)
                reasoning = IntOrNull(det, "reasoning_tokens");

            if (prompt is null || completion is null) codes.Add("USAGE_FIELDS_MISSING");
            int? visible = null;
            if (completion is not null && reasoning is not null)
            {
                visible = completion - reasoning;
                if (visible < 0) codes.Add("USAGE_INCONSISTENT");
            }
            if (completion is not null && completion > maxTokens) codes.Add("COMPLETION_OVER_CAP");
            if (prompt is not null && prompt > requestBytes) codes.Add("PROMPT_TOKENS_EXCEED_BYTES");

            if (finish is null) codes.Add("FINISH_REASON_MISSING");
            else if (finish == "length")
            {
                codes.Add("FINISH_LENGTH");
                // Reasoning spent the whole budget and nothing visible was written.
                if (string.IsNullOrWhiteSpace(content) && reasoning is not null && completion is not null && reasoning >= completion - 1)
                    codes.Add("REASONING_EXHAUSTED");
            }
            else if (finish != "stop") codes.Add("FINISH_REASON_UNEXPECTED");

            double? cost = prompt is not null && completion is not null ? Cost(prompt.Value, completion.Value) : null;
            double? providerCost = usage.TryGetProperty("cost", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : null;
            if (codes.Count == 0) codes.Add("OK");
            return new Audit(codes, prompt, completion, reasoning, finish, visible, cost, providerCost);
        }
    }

    static int? IntOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
