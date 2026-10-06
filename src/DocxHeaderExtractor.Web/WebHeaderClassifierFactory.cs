using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Web composition for a document run. DOCX keeps the web host's named HTTP clients and cached
/// local model; PDF delegates to the separately-qualified production policy.
/// </summary>
internal sealed class WebHeaderClassifierFactory(
    InferenceProviderSelection selection,
    IHttpClientFactory httpClients,
    LlamaModelCache localModels) : IHeaderClassifierFactory
{
    private readonly HeaderClassifierFactory _pdfFactory = new(selection);

    public bool SendsDataExternally => selection.SendsDataExternally;

    public async Task<IHeaderClassifier> CreateAsync(PipelineOptions options, CancellationToken ct = default) =>
        selection.Backend switch
        {
            InferenceBackend.OpenRouter => new OpenRouterHeaderExtractor(
                httpClients.CreateClient("OpenRouter"), selection.Remote),
            InferenceBackend.LmStudio => new LmStudioHeaderExtractor(
                httpClients.CreateClient("LmStudio"), selection.Remote),
            InferenceBackend.Local => new BorrowedHeaderClassifier(
                await localModels.GetAsync(selection.LocalModel, ct).ConfigureAwait(false)),
            _ => await new HeaderClassifierFactory(selection).CreateAsync(options, ct).ConfigureAwait(false),
        };

    public Task<IHeaderClassifier> CreatePdfProductionAsync(PipelineOptions options, CancellationToken ct = default) =>
        _pdfFactory.CreatePdfProductionAsync(options, ct);

    /// <summary>The cache, not an individual document pipeline, owns the local model lifetime.</summary>
    private sealed class BorrowedHeaderClassifier(IHeaderClassifier inner) : IHeaderClassifier
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
