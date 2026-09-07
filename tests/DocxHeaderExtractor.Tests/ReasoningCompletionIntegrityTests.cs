using System.Net;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class ReasoningCompletionIntegrityTests
{
    [Fact]
    public void Truncated_json_is_rejected_as_incomplete()
    {
        Assert.Throws<JsonException>(() => ReasoningModelResponseParser.Parse(
            "{\"complete\":true,\"headings\":[{"));
    }

    [Fact]
    public void Json_prefix_is_never_partially_scored()
    {
        var raw = "{\"headings\":[{\"start\":0}";
        Assert.ThrowsAny<Exception>(() => ReasoningModelResponseParser.Parse(raw));
    }

    [Fact]
    public void Missing_complete_marker_is_accepted_when_schema_is_valid()
    {
        var response = ReasoningModelResponseParser.Parse(
            "{\"headings\":[],\"decisionEvidence\":[]}");
        Assert.Empty(response.Headings);
    }

    [Fact]
    public void Complete_marker_is_not_transport_authority()
    {
        var response = ReasoningModelResponseParser.Parse(
            "{\"complete\":false,\"headings\":[],\"decisionEvidence\":[]}");
        Assert.Empty(response.Headings);
    }

    [Fact]
    public async Task Finish_reason_length_is_provider_output_limit_not_model_gap()
    {
        var request = Request();
        var content = "{\"headings\":[";
        using var model = ProviderModel(Envelope(content, "length"));

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() => model.CompleteAsync(request));

        Assert.Equal(ReasoningCompletionFailureClass.ProviderOutputLimit, exception.FailureClass);
        Assert.Equal(ReasoningCompletionFailureClass.ProviderOutputLimit, model.CompletionTelemetry.Single().FailureClass);
        Assert.False(model.CompletionTelemetry.Single().JsonParseSucceeded);
    }

    [Fact]
    public async Task Complete_response_with_invalid_json_has_schema_failure()
    {
        using var model = ProviderModel(Envelope("{\"complete\":true,\"headings\":[}", "stop"));

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() => model.CompleteAsync(Request()));

        Assert.Equal(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid, exception.FailureClass);
        Assert.True(model.CompletionTelemetry.Single().CompletionEnvelopeComplete);
        Assert.NotNull(model.CompletionTelemetry.Single().JsonParseErrorOffset);
    }

    [Fact]
    public async Task Valid_completion_records_metadata_without_storing_content()
    {
        var request = Request();
        var inner = JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-reasoning-bounded-v1",
            headings = Array.Empty<object>(),
            decisionEvidence = Array.Empty<object>(),
        });
        using var model = ProviderModel(Envelope(inner, "stop"));

        var response = await model.CompleteAsync(request);
        var telemetry = model.CompletionTelemetry.Single();

        Assert.Empty(response.Headings);
        Assert.True(telemetry.JsonParseSucceeded);
        Assert.True(telemetry.CompletionEnvelopeComplete);
        Assert.True(telemetry.StreamCompletedNormally);
        Assert.NotNull(telemetry.FullContentSha256);
        Assert.NotEqual(inner, telemetry.FullContentSha256);
        Assert.Equal(1, model.ProviderCalls);
    }

    [Fact]
    public async Task Transport_truncation_is_classified_and_can_be_retried()
    {
        var first = true;
        var model = new ProgrammableModel(_ =>
        {
            if (first)
            {
                first = false;
                throw Completion(ReasoningCompletionFailureClass.TransportTruncation);
            }
            return EmptyResponse();
        });
        var state = NativePolicyStateFactory.Create([(0, "A heading", null, (int?)null)]);

        var observation = await new ReasoningPreservingHeadingHarness(model, maxTransientRetries: 1)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(model.Requests[0].RequestId, model.Requests[1].RequestId);
        Assert.Equal(1, observation.CompletionStats.Completion.RetryCount);
        Assert.Equal(ReasoningCompletionFailureClass.TransportTruncation, Assert.Single(observation.CompletionStats.FailureClasses));
    }

    [Fact]
    public async Task Oversized_owned_range_splits_deterministically()
    {
        var first = true;
        var model = new ProgrammableModel(request =>
        {
            if (first)
            {
                first = false;
                throw Completion(ReasoningCompletionFailureClass.ProviderOutputLimit);
            }
            return EmptyResponse();
        });
        var state = NativePolicyStateFactory.Create([(0, "ABCD", null, (int?)null)]);

        var observation = await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal(3, model.Requests.Count);
        Assert.All(model.Requests, request => Assert.Equal("p[0]", request.OwnedOutputScope.CanonicalSourceId));
        Assert.Equal([(0, 4), (0, 2), (2, 4)],
            model.Requests.Select(request => (request.OwnedOutputScope.OwnedStart, request.OwnedOutputScope.OwnedEnd)));
        Assert.Equal(1, observation.CompletionStats.Completion.RangeSplitCount);
        Assert.Equal(2, observation.CompletionStats.Completion.SuccessfulCompletionCount);
        Assert.Equal(1, observation.CompletionStats.Completion.FailedCompletionCount);
    }

    [Fact]
    public async Task Local_span_is_mapped_from_a_nonzero_owned_range()
    {
        var first = true;
        var model = new ProgrammableModel(request =>
        {
            if (first)
            {
                first = false;
                throw Completion(ReasoningCompletionFailureClass.ProviderOutputLimit);
            }

            return request.OwnedOutputScope.OwnedStart == 2
                ? new ReasoningModelResponse(
                    [new ReasoningModelHeadingProposal
                    {
                        Start = 0,
                        End = 1,
                        SemanticRole = "CONTENT_HEADING",
                    }],
                    [])
                : EmptyResponse();
        });
        var state = NativePolicyStateFactory.Create([(0, "ABCD", null, (int?)null)]);

        var observation = await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        var validated = Assert.Single(observation.Validated.Where(item => item.Accepted));
        Assert.Equal("p[0]", validated.Proposal.SourceId);
        Assert.Equal(new StructuralSpan(2, 3), validated.Proposal.HeadingSpan);
        Assert.Equal("C", validated.Proposal.Text);
    }

    [Fact]
    public async Task Full_document_remains_visible_while_owned_range_shrinks()
    {
        var first = true;
        var model = new ProgrammableModel(request =>
        {
            if (first)
            {
                first = false;
                throw Completion(ReasoningCompletionFailureClass.ProviderOutputLimit);
            }
            return EmptyResponse();
        });
        var state = NativePolicyStateFactory.Create([
            (0, "one", null, (int?)null),
            (1, "two", null, (int?)null),
            (2, "three", null, (int?)null),
            (3, "four", null, (int?)null),
        ]);

        await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ReasoningPreservingShadow);

        Assert.All(model.Requests, request => Assert.Equal(4, request.SourceOccurrenceIds.Count));
        Assert.All(model.Requests, request => Assert.Equal(1, request.OwnedSourceOccurrenceIds.Count));
    }

    [Fact]
    public void Visible_ranges_may_overlap_but_owned_ranges_do_not()
    {
        var state = NativePolicyStateFactory.Create(Enumerable.Range(0, 8)
            .Select(index => (index, $"paragraph {index} with enough text", (int?)null, (int?)null)));
        var pack = ReasoningContextBuilder.Build(state.Source, state, maxContextCharacters: 100, windowCharacters: 2_400, overlapOccurrences: 2);

        Assert.True(pack.Segments.Count > 1);
        Assert.Contains(pack.Segments.Zip(pack.Segments.Skip(1)), pair =>
            pair.First.SourceOccurrenceIds.Intersect(pair.Second.SourceOccurrenceIds, StringComparer.Ordinal).Any());
        var owned = pack.Segments.SelectMany(segment => segment.OwnedSourceOccurrenceIds).ToArray();
        Assert.Equal(owned.Length, owned.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_model_readable_occurrence_has_exactly_one_owner()
    {
        var state = NativePolicyStateFactory.Create(Enumerable.Range(0, 12)
            .Select(index => (index, $"paragraph {index} with enough text", (int?)null, (int?)null)));
        var pack = ReasoningContextBuilder.Build(state.Source, state, maxContextCharacters: 100, windowCharacters: 2_400, overlapOccurrences: 2);
        var owned = pack.Segments.SelectMany(segment => segment.OwnedSourceOccurrenceIds).ToArray();

        Assert.Equal(pack.Occurrences.Count, owned.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(pack.Occurrences.Count, owned.Length);
        Assert.Equal(pack.Occurrences.Select(item => item.SourceOccurrenceId).Order(), owned.Order());
    }

    [Fact]
    public async Task Owned_range_mismatch_is_fail_closed()
    {
        var model = new ProgrammableModel(_ => new ReasoningModelResponse(
            [new ReasoningModelHeadingProposal { Start = 90, End = 91, SemanticRole = "CONTENT_HEADING" }],
            []));
        var state = NativePolicyStateFactory.Create([(0, "A", null, (int?)null)]);

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() => new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));

        Assert.Equal(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid, exception.FailureClass);
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public async Task Proposal_outside_owned_range_is_rejected_fail_closed()
    {
        var state = NativePolicyStateFactory.Create([(0, "A", null, (int?)null)]);
        var model = new ProgrammableModel(_ => new ReasoningModelResponse(
            [new ReasoningModelHeadingProposal { Start = 1, End = 2, SemanticRole = "CONTENT_HEADING" }],
            []));

        var exception = await Assert.ThrowsAsync<ReasoningCompletionException>(() =>
            new ReasoningPreservingHeadingHarness(model)
                .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling));

        Assert.Contains("reasoning-response-local-span-invalid", exception.Message);
    }

    [Fact]
    public void Source_span_materializes_against_raw_source_facts()
    {
        var state = NativePolicyStateFactory.Create([(0, "prefix Heading suffix", null, (int?)null)]);
        var proposal = new ReasoningHeadingProposal
        {
            SourceId = "p[0]",
            HeadingSpan = new StructuralSpan(7, 14),
            Text = "",
            SemanticRole = "CONTENT_HEADING",
            ProposedLevel = 1,
        };

        var result = ReasoningProposalMaterializer.Materialize(state.Source, state, [proposal]);

        var element = Assert.Single(result.Structure.Elements);
        Assert.Equal("Heading", element.Text);
        Assert.Equal(new StructuralSpan(7, 14), Assert.Single(element.Sources).Span);
    }

    [Fact]
    public void Provider_text_is_optional_when_span_is_valid()
    {
        var response = ReasoningModelResponseParser.Parse(
            "{\"headings\":[{\"start\":7,\"end\":14,\"semanticRole\":\"CONTENT_HEADING\",\"proposedLevel\":1}],\"decisionEvidence\":[]}");

        Assert.Equal(7, Assert.Single(response.Headings).Start);
    }

    [Fact]
    public async Task Retry_preserves_task_source_context_and_configuration()
    {
        var first = true;
        var model = new ProgrammableModel(request =>
        {
            if (first)
            {
                first = false;
                throw Completion(ReasoningCompletionFailureClass.Timeout);
            }
            return EmptyResponse();
        });
        var state = NativePolicyStateFactory.Create([(0, "A", null, (int?)null)]);

        await new ReasoningPreservingHeadingHarness(model, maxTransientRetries: 1)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(model.Requests[0].DocumentId, model.Requests[1].DocumentId);
        Assert.Equal(model.Requests[0].SemanticPassId, model.Requests[1].SemanticPassId);
        Assert.Equal(model.Requests[0].ConfigurationSignature, model.Requests[1].ConfigurationSignature);
        Assert.Equal(model.Requests[0].SourceOccurrenceIds, model.Requests[1].SourceOccurrenceIds);
        Assert.DoesNotContain("gold", model.Requests[1].UserPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_attempts_and_retries_are_counted()
    {
        var first = true;
        var model = new ProgrammableModel(_ =>
        {
            if (first)
            {
                first = false;
                throw Completion(ReasoningCompletionFailureClass.TransportTruncation);
            }
            return EmptyResponse();
        });
        var state = NativePolicyStateFactory.Create([(0, "A", null, (int?)null)]);

        var result = await new ReasoningPreservingHeadingHarness(model, maxTransientRetries: 1)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal(2, result.CompletionStats.Completion.AttemptCount);
        Assert.Equal(1, result.CompletionStats.Completion.FailedCompletionCount);
        Assert.Equal(1, result.CompletionStats.Completion.SuccessfulCompletionCount);
        Assert.Equal(2, model.ProviderCalls);
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
        ConfigurationSignature = "config",
    };

    private static ReasoningModelResponse EmptyResponse() => new([], []);

    private static ReasoningCompletionException Completion(string failureClass) => new(
        failureClass,
        failureClass,
        new ReasoningCompletionTelemetry { FailureClass = failureClass },
        null);

    private static OpenRouterReasoningSemanticModel ProviderModel(string body)
    {
        var options = new RemoteInferenceOptions { ApiKey = "test-key" };
        return new OpenRouterReasoningSemanticModel(
            options,
            new HttpClient(new FixedResponseHandler(body)));
    }

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

    private sealed class FixedResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
