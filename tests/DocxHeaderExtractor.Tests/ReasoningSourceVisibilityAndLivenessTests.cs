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
    public void Prompt_makes_per_request_identity_explicit()
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
            requestId: "request-123",
            semanticPassId: "semantic-456",
            attemptId: "request-123:attempt-1",
            sourceIdentityMap: [new ReasoningSourceIdentity
            {
                CanonicalSourceId = "body[1]/p[1]",
                SourceOccurrenceId = "DOC-1:body[1]/p[1]:1:9",
                ProviderSourceAlias = "s0001",
                SourceOrdinal = 1,
                RawTextLength = 9,
            }]);

        Assert.Contains("REQUEST_ID_EXACT=request-123", prompt);
        Assert.Contains("SEMANTIC_PASS_ID_EXACT=semantic-456", prompt);
        Assert.Contains("ATTEMPT_ID_EXACT=request-123:attempt-1", prompt);
        Assert.Contains("canonicalSourceId=body[1]/p[1]", prompt);
        Assert.Contains("providerSourceAlias=s0001", prompt);
        Assert.Contains("Never use a file path", prompt);
    }

    [Fact]
    public async Task Canonical_source_id_visible_is_accepted()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(request => ProposalResponse("p[0]", 0, 9));

        var observation = await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal("p[0]", Assert.Single(observation.Proposed).SourceId);
        Assert.Equal(1, observation.CompletionStats.Completion.SuccessfulCompletionCount);
    }

    [Fact]
    public async Task Explicit_source_occurrence_alias_reverses_to_canonical_source_id()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(request =>
        {
            var identity = Assert.Single(request.SourceIdentityMap);
            return ProposalResponse(identity.SourceOccurrenceId, 0, identity.RawTextLength);
        });

        var observation = await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal("p[0]", Assert.Single(observation.Proposed).SourceId);
    }

    [Fact]
    public async Task Explicit_provider_source_alias_reverses_to_canonical_source_id()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(request =>
        {
            var identity = Assert.Single(request.SourceIdentityMap);
            return ProposalResponse(identity.ProviderSourceAlias!, 0, identity.RawTextLength);
        });

        var observation = await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal("p[0]", Assert.Single(observation.Proposed).SourceId);
    }

    [Fact]
    public async Task Unknown_source_id_fails_closed_with_exact_visibility_diagnostic()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(_ => ProposalResponse("invented-source", 0, 1));

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() =>
            new ReasoningPreservingHeadingHarness(model)
                .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));

        Assert.Contains("reasoning-response-source-not-visible", exception.Message);
        var identity = Assert.IsType<ReasoningResponseIdentityException>(exception.InnerException);
        Assert.Equal(["invented-source"], identity.ReturnedSourceIds);
        Assert.Contains("p[0]", identity.VisibleSourceIds);
        Assert.Contains("p[0]", identity.OwnedSourceIds);
    }

    [Fact]
    public async Task Visible_but_not_owned_is_classified_separately_from_not_visible()
    {
        var first = true;
        var state = NativePolicyStateFactory.Create([
            (0, "A", null, (int?)null),
            (1, "B", null, (int?)null),
        ]);
        var model = new ProgrammableModel(request =>
        {
            if (first)
            {
                first = false;
                throw Completion(ReasoningCompletionFailureClass.ProviderOutputLimit);
            }
            return ProposalResponse("p[0]", 0, 1);
        });

        var observation = await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal(1, observation.CompletionStats.Completion.OutOfScopeProposalCount);
        Assert.Equal(1, observation.CompletionStats.Completion.OwnershipViolationCount);
        Assert.DoesNotContain(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid,
            observation.CompletionStats.FailureClasses);
    }

    [Fact]
    public async Task Returned_span_must_be_valid_for_the_visible_raw_text()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(_ => ProposalResponse("p[0]", 0, 99));

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() =>
            new ReasoningPreservingHeadingHarness(model)
                .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));

        Assert.Contains("reasoning-response-span-invalid-for-visible-source", exception.Message);
    }

    [Fact]
    public async Task Response_request_identity_mismatch_is_rejected_as_stale()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(request => new ReasoningModelResponse(
            null,
            [],
            [],
            RequestId: "stale-request",
            SemanticPassId: request.SemanticPassId,
            AttemptId: request.AttemptId));

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() =>
            new ReasoningPreservingHeadingHarness(model)
                .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));

        Assert.Contains("reasoning-response-request-id-mismatch", exception.Message);
    }

    [Fact]
    public async Task Duplicate_occurrence_in_one_response_is_rejected()
    {
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);
        var model = new ProgrammableModel(_ => new ReasoningModelResponse(
            null,
            [Proposal("p[0]", 0, 9), Proposal("p[0]", 0, 9)],
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
        var model = new ProgrammableModel(request => request.SemanticPassId.StartsWith("global-", StringComparison.Ordinal)
            ? ProposalResponse("p[1]", 0, 6)
            : new ReasoningModelResponse(null, [], []));

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
        OwnedStartOrdinal = 0,
        OwnedEndOrdinal = 0,
        AttemptId = "request:attempt-1",
        ConfigurationSignature = "config",
    };

    private static ReasoningHeadingProposal Proposal(string sourceId, int start, int end) => new()
    {
        SourceId = sourceId,
        HeadingSpan = new StructuralSpan(start, end),
        Text = "",
        SemanticRole = "CONTENT_HEADING",
        ProposedLevel = 1,
    };

    private static ReasoningModelResponse ProposalResponse(string sourceId, int start, int end) =>
        new(null, [Proposal(sourceId, start, end)], []);

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
