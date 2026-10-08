using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Web composition for a document run. DOCX keeps the web host's named HTTP clients and cached
/// local model; PDF delegates to the separately-qualified production policy.
/// </summary>
internal sealed class WebInferenceTransportFactory(
    InferenceProviderSelection selection,
    IHttpClientFactory httpClients,
    LlamaModelCache localModels) : IInferenceTransportFactory
{
    private readonly InferenceTransportFactory _pdfFactory = new(selection);

    public bool SendsDataExternally => selection.SendsDataExternally;

    public async Task<IInferenceTransport> CreateAsync(CancellationToken ct = default) =>
        selection.Backend switch
        {
            InferenceBackend.OpenRouter => new OpenRouterInferenceTransport(
                httpClients.CreateClient("OpenRouter"), selection.Remote),
            InferenceBackend.LmStudio => new LmStudioInferenceTransport(
                httpClients.CreateClient("LmStudio"), selection.Remote),
            InferenceBackend.Local => new BorrowedInferenceTransport(
                await localModels.GetAsync(selection.LocalModel, ct).ConfigureAwait(false)),
            _ => await new InferenceTransportFactory(selection).CreateAsync(ct).ConfigureAwait(false),
        };

    public Task<IInferenceTransport> CreatePdfProductionAsync(CancellationToken ct = default) =>
        _pdfFactory.CreatePdfProductionAsync(ct);

    /// <summary>The cache, not an individual document pipeline, owns the local model lifetime.</summary>
    private sealed class BorrowedInferenceTransport(IInferenceTransport inner) : IInferenceTransport
    {
        public string ModelName => inner.ModelName;
        public int ContextSize => inner.ContextSize;
        public string RuntimeDescription => inner.RuntimeDescription;
        public int SharedPrefixTokens => inner.SharedPrefixTokens;

        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage,
            CancellationToken ct = default, int expectedItemCount = 0) =>
            inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount);

        public void Dispose() { }
    }
}
