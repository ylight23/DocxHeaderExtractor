using System.Net;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class StructuredInferenceTransportTests
{
    [Theory]
    [InlineData("stop", 32768, 16896)]
    [InlineData("length", 8192, 8192)]
    [InlineData("content_filter", 32768, 16896)]
    public async Task Explicit_completion_cap_and_finish_reason_survive_the_real_SSE_transport_without_content_retries(
        string finish, int configuredCap, int expectedCap)
    {
        using var handler = new FakeOpenRouter((body, _) =>
        {
            Assert.Equal(expectedCap, body["max_tokens"]!.GetValue<int>());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = "{}" }, finish));
        }, autoVerify: false);
        using var http = new HttpClient(handler);
        using var transport = new OpenRouterInferenceTransport(http, new RemoteInferenceOptions
        { ApiKey = "sk-test", MaxOutputTokens = configuredCap });
        var reply = await transport.CompleteStructuredAsync("Return one JSON object.", "{}", 16896);
        Assert.Equal("{}", reply.Content);
        Assert.Equal(finish, reply.FinishReason);
        Assert.Equal(expectedCap, reply.MaxOutputTokens);
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task Legacy_boundary_calls_keep_the_existing_per_item_budget()
    {
        using var handler = new FakeOpenRouter((body, _) =>
        {
            Assert.Equal(96 + 16 * 128, body["max_tokens"]!.GetValue<int>());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = "{}" }, "stop"));
        }, autoVerify: false);
        using var http = new HttpClient(handler);
        using var transport = new OpenRouterInferenceTransport(http, new RemoteInferenceOptions { ApiKey = "sk-test" });
        Assert.Equal("{}", await transport.CompleteAsync("Return JSON.", "{}", expectedItemCount: 16));
    }

    [Fact]
    public async Task Production_agent_route_wrapper_preserves_the_structured_completion_capability()
    {
        var factory = new InferenceTransportFactory(new InferenceProviderSelection
        {
            Backend = InferenceBackend.OpenRouter,
            Remote = new RemoteInferenceOptions { ApiKey = "sk-test", OpenRouterProviderRoute = RemoteInferenceOptions.DefaultProviderRoute }
        });
        using var transport = await factory.CreateAgentRouteAsync();
        Assert.IsAssignableFrom<IStructuredInferenceTransport>(transport);
    }
}
