using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfLiveExecutionLifecycleTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    [Fact]
    public async Task Live_pdf_path_succeeds_before_the_execution_deadline()
    {
        using var transport = new GateClassifier();

        var executionTask = RunAsync(transport, new SemanticLaneOptions(TimeSpan.FromSeconds(30)));
        await transport.Started.Task;
        transport.Complete();
        var execution = await executionTask;

        Assert.Equal("pdf-canonical-vnext", execution.Result.Provenance.Route);
        Assert.True(transport.Calls > 0);
    }

    [Fact]
    public async Task Live_pdf_path_surfaces_a_provider_failure_before_the_deadline()
    {
        using var transport = new GateClassifier
        {
            ImmediateFailure = new InvalidOperationException("live-provider-failure"),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            transport,
            new SemanticLaneOptions(TimeSpan.FromSeconds(30))));

        Assert.Equal("live-provider-failure", error.Message);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Live_pdf_timeout_quarantines_late_success_and_blocks_follow_up_provider_work()
    {
        using var transport = new GateClassifier();
        var execution = RunAsync(
            transport,
            new SemanticLaneOptions(TimeSpan.FromSeconds(2)));

        await transport.Started.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => execution);

        transport.Complete();
        await transport.FirstCallCompleted.Task;

        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Live_pdf_cancellation_quarantines_late_success_and_blocks_follow_up_provider_work()
    {
        using var cancellation = new CancellationTokenSource();
        using var transport = new GateClassifier();
        var execution = RunAsync(
            transport,
            new SemanticLaneOptions(TimeSpan.FromSeconds(30)),
            cancellation.Token);

        await transport.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => execution);

        transport.Complete();
        await transport.FirstCallCompleted.Task;

        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Live_pdf_timeout_observes_a_late_provider_failure_without_reopening_execution()
    {
        using var transport = new GateClassifier();
        var execution = RunAsync(
            transport,
            new SemanticLaneOptions(TimeSpan.FromSeconds(2)));

        await transport.Started.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => execution);

        transport.Fail(new InvalidOperationException("late-live-failure"));
        await transport.FirstCallCompleted.Task;

        Assert.Equal(1, transport.Calls);
    }

    private static Task<DocumentExtractionExecutionResult> RunAsync(
        IFrozenInferenceTransport transport,
        SemanticLaneOptions lane,
        CancellationToken cancellationToken = default)
    {
        var file = UploadedFile.FromLocalPath(Path.Combine(TestRepository.Root(), Pdf));
        return PdfExtractionPipeline.RunExecutionAsync(
            file,
            new PipelineOptions(),
            transport,
            ct: cancellationToken,
            semanticLaneOptions: lane);
    }

    private sealed class GateClassifier : IPdfProductionAuthorizedInferenceTransport
    {
        private readonly TaskCompletionSource<FrozenInferenceResponse> _firstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _firstUserMessage;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCallCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? ImmediateFailure { get; init; }
        public int Calls { get; private set; }
        public string ModelName => "p5c-fake";
        public string PdfProductionProvider => "test";
        public string PdfProductionModel => "test";
        public int ContextSize => 1 << 20;
        public string RuntimeDescription => "P5c live-path fake; no provider";
        public int SharedPrefixTokens => 0;
        public IFrozenInferenceRequestComposer RequestComposer => new OpenRouterQwen37InferenceRequestComposer();

        public Task<string> BoundaryCutAsync(
            string systemPrompt,
            string userMessage,
            CancellationToken ct = default,
            int expectedItemCount = 0)
        {
            return Task.FromResult("{\"headings\":[]}");
        }

        public Task<FrozenInferenceResponse> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ImmediateFailure is not null)
                return Task.FromException<FrozenInferenceResponse>(ImmediateFailure);
            if (Calls > 1)
                return Task.FromResult(F1AllOther(userMessage));
            _firstUserMessage = userMessage;
            Started.TrySetResult();
            return AwaitFirstCallAsync();
        }

        public void Complete() => _firstCall.TrySetResult(F1AllOther(_firstUserMessage!));
        public void Fail(Exception exception) => _firstCall.TrySetException(exception);
        public void Dispose() { }

        private static FrozenInferenceResponse F1AllOther(string userMessage)
        {
            using var request = System.Text.Json.JsonDocument.Parse(userMessage);
            var decisions = request.RootElement.GetProperty("occurrences").EnumerateArray()
                .Select(item => new { occurrence = item.GetProperty("id").GetString(), function = "OTHER" }).ToArray();
            return new FrozenInferenceResponse(
                System.Text.Json.JsonSerializer.Serialize(new { decisions }), "stop");
        }

        private async Task<FrozenInferenceResponse> AwaitFirstCallAsync()
        {
            try
            {
                return await _firstCall.Task.ConfigureAwait(false);
            }
            finally
            {
                FirstCallCompleted.TrySetResult();
            }
        }
    }
}
