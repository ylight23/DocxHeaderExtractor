using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The live PDF lane's lifecycle under a deadline and cancellation. Since the V6 promotion (2026-10-14) the model work of the lane is the heading
/// selector, so the gate sits there: a late selection after a timeout or a cancellation is quarantined, and the follow-up provider work (the
/// placement call through the authorized transport) never starts.
/// </summary>
public sealed class PdfLiveExecutionLifecycleTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    [Fact]
    public async Task Live_pdf_path_succeeds_before_the_execution_deadline()
    {
        using var transport = new CountingTransport();
        var selector = new GateSelector(FirstAlias());

        var executionTask = RunAsync(transport, selector, new SemanticLaneOptions(TimeSpan.FromSeconds(30)));
        await selector.Started.Task;
        selector.Complete();
        var execution = await executionTask;

        Assert.Equal("pdf-canonical-vnext", execution.Result.Provenance.Route);
        Assert.Equal(1, selector.Calls);
        Assert.Equal(1, transport.PlacementCalls);
    }

    [Fact]
    public async Task Live_pdf_path_surfaces_a_selector_failure_before_the_deadline()
    {
        using var transport = new CountingTransport();
        var selector = new GateSelector(FirstAlias()) { ImmediateFailure = new InvalidOperationException("live-provider-failure") };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(transport, selector, new SemanticLaneOptions(TimeSpan.FromSeconds(30))));

        Assert.Equal("live-provider-failure", error.Message);
        Assert.Equal(1, selector.Calls);
        Assert.Equal(0, transport.PlacementCalls);
    }

    [Fact]
    public async Task Live_pdf_timeout_quarantines_late_success_and_blocks_follow_up_provider_work()
    {
        using var transport = new CountingTransport();
        var selector = new GateSelector(FirstAlias());
        var execution = RunAsync(transport, selector, new SemanticLaneOptions(TimeSpan.FromSeconds(2)));

        await selector.Started.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => execution);

        selector.Complete();
        await selector.Returned.Task;
        await Task.Delay(500);

        Assert.Equal(1, selector.Calls);
        Assert.Equal(0, transport.PlacementCalls);
    }

    [Fact]
    public async Task Live_pdf_cancellation_quarantines_late_success_and_blocks_follow_up_provider_work()
    {
        using var cancellation = new CancellationTokenSource();
        using var transport = new CountingTransport();
        var selector = new GateSelector(FirstAlias());
        var execution = RunAsync(transport, selector, new SemanticLaneOptions(TimeSpan.FromSeconds(30)), cancellation.Token);

        await selector.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => execution);

        selector.Complete();
        await selector.Returned.Task;
        await Task.Delay(500);

        Assert.Equal(1, selector.Calls);
        Assert.Equal(0, transport.PlacementCalls);
    }

    [Fact]
    public async Task Live_pdf_timeout_observes_a_late_selector_failure_without_reopening_execution()
    {
        using var transport = new CountingTransport();
        var selector = new GateSelector(FirstAlias());
        var execution = RunAsync(transport, selector, new SemanticLaneOptions(TimeSpan.FromSeconds(2)));

        await selector.Started.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => execution);

        selector.Fail(new InvalidOperationException("late-live-failure"));
        await selector.Returned.Task;
        await Task.Delay(500);

        Assert.Equal(1, selector.Calls);
        Assert.Equal(0, transport.PlacementCalls);
    }

    private static string FirstAlias() =>
        PdfSourceAdapter.BuildWithDetails(Path.Combine(TestRepository.Root(), Pdf)).Snapshot.Atoms.OrderBy(a => a.Ordinal).First().Alias;

    private static Task<DocumentExtractionExecutionResult> RunAsync(
        IFrozenInferenceTransport transport,
        IPdfHeadingSelector selector,
        SemanticLaneOptions lane,
        CancellationToken cancellationToken = default)
    {
        var file = UploadedFile.FromLocalPath(Path.Combine(TestRepository.Root(), Pdf));
        return PdfExtractionPipeline.RunExecutionAsync(
            file,
            new PipelineOptions(),
            transport,
            ct: cancellationToken,
            semanticLaneOptions: lane,
            selector: selector);
    }

    /// <summary>A selector whose answer waits for the test: it signals Started when called and Returned once its answer or failure is out.</summary>
    private sealed class GateSelector(string alias) : IPdfHeadingSelector
    {
        private readonly TaskCompletionSource<PdfHeadingSelection> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? ImmediateFailure { get; init; }
        public int Calls { get; private set; }
        public string SelectorId => "gate-test-selector";

        public async Task<PdfHeadingSelection> SelectAsync(string pdfPath, string apiKey, CancellationToken ct)
        {
            Calls++;
            Started.TrySetResult();
            try
            {
                if (ImmediateFailure is not null) throw ImmediateFailure;
                return await _answer.Task;
            }
            finally { Returned.TrySetResult(); }
        }

        public void Complete() => _answer.TrySetResult(new PdfHeadingSelection(new HashSet<string> { alias }, 1, 1, [], ["{}"], 1, 0.0));
        public void Fail(Exception error) => _answer.TrySetException(error);
    }

    /// <summary>The authorized transport: never sends a frozen request on this lane; counts the placement calls and answers them with no placement.</summary>
    private sealed class CountingTransport : IPdfProductionAuthorizedInferenceTransport
    {
        private int _placementCalls;
        public int PlacementCalls => Volatile.Read(ref _placementCalls);
        public string? OpenRouterApiKey => "test-key";
        public IFrozenInferenceRequestComposer RequestComposer => new OpenRouterQwen37InferenceRequestComposer();
        public string PdfProductionProvider => "test";
        public string PdfProductionModel => "test";
        public string ModelName => "lifecycle-fake";
        public int ContextSize => 1 << 20;
        public string RuntimeDescription => "lifecycle fake; no provider";
        public int SharedPrefixTokens => 0;

        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            Interlocked.Increment(ref _placementCalls);
            return Task.FromResult("{\"placements\":[]}");
        }

        public Task<FrozenInferenceResponse> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the V6 lane sends no frozen request");

        public void Dispose() { }
    }
}
