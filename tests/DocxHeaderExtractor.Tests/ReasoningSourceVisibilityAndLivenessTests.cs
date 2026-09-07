using System.Net;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class ReasoningSourceVisibilityAndLivenessTests
{
    [Fact]
    public void Prompt_describes_harness_owned_character_scope_without_response_identity()
    {
        var segment = new ReasoningContextSegment
        {
            ContextSegmentId = "segment-1",
            Ordinal = 1,
            SourceOccurrenceIds = [],
            OwnedSourceOccurrenceIds = [],
            Text = "SOURCE_OCCURRENCE blocks"
        };

        var prompt = ReasoningPrompt.BuildUser(
            segment,
            shadow: false,
            ownedOutputScope: new ReasoningOwnedOutputScope
            {
                CanonicalSourceId = "body[1]/p[1]",
                SourceOccurrenceId = "DOC-1:body[1]/p[1]:1:9",
                RawTextLength = 9,
                OwnedStart = 0,
                OwnedEnd = 9,
            });

        Assert.Contains("source=body[1]/p[1]", prompt);
        Assert.Contains("ownedCharacters=0..9", prompt);
        Assert.Contains("local start/end offsets", prompt);
        Assert.Contains("exactly that one owned source occurrence", prompt);
        Assert.Contains("each exact local span at most once", prompt);
        Assert.DoesNotContain("REQUEST_ID_EXACT", prompt);
        Assert.DoesNotContain("sourceId field", prompt);
    }

    [Fact]
    public async Task Canonical_source_id_visible_is_accepted()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(_ => ProposalResponse(0, 9));

        var observation = await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal("p[0]", Assert.Single(observation.Proposed).SourceId);
        Assert.Equal(1, observation.CompletionStats.Completion.SuccessfulCompletionCount);
    }

    [Fact]
    public async Task Harness_binds_local_response_to_active_canonical_source()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(_ => ProposalResponse(0, 9));

        var observation = await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal("p[0]", Assert.Single(observation.Proposed).SourceId);
    }

    [Fact]
    public void Model_response_parser_rejects_source_identity_fields()
    {
        Assert.Throws<FormatException>(() => ReasoningModelResponseParser.Parse(
            "{\"headings\":[{\"sourceId\":\"invented\",\"start\":0,\"end\":1,\"semanticRole\":\"CONTENT_HEADING\"}]}"));
    }

    [Fact]
    public void Model_response_without_source_identity_is_parseable()
    {
        var response = ReasoningModelResponseParser.Parse(
            "{\"headings\":[{\"start\":0,\"end\":1,\"semanticRole\":\"CONTENT_HEADING\"}]}");
        Assert.Equal(0, Assert.Single(response.Headings).Start);
    }

    [Fact]
    public async Task Invalid_local_span_fails_closed()
    {
        var state = NativePolicyStateFactory.Create([
            (0, "A", null, (int?)null),
            (1, "B", null, (int?)null),
        ]);
        var model = new ProgrammableModel(_ => ProposalResponse(0, 99));
        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() =>
            new ReasoningPreservingHeadingHarness(model)
                .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));
        Assert.Contains("reasoning-response-local-span-invalid", exception.Message);
    }

    [Fact]
    public async Task Local_span_must_fit_the_owned_character_range()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(_ => ProposalResponse(0, 99));

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() =>
            new ReasoningPreservingHeadingHarness(model)
                .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));

        Assert.Contains("reasoning-response-local-span-invalid", exception.Message);
    }

    [Fact]
    public void Response_control_identity_is_not_a_model_contract()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var response = ReasoningModelResponseParser.Parse("{\"headings\":[]}");
        Assert.Empty(response.Headings);
    }

    [Fact]
    public async Task Duplicate_occurrence_in_one_response_is_rejected()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(_ => new ReasoningModelResponse(
            [ModelProposal(0, 9), ModelProposal(0, 9)],
            []));

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() =>
            new ReasoningPreservingHeadingHarness(model)
                .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));

        Assert.Contains("reasoning-response-duplicate-occurrence", exception.Message);
    }

    [Fact]
    public async Task Consolidation_retains_canonical_source_mapping()
    {
        var state = NativePolicyStateFactory.Create([
            (0, "First", null, (int?)null),
            (1, "Second", null, (int?)null),
        ]);
        var model = new ProgrammableModel(request => request.SemanticPassId.StartsWith("global-", StringComparison.Ordinal) &&
            request.OwnedOutputScope.CanonicalSourceId == "p[1]"
            ? ProposalResponse(0, 6)
            : new ReasoningModelResponse([], []));

        var observation = await new ReasoningPreservingHeadingHarness(
                model,
                maxContextCharacters: 1,
                windowCharacters: 100)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Contains(observation.Proposed, item => item.SourceId == "p[1]");
    }

    [Fact]
    public async Task Provider_connect_timeout_is_classified_separately()
    {
        using var model = ProviderModel(
            new ConnectTimeoutHandler(),
            new ReasoningProviderTimeoutOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(20),
                FirstByteTimeout = TimeSpan.FromSeconds(1),
                InactivityTimeout = TimeSpan.FromSeconds(1),
                TotalRequestTimeout = TimeSpan.FromSeconds(2),
            });

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() => model.CompleteAsync(Request()));

        Assert.Equal(ReasoningCompletionFailureClass.ProviderConnectTimeout, exception.FailureClass);
        Assert.Equal("CONNECT", model.CompletionTelemetry.Single().TimeoutStage);
    }

    [Fact]
    public async Task Provider_first_byte_timeout_is_classified_separately()
    {
        using var model = ProviderModel(
            new DelayedHeadersHandler(),
            new ReasoningProviderTimeoutOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(1),
                FirstByteTimeout = TimeSpan.FromMilliseconds(20),
                InactivityTimeout = TimeSpan.FromSeconds(1),
                TotalRequestTimeout = TimeSpan.FromSeconds(2),
            });

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() => model.CompleteAsync(Request()));

        Assert.Equal(ReasoningCompletionFailureClass.ProviderFirstByteTimeout, exception.FailureClass);
        Assert.Equal("FIRST_BYTE", model.CompletionTelemetry.Single().TimeoutStage);
    }

    [Fact]
    public async Task Provider_stream_inactivity_timeout_is_classified_separately()
    {
        using var model = ProviderModel(
            new BodyHandler(new BlockingReadStream()),
            new ReasoningProviderTimeoutOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(1),
                FirstByteTimeout = TimeSpan.FromSeconds(1),
                InactivityTimeout = TimeSpan.FromMilliseconds(20),
                TotalRequestTimeout = TimeSpan.FromSeconds(2),
            });

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() => model.CompleteAsync(Request()));

        Assert.Equal(ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout, exception.FailureClass);
        Assert.Equal("STREAM_INACTIVITY", model.CompletionTelemetry.Single().TimeoutStage);
    }

    [Fact]
    public async Task Provider_total_timeout_is_classified_separately()
    {
        using var model = ProviderModel(
            new BodyHandler(new BlockingReadStream()),
            new ReasoningProviderTimeoutOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(1),
                FirstByteTimeout = TimeSpan.FromSeconds(1),
                InactivityTimeout = TimeSpan.FromSeconds(1),
                TotalRequestTimeout = TimeSpan.FromMilliseconds(20),
            });

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() => model.CompleteAsync(Request()));

        Assert.Equal(ReasoningCompletionFailureClass.ProviderTotalTimeout, exception.FailureClass);
        Assert.Equal("TOTAL", model.CompletionTelemetry.Single().TimeoutStage);
    }

    [Fact]
    public async Task Stream_progress_resets_inactivity_watchdog()
    {
        var inner = JsonSerializer.Serialize(new
        {
            complete = true,
            headings = Array.Empty<object>(),
            decisionEvidence = Array.Empty<object>(),
        });
        using var model = ProviderModel(
            new BodyHandler(new ProgressiveReadStream(Envelope(inner, "stop"), 8, TimeSpan.FromMilliseconds(5))),
            new ReasoningProviderTimeoutOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(1),
                FirstByteTimeout = TimeSpan.FromSeconds(1),
                InactivityTimeout = TimeSpan.FromMilliseconds(25),
                TotalRequestTimeout = TimeSpan.FromSeconds(2),
            });

        var response = await model.CompleteAsync(Request());

        Assert.Empty(response.Headings);
        Assert.Null(model.CompletionTelemetry.Single().TimeoutStage);
        Assert.True(model.CompletionTelemetry.Single().StreamCompletedNormally);
    }

    [Fact]
    public async Task Timeout_retry_preserves_request_identity_and_stops_at_limit()
    {
        var attempts = 0;
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(_ =>
        {
            attempts++;
            throw Completion(ReasoningCompletionFailureClass.ProviderFirstByteTimeout);
        });

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() =>
            new ReasoningPreservingHeadingHarness(model, maxTransientRetries: 2)
                .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));

        Assert.Equal(3, attempts);
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(model.Requests[0].RequestId, model.Requests[1].RequestId);
        Assert.Equal(model.Requests[1].RequestId, model.Requests[2].RequestId);
        Assert.NotEqual(model.Requests[0].AttemptId, model.Requests[1].AttemptId);
        Assert.Equal(ReasoningCompletionFailureClass.ProviderFirstByteTimeout, exception.FailureClass);
    }

    [Fact]
    public async Task Retries_share_one_semantic_pass_deadline()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new BudgetedTransientModel(TimeSpan.FromMilliseconds(35));
        var budget = new ReasoningExecutionBudgetOptions
        {
            AttemptTotalTimeout = TimeSpan.FromMilliseconds(100),
            SemanticPassTimeout = TimeSpan.FromMilliseconds(60),
            MaxTransientRetries = 2,
        };
        var started = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() =>
            new ReasoningPreservingHeadingHarness(model, budgetOptions: budget)
                .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));

        Assert.Equal(ReasoningCompletionFailureClass.ProviderSemanticPassTimeout, exception.FailureClass);
        Assert.Equal(2, model.Calls);
        Assert.Equal(2, model.Requests.Count);
        Assert.InRange(model.AttemptTimeouts[0].TotalMilliseconds, 45, 60);
        Assert.InRange(model.AttemptTimeouts[1].TotalMilliseconds, 1, 30);
        Assert.True(model.AttemptTimeouts[1] < model.AttemptTimeouts[0]);
        Assert.InRange(started.ElapsedMilliseconds, 35, 220);
    }

    private static ReasoningModelRequest Request() => new()
    {
        RequestId = "request",
        DocumentId = "doc",
        Route = "ModelCapabilityCeiling",
        SemanticPassId = "semantic-pass",
        ContextSegmentId = "segment",
        SystemPrompt = "system",
        UserPrompt = "user",
        SourceOccurrenceIds = ["occurrence"],
        OwnedSourceOccurrenceIds = ["occurrence"],
        OwnedOutputScope = new ReasoningOwnedOutputScope
        {
            CanonicalSourceId = "p[0]",
            SourceOccurrenceId = "occurrence",
            RawTextLength = 9,
            OwnedStart = 0,
            OwnedEnd = 9,
        },
        AttemptId = "request:attempt-1",
        ConfigurationSignature = "config",
    };

    private static ReasoningModelHeadingProposal ModelProposal(int start, int end) => new()
    {
        Start = start,
        End = end,
        SemanticRole = "CONTENT_HEADING",
    };

    private static ReasoningModelResponse ProposalResponse(int start, int end) =>
        new([ModelProposal(start, end)], []);

    private static ReasoningCompletionException Completion(string failureClass) => new(
        failureClass,
        failureClass,
        new ReasoningCompletionTelemetry { FailureClass = failureClass },
        null);

    private static OpenRouterReasoningSemanticModel ProviderModel(
        HttpMessageHandler handler,
        ReasoningProviderTimeoutOptions timeoutOptions) =>
        new(
            new RemoteInferenceOptions { ApiKey = "test-key" },
            new HttpClient(handler),
            timeoutOptions);

    private static string Envelope(string content, string finishReason) => JsonSerializer.Serialize(new
    {
        id = "provider-request",
        choices = new[] { new { message = new { content }, finish_reason = finishReason } },
        usage = new { prompt_tokens = 10, completion_tokens = 12, total_tokens = 22 },
    });

    private sealed class ProgrammableModel(Func<ReasoningModelRequest, ReasoningModelResponse> handler) : IReasoningSemanticModel
    {
        public string ModelName => "test";
        public string ProviderName => "test";
        public int ContextSize => 80_000;
        public int ProviderCalls { get; private set; }
        public List<ReasoningModelRequest> Requests { get; } = [];

        public Task<ReasoningModelResponse> CompleteAsync(ReasoningModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            ProviderCalls++;
            return Task.FromResult(handler(request));
        }
    }

    private sealed class BudgetedTransientModel(TimeSpan delay) : IReasoningSemanticModel, IReasoningAttemptTimeoutModel
    {
        public string ModelName => "test";
        public string ProviderName => "test";
        public int ContextSize => 80_000;
        public int ProviderCalls => Calls;
        public int Calls { get; private set; }
        public List<ReasoningModelRequest> Requests { get; } = [];
        public List<TimeSpan> AttemptTimeouts { get; } = [];

        public Task<ReasoningModelResponse> CompleteAsync(ReasoningModelRequest request, CancellationToken ct = default) =>
            CompleteAsync(request, TimeSpan.FromMinutes(1), ct);

        public async Task<ReasoningModelResponse> CompleteAsync(
            ReasoningModelRequest request,
            TimeSpan attemptTimeout,
            CancellationToken ct = default)
        {
            Calls++;
            Requests.Add(request);
            AttemptTimeouts.Add(attemptTimeout);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw Completion(ReasoningCompletionFailureClass.ProviderTotalTimeout);
            }

            throw Completion(ReasoningCompletionFailureClass.ProviderFirstByteTimeout);
        }
    }

    private sealed class ConnectTimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connect timed out", new TimeoutException("connect")));
    }

    private sealed class DelayedHeadersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class BodyHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class ProgressiveReadStream(string text, int chunkSize, TimeSpan delay) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Copy(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return Copy(buffer.Span);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Copy(Span<byte> destination)
        {
            if (_offset >= _bytes.Length) return 0;
            var count = Math.Min(Math.Min(chunkSize, destination.Length), _bytes.Length - _offset);
            _bytes.AsSpan(_offset, count).CopyTo(destination);
            _offset += count;
            return count;
        }
    }
}
