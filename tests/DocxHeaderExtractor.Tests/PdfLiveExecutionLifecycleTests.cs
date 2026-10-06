using DocxHeaderExtractor.DocumentProcessing.Inference;
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
        using var classifier = new GateClassifier();

        var executionTask = RunAsync(classifier, new SemanticLaneOptions(TimeSpan.FromSeconds(30)));
        await classifier.Started.Task;
        classifier.Complete("{\"headings\":[]}");
        var execution = await executionTask;

        Assert.Equal("pdf-canonical-vnext", execution.Result.Provenance.Route);
        Assert.True(classifier.Calls > 0);
    }

    [Fact]
    public async Task Live_pdf_path_surfaces_a_provider_failure_before_the_deadline()
    {
        using var classifier = new GateClassifier
        {
            ImmediateFailure = new InvalidOperationException("live-provider-failure"),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            classifier,
            new SemanticLaneOptions(TimeSpan.FromSeconds(30))));

        Assert.Equal("live-provider-failure", error.Message);
        Assert.Equal(1, classifier.Calls);
    }

    [Fact]
    public async Task Live_pdf_timeout_quarantines_late_success_and_blocks_follow_up_provider_work()
    {
        using var classifier = new GateClassifier();
        var execution = RunAsync(
            classifier,
            new SemanticLaneOptions(TimeSpan.FromSeconds(2)));

        await classifier.Started.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => execution);

        classifier.Complete("{\"headings\":[]}");
        await classifier.FirstCallCompleted.Task;

        Assert.Equal(1, classifier.Calls);
    }

    [Fact]
    public async Task Live_pdf_cancellation_quarantines_late_success_and_blocks_follow_up_provider_work()
    {
        using var cancellation = new CancellationTokenSource();
        using var classifier = new GateClassifier();
        var execution = RunAsync(
            classifier,
            new SemanticLaneOptions(TimeSpan.FromSeconds(30)),
            cancellation.Token);

        await classifier.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => execution);

        classifier.Complete("{\"headings\":[]}");
        await classifier.FirstCallCompleted.Task;

        Assert.Equal(1, classifier.Calls);
    }

    [Fact]
    public async Task Live_pdf_timeout_observes_a_late_provider_failure_without_reopening_execution()
    {
        using var classifier = new GateClassifier();
        var execution = RunAsync(
            classifier,
            new SemanticLaneOptions(TimeSpan.FromSeconds(2)));

        await classifier.Started.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => execution);

        classifier.Fail(new InvalidOperationException("late-live-failure"));
        await classifier.FirstCallCompleted.Task;

        Assert.Equal(1, classifier.Calls);
    }

    private static Task<AuthorityPipelineExecutionResult> RunAsync(
        IFrozenInferenceTransport classifier,
        SemanticLaneOptions lane,
        CancellationToken cancellationToken = default)
    {
        var file = UploadedFile.FromLocalPath(Path.Combine(TestRepository.Root(), Pdf));
        return PdfCanonicalExtraction.RunExecutionAsync(
            file,
            new PipelineOptions(),
            classifier,
            ct: cancellationToken,
            semanticLaneOptions: lane);
    }

    private sealed class GateClassifier : IPdfProductionAuthorizedInferenceTransport
    {
        private readonly TaskCompletionSource<FrozenHeaderExecutionResult> _firstCall =
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

        public Task<string> BoundaryCutAsync(
            string systemPrompt,
            string userMessage,
            CancellationToken ct = default,
            int expectedItemCount = 0)
        {
            return Task.FromResult("{\"headings\":[]}");
        }

        public Task<FrozenHeaderExecutionResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ImmediateFailure is not null)
                return Task.FromException<FrozenHeaderExecutionResult>(ImmediateFailure);
            if (Calls > 1)
                return Task.FromResult(F1AllOther(userMessage));
            _firstUserMessage = userMessage;
            Started.TrySetResult();
            return AwaitFirstCallAsync();
        }

        public void Complete(string response) => _firstCall.TrySetResult(F1AllOther(_firstUserMessage!));
        public void Fail(Exception exception) => _firstCall.TrySetException(exception);
        public void Dispose() { }

        private static FrozenHeaderExecutionResult F1AllOther(string userMessage)
        {
            using var request = System.Text.Json.JsonDocument.Parse(userMessage);
            var decisions = request.RootElement.GetProperty("occurrences").EnumerateArray()
                .Select(item => new { occurrence = item.GetProperty("id").GetString(), function = "OTHER" }).ToArray();
            return new FrozenHeaderExecutionResult(
                System.Text.Json.JsonSerializer.Serialize(new { decisions }), "stop", null, string.Empty, 0, 0);
        }

        private async Task<FrozenHeaderExecutionResult> AwaitFirstCallAsync()
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
