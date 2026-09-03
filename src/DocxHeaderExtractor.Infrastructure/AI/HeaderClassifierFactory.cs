using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>Provider composition for production hosts; no provider implementation is owned by Core.</summary>
public sealed class HeaderClassifierFactory : IHeaderClassifierFactory
{
    public async Task<IHeaderClassifier> CreateAsync(
        PipelineOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PrepareLocalModelProfile();
        options.Llama.ChunkTokenBudget = options.Chunking.TokenBudget;
        return options.Backend switch
        {
            InferenceBackend.OpenRouter => OpenRouterHeaderExtractor.CreateOwned(options.OpenRouter),
            InferenceBackend.LmStudio => LmStudioHeaderExtractor.CreateOwned(options.LmStudio),
            InferenceBackend.Sglang => SglangHeaderExtractor.CreateOwned(options.Sglang),
            _ => await LlamaHeaderExtractor.LoadAsync(options.Llama, ct),
        };
    }
}
