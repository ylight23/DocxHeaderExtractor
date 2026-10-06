using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>The frozen OpenRouter json_object request carrier used by the qualified PDF route.</summary>
public static class OpenRouterQwen37JsonObjectCarrierV2_1
{
    public static V5ProviderRequestBodyV2_1 BuildFromRaw(
        string systemPrompt, string userMessage, int maxCompletionTokens, V5ProviderEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(envelope);
        if (maxCompletionTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens));
        // Code-authored prompt only; userMessage carries source text and is sent as given.
        systemPrompt = QualifiedPromptText.Canonicalize(systemPrompt);

        object provider = string.IsNullOrWhiteSpace(envelope.Provider)
            ? new { zdr = false, data_collection = "deny", require_parameters = true, allow_fallbacks = true }
            : new { order = new[] { envelope.Provider }, allow_fallbacks = false, require_parameters = true, data_collection = "deny", zdr = false };
        var body = new
        {
            model = envelope.Model,
            temperature = 0,
            max_tokens = maxCompletionTokens,
            reasoning = new { effort = envelope.Reasoning },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            response_format = new { type = envelope.ResponseFormat },
            provider,
            stream = envelope.Streaming,
            usage = new { include = envelope.UsageInclude },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, CanonicalJson.Options);
        return new V5ProviderRequestBodyV2_1(bytes, Hashing.Sha256(Encoding.UTF8.GetString(bytes)), bytes.Length);
    }

    public static V5ProviderRequestBodyV2_1 BuildFromRawReasoningEnabled(
        string systemPrompt, string userMessage, int maxCompletionTokens, V5ProviderEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(envelope);
        if (maxCompletionTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens));
        // Code-authored prompt only; userMessage carries source text and is sent as given.
        systemPrompt = QualifiedPromptText.Canonicalize(systemPrompt);

        object provider = string.IsNullOrWhiteSpace(envelope.Provider)
            ? new { zdr = false, data_collection = "deny", require_parameters = true, allow_fallbacks = true }
            : new { order = new[] { envelope.Provider }, allow_fallbacks = false, require_parameters = true, data_collection = "deny", zdr = false };
        var body = new
        {
            model = envelope.Model,
            temperature = 0,
            max_tokens = maxCompletionTokens,
            reasoning = new { enabled = true },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            response_format = new { type = envelope.ResponseFormat },
            provider,
            stream = envelope.Streaming,
            usage = new { include = envelope.UsageInclude },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, CanonicalJson.Options);
        return new V5ProviderRequestBodyV2_1(bytes, Hashing.Sha256(Encoding.UTF8.GetString(bytes)), bytes.Length);
    }
}
