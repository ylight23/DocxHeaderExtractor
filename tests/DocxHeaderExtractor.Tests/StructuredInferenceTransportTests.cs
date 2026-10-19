using System.Net;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
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

    [Theory]
    [InlineData("stop", 32768, 16896)]
    [InlineData("stop", 8192, 8192)]
    [InlineData("length", 32768, 16896)]
    [InlineData("length", 8192, 8192)]
    public async Task Hierarchy_preserves_budget_metadata_and_finish_authority_through_the_lane_lease(
        string finish, int configuredCap, int expectedCap)
    {
        var lines = Enumerable.Range(0, 16).Select(i => new SemanticSourceAtom("L" + i, "S" + i, i, 1, i, 0, "Section " + i)).ToArray();
        var bound = lines.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text,
            "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var evidence = new HeadingPlacementEvidence("{}", new Dictionary<string, string>())
        { Source = new(lines, new Dictionary<string, DocxHeaderExtractor.DocumentProcessing.Routing.HeadingAliasState>()) };
        using var handler = new FakeOpenRouter((body, _) =>
        {
            var packet = body["messages"]![1]!["content"]!.GetValue<string>();
            var verify = JsonNode.Parse(packet)!["stage"]!.GetValue<string>() is "HIERARCHY_VERIFY" or "HIERARCHY_REVERIFY";
            Assert.Equal(verify ? expectedCap : EvidenceHeadingPlacement.OutputBudget(false, 16), body["max_tokens"]!.GetValue<int>());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = HierarchyTestProtocol.Reply(packet) }, verify ? finish : "stop"));
        }, autoVerify: false);
        using var http = new HttpClient(handler);
        using var transport = new OpenRouterInferenceTransport(http, new RemoteInferenceOptions { ApiKey = "sk-test", MaxOutputTokens = configuredCap });
        using var leaseBound = LeaseBoundInferenceTransport.Bind(transport, new LaneExecutionLease());
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, leaseBound, CancellationToken.None, evidence);
        var completed = finish == "stop";
        Assert.Equal(completed ? 16 : 0, result.Observation.VerifiedCount);
        Assert.Equal(completed ? 16 : 0, result.Observation.PlacedCount);
        Assert.Equal(completed ? 0 : 16, result.Observation.UnresolvedCount);
        Assert.Equal(completed ? 2 : 8, handler.Posts);
        Assert.All(result.Observation.Calls.Where(c => c.Phase is "HIERARCHY_VERIFY" or "HIERARCHY_REVERIFY"), c =>
        {
            Assert.Equal(finish, c.FinishReason);
            Assert.Equal(16896, c.RequestedMaxOutputTokens);
            Assert.Equal(expectedCap, c.MaxOutputTokens);
            if (!completed)
            {
                Assert.Empty(c.AcceptedAliases);
                Assert.Contains("FINISH_REASON_LENGTH", c.ProtocolErrors);
            }
        });
    }
}
