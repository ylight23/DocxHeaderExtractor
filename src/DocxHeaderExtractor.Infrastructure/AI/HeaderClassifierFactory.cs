using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

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
        options.LocalModel.ChunkTokenBudget = options.Chunking.TokenBudget;
        return options.Backend switch
        {
            InferenceBackend.OpenRouter => OpenRouterHeaderExtractor.CreateOwned(options.Remote),
            InferenceBackend.LmStudio => LmStudioHeaderExtractor.CreateOwned(options.Remote),
            InferenceBackend.Sglang => SglangHeaderExtractor.CreateOwned(options.Remote),
            _ => await LlamaHeaderExtractor.LoadAsync(options.LocalModel, ct),
        };
    }
}
