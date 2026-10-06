using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfProductionProviderPolicyTests
{
    [Theory]
    [InlineData(InferenceBackend.Local, "local Llama")]
    [InlineData(InferenceBackend.LmStudio, "LM Studio")]
    [InlineData(InferenceBackend.Sglang, "SGLang")]
    public async Task Pdf_production_fails_before_transport_for_unsupported_provider(
        InferenceBackend backend, string name)
    {
        var factory = new HeaderClassifierFactory(new InferenceProviderSelection
        {
            Backend = backend,
            Remote = QualifiedRemote(),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreatePdfProductionAsync(new PipelineOptions()));

        Assert.Contains("PDF_PRODUCTION_PROVIDER_UNSUPPORTED", error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_production_rejects_a_nonqualified_OpenRouter_model_before_transport()
    {
        var remote = QualifiedRemote();
        remote.Model = "another/model";
        var factory = new HeaderClassifierFactory(new InferenceProviderSelection
        {
            Backend = InferenceBackend.OpenRouter,
            Remote = remote,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreatePdfProductionAsync(new PipelineOptions()));

        Assert.Contains("PDF_PRODUCTION_MODEL_UNSUPPORTED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_production_rejects_an_automatic_or_other_OpenRouter_route_before_transport()
    {
        var remote = QualifiedRemote();
        remote.OpenRouterProviderRoute = null;
        var factory = new HeaderClassifierFactory(new InferenceProviderSelection
        {
            Backend = InferenceBackend.OpenRouter,
            Remote = remote,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreatePdfProductionAsync(new PipelineOptions()));

        Assert.Contains("PDF_PRODUCTION_PROVIDER_ROUTE_UNSUPPORTED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qualified_OpenRouter_factory_returns_the_separate_PDF_production_authorization()
    {
        var factory = new HeaderClassifierFactory(new InferenceProviderSelection
        {
            Backend = InferenceBackend.OpenRouter,
            Remote = QualifiedRemote(),
        });

        using var classifier = await factory.CreatePdfProductionAsync(new PipelineOptions());

        var authorized = Assert.IsAssignableFrom<IPdfProductionAuthorizedFrozenRequestClassifier>(classifier);
        Assert.Equal("OpenRouter/Alibaba", authorized.PdfProductionProvider);
        Assert.Equal(RemoteInferenceOptions.DefaultModel, authorized.PdfProductionModel);
    }

    [Fact]
    public async Task A_factory_that_has_not_explicitly_authorized_PDF_fails_closed()
    {
        IHeaderClassifierFactory factory = new LegacyFactory();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreatePdfProductionAsync(new PipelineOptions()));

        Assert.Equal("PDF_PRODUCTION_PROVIDER_FACTORY_REQUIRED", error.Message);
    }

    private static RemoteInferenceOptions QualifiedRemote() => new()
    {
        ApiKey = "test-key",
        Model = RemoteInferenceOptions.DefaultModel,
        OpenRouterProviderRoute = RemoteInferenceOptions.DefaultProviderRoute,
    };

    private sealed class LegacyFactory : IHeaderClassifierFactory
    {
        public Task<IHeaderClassifier> CreateAsync(PipelineOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
