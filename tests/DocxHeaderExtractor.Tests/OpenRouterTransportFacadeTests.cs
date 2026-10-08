using System.Net;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification;

namespace DocxHeaderExtractor.Tests;

public sealed class OpenRouterTransportFacadeTests
{
    [Fact]
    public void Production_facade_exposes_no_raw_observation_or_forced_tool_transport()
    {
        var publicMethods = typeof(OpenRouterInferenceTransport).GetMethods()
            .Where(method => method.DeclaringType == typeof(OpenRouterInferenceTransport))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ExecuteObservedAsync", publicMethods);
        Assert.DoesNotContain("ExecuteObservedUnconstrainedAsync", publicMethods);
        Assert.DoesNotContain("ExecuteToolCallAsync", publicMethods);
        Assert.DoesNotContain("ExecuteAsync", publicMethods);
    }

    [Fact]
    public void Production_infrastructure_assembly_exports_no_qualification_transport_types()
    {
        var exported = typeof(OpenRouterInferenceTransport).Assembly.GetExportedTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(nameof(OpenRouterQualificationTransport), exported);
        Assert.DoesNotContain(nameof(OpenRouterExecutionObservation), exported);
        Assert.NotEqual(typeof(OpenRouterInferenceTransport).Assembly, typeof(OpenRouterQualificationTransport).Assembly);
    }

    [Fact]
    public void Qualification_facade_owns_raw_observation_and_forced_tool_transport()
    {
        var publicMethods = typeof(OpenRouterQualificationTransport).GetMethods()
            .Where(method => method.DeclaringType == typeof(OpenRouterQualificationTransport))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(OpenRouterQualificationTransport.ExecuteObservedAsync), publicMethods);
        Assert.Contains(nameof(OpenRouterQualificationTransport.ExecuteObservedUnconstrainedAsync), publicMethods);
        Assert.Contains(nameof(OpenRouterQualificationTransport.ExecuteToolCallAsync), publicMethods);
    }

    [Fact]
    public void Runtime_response_has_only_content_and_finish_reason_and_retires_the_old_public_DTO()
    {
        Assert.Equal(["Content", "FinishReason"], typeof(FrozenInferenceResponse).GetProperties()
            .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Null(typeof(FrozenInferenceResponse).Assembly.GetType(
            "DocxHeaderExtractor.DocumentProcessing.Inference.FrozenInferenceResult"));
        Assert.Equal(typeof(Task<FrozenInferenceResponse>), typeof(IFrozenInferenceTransport)
            .GetMethod(nameof(IFrozenInferenceTransport.ExecuteFrozenRequestAsync))!.ReturnType);
        Assert.Equal("{\"Content\":\"{}\",\"FinishReason\":null}",
            JsonSerializer.Serialize(new FrozenInferenceResponse("{}", null)));
    }

    [Fact]
    public void Qualification_observation_keeps_the_full_diagnostic_shape_outside_document_processing()
    {
        Assert.Equal(["Content", "FinishReason", "RawSse", "RetryCount", "SseEventCount", "Usage"],
            typeof(OpenRouterExecutionObservation).GetProperties().Select(property => property.Name)
                .Order(StringComparer.Ordinal).ToArray());
        Assert.NotEqual(typeof(FrozenInferenceResponse).Assembly, typeof(OpenRouterExecutionObservation).Assembly);
    }

    [Theory]
    [InlineData("stop", false)]
    [InlineData("length", false)]
    [InlineData("stop", true)]
    public async Task Frozen_runtime_and_qualification_keep_identical_bytes_content_and_finish_reason(
        string finishReason, bool transientFailure)
    {
        var body = Encoding.UTF8.GetBytes("{\"frozen\":\"<>& Tiêu đề\",\"stream\":true}");
        var sse = Sse(finishReason);
        using var runtimeHandler = new FrozenReplyHandler(sse, transientFailure);
        using var observedHandler = new FrozenReplyHandler(sse, transientFailure);
        using var qualificationFrozenHandler = new FrozenReplyHandler(sse, transientFailure);
        using var runtimeHttp = new HttpClient(runtimeHandler);
        using var observedHttp = new HttpClient(observedHandler);
        using var qualificationFrozenHttp = new HttpClient(qualificationFrozenHandler);
        var options = new RemoteInferenceOptions { ApiKey = "mock-only", TransientRequestRetries = 1 };
        using var runtime = new OpenRouterInferenceTransport(runtimeHttp, options);
        using var observed = new OpenRouterQualificationTransport(observedHttp, options);
        using var qualificationFrozen = new OpenRouterQualificationTransport(qualificationFrozenHttp, options);
        runtime.RetryWait = observed.RetryWait = qualificationFrozen.RetryWait = (_, _) => Task.CompletedTask;

        var response = await runtime.ExecuteFrozenRequestAsync(body, 32768, "Return JSON.", "{}");
        var observation = await observed.ExecuteObservedAsync(body, 32768, "Return JSON.", "{}");
        var qualificationResponse = await qualificationFrozen.ExecuteFrozenRequestAsync(body, 32768, "Return JSON.", "{}");

        Assert.Equal("{\"ok\":true}", response.Content);
        Assert.Equal(finishReason, response.FinishReason);
        Assert.Equal(response, qualificationResponse);
        Assert.Equal(response.Content, observation.Content);
        Assert.Equal(response.FinishReason, observation.FinishReason);
        Assert.Equal(sse, observation.RawSse);
        Assert.True(observation.SseEventCount > 0);
        Assert.Equal(transientFailure ? 1 : 0, observation.RetryCount);
        Assert.Equal(13, observation.Usage!.Value.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(7, observation.Usage.Value.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(3, observation.Usage.Value.GetProperty("completion_tokens_details")
            .GetProperty("reasoning_tokens").GetInt32());
        foreach (var handler in new[] { runtimeHandler, observedHandler, qualificationFrozenHandler })
        {
            Assert.Equal(transientFailure ? 2 : 1, handler.Bodies.Count);
            Assert.All(handler.Bodies, bytes => Assert.Equal(body, bytes));
        }
    }

    private static string Sse(string finishReason) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { content = " {\"ok\":true} " }, finish_reason = (string?)null } },
        }) + "\n\n" +
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { }, finish_reason = finishReason } },
            usage = new { prompt_tokens = 13, completion_tokens = 7, completion_tokens_details = new { reasoning_tokens = 3 } },
        }) + "\n\ndata: [DONE]\n\n";

    private sealed class FrozenReplyHandler(string sse, bool transientFailure) : HttpMessageHandler
    {
        public List<byte[]> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return transientFailure && Bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }
}
