using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class Qwen9BProviderExecutionTests
{
    [Fact]
    public async Task ProviderRouteMetadata_UsesOnlyCeilingCapableRoutes()
    {
        var handler = new JsonHandler(new
        {
            data = new
            {
                endpoints = new object[]
                {
                    new { provider_name = "Good", tag = "good/fp8", context_length = 262144, max_completion_tokens = 65536,
                        supported_parameters = new[] { "reasoning", "max_tokens", "response_format", "structured_outputs" },
                        uptime_last_30m = 99.0, latency_last_30m = new { p50 = 10, p99 = 20 }, throughput_last_30m = new { p50 = 30 }, supports_implicit_caching = false },
                    new { provider_name = "FastButNoReasoning", tag = "fast", context_length = 262144, max_completion_tokens = 65536,
                        supported_parameters = new[] { "max_tokens", "response_format", "structured_outputs" },
                        uptime_last_30m = 100.0, latency_last_30m = new { p50 = 1, p99 = 2 }, throughput_last_30m = new { p50 = 100 }, supports_implicit_caching = true },
                }
            }
        });
        using var http = new HttpClient(handler);
        var result = await OpenRouterProviderRouteResolver.ResolveAsync(new RemoteInferenceOptions
        {
            ApiKey = "k", Model = "qwen/qwen3.5-9b",
        }, http);

        Assert.True(result.Available);
        Assert.Equal(2, result.Routes.Count);
        Assert.Single(result.Routes.Where(x => x.SupportsCeilingRequest));
        Assert.Equal("good", result.Routes.Single(x => x.SupportsCeilingRequest).Route);
        Assert.Equal("good/fp8", result.Routes.Single(x => x.SupportsCeilingRequest).EndpointTag);
    }

    [Fact]
    public void ReopenExecutionTerminals_PreservesHistoricalFailureEvidence_AndNotSuccess()
    {
        var tree = new SegmentRecoveryTree("D", new[] { 20 }, minimumSegmentCharacters: 1, minimumVisibleContextCharacters: 0);
        var root = tree.Get(tree.RootSegmentId);
        tree.RecordFailure(root.SegmentId, ReasoningCompletionFailureClass.ProviderTotalTimeout);
        tree.RecordFailure(root.SegmentId, ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout);
        var split = tree.Split(root.SegmentId, haloOccurrences: 0);
        tree.MarkSuccess(split.Left.SegmentId, "rq", "rs");
        tree.MarkFailedTerminal(split.Right.SegmentId, ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid);
        tree.Get(split.Right.SegmentId).Attempts = 1;

        Assert.Equal(1, tree.ReopenFailedTerminalsForExecutionChange());
        Assert.Equal(SegmentRecoveryState.Success, tree.Get(split.Left.SegmentId).Status);
        Assert.Equal(SegmentRecoveryState.StaleTerminal, tree.Get(split.Right.SegmentId).Status);
        Assert.Equal(1, tree.Get(split.Right.SegmentId).HistoricalAttempts);
        Assert.Contains(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid, tree.Get(split.Right.SegmentId).FailureHistory);
    }

    private sealed class JsonHandler(object payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
    }
}
