using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>Provider composition for production hosts; no provider implementation is owned by Core.</summary>
public sealed class InferenceTransportFactory : IInferenceTransportFactory
{
    private readonly InferenceProviderSelection _selection;

    public InferenceTransportFactory(InferenceProviderSelection? selection = null)
    {
        _selection = selection ?? InferenceProviderSelection.LocalDefault();
    }

    public bool SendsDataExternally => _selection.SendsDataExternally;

    public async Task<IInferenceTransport> CreateAsync(
        PipelineOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _selection.Backend switch
        {
            InferenceBackend.OpenRouter => OpenRouterInferenceTransport.CreateOwned(_selection.Remote),
            InferenceBackend.LmStudio => LmStudioInferenceTransport.CreateOwned(_selection.Remote),
            InferenceBackend.Sglang => SglangInferenceTransport.CreateOwned(_selection.Remote),
            _ => await LlamaInferenceTransport.LoadAsync(_selection.LocalModel, ct),
        };
    }

    /// <summary>
    /// The promoted PDF route is deliberately narrower than the general heading transport
    /// factory. Its frozen F1/G2A/H2-C bodies were qualified only on OpenRouter's Alibaba route
    /// for qwen/qwen3.7-flash, so selecting a local or alternate remote backend must fail before
    /// a PDF provider call is attempted.
    /// </summary>
    public Task<IInferenceTransport> CreatePdfProductionAsync(
        PipelineOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_selection.Backend is not InferenceBackend.OpenRouter)
            throw new InvalidOperationException(
                $"PDF_PRODUCTION_PROVIDER_UNSUPPORTED: requires OpenRouter/{RemoteInferenceOptions.DefaultModel} " +
                $"via {RemoteInferenceOptions.DefaultProviderRoute}; selected {Describe(_selection.Backend)}.");

        if (!string.Equals(_selection.Remote.Model, RemoteInferenceOptions.DefaultModel, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PDF_PRODUCTION_MODEL_UNSUPPORTED: requires {RemoteInferenceOptions.DefaultModel}; " +
                $"selected {_selection.Remote.Model}.");

        if (!string.Equals(_selection.Remote.OpenRouterProviderRoute, RemoteInferenceOptions.DefaultProviderRoute,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PDF_PRODUCTION_PROVIDER_ROUTE_UNSUPPORTED: requires {RemoteInferenceOptions.DefaultProviderRoute}; " +
                $"selected {_selection.Remote.OpenRouterProviderRoute ?? "<automatic>"}.");

        IInferenceTransport transport = new PdfProductionOpenRouterInferenceTransport(
            OpenRouterInferenceTransport.CreateOwned(_selection.Remote));
        return Task.FromResult(transport);
    }

    private static string Describe(InferenceBackend backend) => backend switch
    {
        InferenceBackend.LmStudio => "LM Studio",
        InferenceBackend.Sglang => "SGLang",
        InferenceBackend.Local => "local Llama",
        _ => backend.ToString(),
    };

    /// <summary>Capability/authorization wrapper for the only qualified PDF production transport.</summary>
    private sealed class PdfProductionOpenRouterInferenceTransport(OpenRouterInferenceTransport inner)
        : IPdfProductionAuthorizedInferenceTransport
    {
        public string PdfProductionProvider => "OpenRouter/Alibaba";
        public string PdfProductionModel => RemoteInferenceOptions.DefaultModel;
        public string ModelName => inner.ModelName;
        public int ContextSize => inner.ContextSize;
        public string RuntimeDescription => inner.RuntimeDescription;
        public int SharedPrefixTokens => inner.SharedPrefixTokens;
        public IFrozenInferenceRequestComposer RequestComposer => inner.RequestComposer;

        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage,
            CancellationToken ct = default, int expectedItemCount = 0) =>
            inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount);

        public Task<FrozenInferenceResult> ExecuteFrozenRequestAsync(
            byte[] providerBody, int maxTokens, string systemPrompt, string userMessage,
            CancellationToken cancellationToken = default) =>
            inner.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken);

        public void Dispose() => inner.Dispose();
    }
}
