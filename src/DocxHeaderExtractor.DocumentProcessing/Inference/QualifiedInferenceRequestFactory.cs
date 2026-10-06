using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Inference;

/// <summary>
/// The one qualified provider envelope used by the PDF production policy. Semantic protocols give
/// this factory immutable prompt and user bytes; they never name an HTTP provider or model.
/// </summary>
internal static class QualifiedInferenceRequestFactory
{
    private static readonly V5ProviderEnvelope Envelope = new(
        "qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    {
        UsageInclude = true,
        OpenRouterResponseCacheDisabled = true,
    };

    internal static byte[] Build(string systemPrompt, string userMessage, int maxTokens) =>
        OpenRouterQwen37JsonObjectCarrierV2_1
            .BuildFromRawReasoningEnabled(systemPrompt, userMessage, maxTokens, Envelope)
            .PayloadBytes;
}
