using DocxHeaderExtractor.Infrastructure.AI.QualifiedInference;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// The one qualified provider envelope used by the PDF production policy: OpenRouter, qwen/qwen3.7-flash through
/// Alibaba, reasoning enabled, JSON-object response. Semantic protocols give this composer immutable prompt and
/// user bytes; they never name an HTTP provider or model. Document processing sees only
/// <see cref="IFrozenInferenceRequestComposer"/>.
/// </summary>
public sealed class OpenRouterQwen37InferenceRequestComposer : IFrozenInferenceRequestComposer
{
    private static readonly V5ProviderEnvelope Envelope = new(
        "qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    {
        UsageInclude = true,
        OpenRouterResponseCacheDisabled = true,
    };

    public byte[] Build(string systemPrompt, string userMessage, int maxTokens) =>
        OpenRouterQwen37JsonObjectCarrierV2_1
            .BuildFromRawReasoningEnabled(systemPrompt, userMessage, maxTokens, Envelope)
            .PayloadBytes;
}
