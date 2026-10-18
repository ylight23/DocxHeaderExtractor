using System.Net;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
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
        Assert.Equal("{}", await transport.BoundaryCutAsync("Return JSON.", "{}", expectedItemCount: 16));
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

    [Fact]
    public async Task Hierarchy_uses_the_rich_verdict_budget_and_reports_terminal_metadata_through_production_transport()
    {
        var lines = Enumerable.Range(0, 16).Select(i => new SemanticSourceAtom("L" + i, "S" + i, i, 1, i, 0, "Section " + i)).ToArray();
        var bound = lines.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text,
            "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var evidence = new HeadingPlacementEvidence("{}", new Dictionary<string, string>())
        { Source = new(lines, new Dictionary<string, DocxHeaderExtractor.DocumentProcessing.Routing.HeadingAliasState>()) };
        using var handler = new FakeOpenRouter((body, _) =>
        {
            var packet = body["messages"]![1]!["content"]!.GetValue<string>();
            var verify = JsonNode.Parse(packet)!["stage"]!.GetValue<string>() == "HIERARCHY_VERIFY";
            Assert.Equal(EvidenceHeadingPlacement.OutputBudget(verify, 16), body["max_tokens"]!.GetValue<int>());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = HierarchyTestProtocol.Reply(packet) }, "stop"));
        }, autoVerify: false);
        using var http = new HttpClient(handler);
        using var transport = new OpenRouterInferenceTransport(http, new RemoteInferenceOptions { ApiKey = "sk-test" });
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, transport, CancellationToken.None, evidence);
        Assert.Equal(16, result.Observation.VerifiedCount);
        Assert.Equal(0, result.Observation.UnresolvedCount);
        Assert.Equal(2, handler.Posts);
        Assert.All(result.Observation.Calls, c => Assert.Equal("stop", c.FinishReason));
        Assert.Equal(16896, result.Observation.Calls.Single(c => c.Phase == "HIERARCHY_VERIFY").MaxOutputTokens);
    }
}
