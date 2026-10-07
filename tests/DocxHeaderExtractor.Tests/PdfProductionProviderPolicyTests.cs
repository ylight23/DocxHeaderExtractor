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
        var factory = new InferenceTransportFactory(new InferenceProviderSelection
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
        var factory = new InferenceTransportFactory(new InferenceProviderSelection
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
        var factory = new InferenceTransportFactory(new InferenceProviderSelection
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
        var factory = new InferenceTransportFactory(new InferenceProviderSelection
        {
            Backend = InferenceBackend.OpenRouter,
            Remote = QualifiedRemote(),
        });

        using var transport = await factory.CreatePdfProductionAsync(new PipelineOptions());

        var authorized = Assert.IsAssignableFrom<IPdfProductionAuthorizedInferenceTransport>(transport);
        Assert.Equal("OpenRouter/Alibaba", authorized.PdfProductionProvider);
        Assert.Equal(RemoteInferenceOptions.DefaultModel, authorized.PdfProductionModel);
        Assert.IsType<OpenRouterQwen37InferenceRequestComposer>(authorized.RequestComposer);
    }

    [Fact]
    public void Generic_frozen_transport_does_not_own_a_qualified_provider_composer()
    {
        Assert.Null(typeof(IFrozenInferenceTransport).GetProperty("RequestComposer"));
        Assert.Null(typeof(OpenRouterInferenceTransport).GetProperty("RequestComposer"));
        Assert.Null(typeof(DocxHeaderExtractor.V5Qualification.OpenRouterQualificationTransport)
            .GetProperty("RequestComposer"));
        Assert.NotNull(typeof(IPdfProductionAuthorizedInferenceTransport).GetProperty("RequestComposer"));
    }

    [Fact]
    public async Task Generic_OpenRouter_transport_can_target_another_model_without_PDF_authorization()
    {
        var remote = QualifiedRemote();
        remote.Model = "another/model";
        var factory = new InferenceTransportFactory(new InferenceProviderSelection
        {
            Backend = InferenceBackend.OpenRouter,
            Remote = remote,
        });
        using var transport = await factory.CreateAsync(new PipelineOptions());
        Assert.IsAssignableFrom<IFrozenInferenceTransport>(transport);
        Assert.False(transport is IPdfProductionAuthorizedInferenceTransport);
        Assert.Equal("another/model", transport.ModelName);
    }

    [Fact]
    public async Task A_factory_that_has_not_explicitly_authorized_PDF_fails_closed()
    {
        IInferenceTransportFactory factory = new LegacyFactory();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreatePdfProductionAsync(new PipelineOptions()));

        Assert.Equal("PDF_PRODUCTION_PROVIDER_FACTORY_REQUIRED", error.Message);
    }

    [Fact]
    public async Task Pdf_heading_pipeline_rejects_generic_frozen_transport_before_any_call()
    {
        var built = DocxHeaderExtractor.DocumentProcessing.Source.Pdf.PdfSourceAdapter.BuildWithDetails(
            TestRepository.Path("todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"));
        using var transport = new GenericFrozenTransport();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PdfHeadingPipeline.RunAsync(
            built.Snapshot, built.Details, "generic.pdf", transport, null, CancellationToken.None));
        Assert.Equal("PDF_PRODUCTION_AUTHORIZATION_REQUIRED", error.Message);
        Assert.Equal(0, transport.Calls);
    }

    private sealed class GenericFrozenTransport : IFrozenInferenceTransport
    {
        public int Calls { get; private set; }
        public string ModelName => "arbitrary-model";
        public int ContextSize => 1 << 20;
        public string RuntimeDescription => "no network";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage,
            CancellationToken ct = default, int expectedItemCount = 0)
        {
            Calls++;
            throw new InvalidOperationException("must not start");
        }
        public Task<FrozenInferenceResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens,
            string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("must not start");
        }
        public void Dispose() { }
    }

    private static RemoteInferenceOptions QualifiedRemote() => new()
    {
        ApiKey = "test-key",
        Model = RemoteInferenceOptions.DefaultModel,
        OpenRouterProviderRoute = RemoteInferenceOptions.DefaultProviderRoute,
    };

    private sealed class LegacyFactory : IInferenceTransportFactory
    {
        public Task<IInferenceTransport> CreateAsync(PipelineOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
