using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>Provider composition for production hosts; no provider implementation is owned by Core.</summary>
public sealed class HeaderClassifierFactory : IHeaderClassifierFactory
{
    private readonly InferenceProviderSelection _selection;

    public HeaderClassifierFactory(InferenceProviderSelection? selection = null)
    {
        _selection = selection ?? InferenceProviderSelection.LocalDefault();
    }

    public bool SendsDataExternally => _selection.SendsDataExternally;

    public async Task<IHeaderClassifier> CreateAsync(
        PipelineOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _selection.LocalModel.ChunkTokenBudget = options.Chunking.TokenBudget;
        if (_selection.Backend == InferenceBackend.Local && !string.IsNullOrWhiteSpace(_selection.LocalModel.ModelPath))
            _selection.LocalModel.ApplyRecommendedModelProfile(options.Chunking);

        return _selection.Backend switch
        {
            InferenceBackend.OpenRouter => OpenRouterHeaderExtractor.CreateOwned(_selection.Remote),
            InferenceBackend.LmStudio => LmStudioHeaderExtractor.CreateOwned(_selection.Remote),
            InferenceBackend.Sglang => SglangHeaderExtractor.CreateOwned(_selection.Remote),
            _ => await LlamaHeaderExtractor.LoadAsync(_selection.LocalModel, ct),
        };
    }
}
