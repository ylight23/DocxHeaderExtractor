using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>Small shared provider envelope used by the qualified PDF inference route.</summary>
public sealed record V5ProviderEnvelope(
    string Model,
    string Provider,
    string Reasoning,
    bool Streaming,
    string ResponseFormat,
    int TimeoutSeconds)
{
    [JsonPropertyName("usageInclude")]
    public bool UsageInclude { get; init; } = true;

    [JsonPropertyName("openRouterResponseCacheDisabled")]
    public bool OpenRouterResponseCacheDisabled { get; init; } = true;

    [JsonPropertyName("explicitCacheControl")]
    public string? ExplicitCacheControl { get; init; }
}

/// <summary>Minimal production wire body DTO, shared by the carrier and the live adapter.</summary>
public sealed record V5ProviderRequestBodyV2_1(byte[] PayloadBytes, string Hash, int Bytes)
{
    public static V5ProviderRequestBodyV2_1 Build(
        string systemPrompt, string userMessage, int maxCompletionTokens, V5ProviderEnvelope envelope) =>
        OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(systemPrompt, userMessage, maxCompletionTokens, envelope);
}
